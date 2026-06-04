using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace Sleams;

static class SlackClient
{
    static readonly HttpClient Http = new();
    const int OAuthPort = 47891;

    // ── Status ────────────────────────────────────────────────────────────────

    // Expiration is a Unix timestamp (seconds); 0 means no expiry.
    public record SlackStatus(string Text, string Emoji, long Expiration = 0);

    public static async Task<SlackStatus> GetStatusAsync(string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://slack.com/api/users.profile.get")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        };
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "unknown";
            throw new InvalidOperationException($"Slack API error: {error}");
        }

        if (!root.TryGetProperty("profile", out var profile))
            return new SlackStatus("", "");

        var text  = profile.TryGetProperty("status_text",       out var t) ? t.GetString() ?? ""  : "";
        var emoji = profile.TryGetProperty("status_emoji",      out var e) ? e.GetString() ?? ""  : "";
        var exp   = profile.TryGetProperty("status_expiration", out var x) ? x.GetInt64()         : 0L;

        // If the expiry has already passed, treat it as no expiry so we don't restore a stale status
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (exp > 0 && exp <= now) exp = 0;

        return new SlackStatus(text, emoji, exp);
    }

    public static async Task SetStatusAsync(string token, string text, string emoji, long expiration = 0)
    {
        var body = JsonSerializer.Serialize(new
        {
            profile = new { status_text = text, status_emoji = emoji, status_expiration = expiration }
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/users.profile.set")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        };

        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        await EnsureSlackOkAsync(resp);
    }

    public static Task ClearStatusAsync(string token) =>
        SetStatusAsync(token, string.Empty, string.Empty);

    // ── OAuth ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the browser to Slack's auth page, waits for the local redirect,
    /// exchanges the code, and returns (userToken, teamName).
    /// </summary>
    public static async Task<(string Token, string TeamName)> RunOAuthFlowAsync(
        string clientId, string clientSecret, CancellationToken ct = default)
    {
        var state = Guid.NewGuid().ToString("N");
        var redirectUri = $"http://localhost:{OAuthPort}/callback";

        var authUrl = "https://slack.com/oauth/v2/authorize"
            + $"?client_id={Uri.EscapeDataString(clientId)}"
            + "&user_scope=users.profile%3Awrite%2Cusers.profile%3Aread"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{OAuthPort}/");
        listener.Start();

        // Open the user's browser
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        // Wait up to 5 minutes for the redirect
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            listener.Stop();
            throw new TimeoutException("Slack OAuth timed out — the browser was not completed in time.");
        }

        // Capture query string before stopping the listener (Stop() disposes the request)
        var qs = context.Request.QueryString;

        // Respond to browser, then stop
        try
        {
            var html = Encoding.UTF8.GetBytes(
                "<html><head><title>Sleams</title></head><body style='font-family:sans-serif;padding:40px'>" +
                "<h2>✅ Connected to Slack!</h2><p>You can close this tab and return to Sleams.</p></body></html>");
            context.Response.ContentType     = "text/html; charset=utf-8";
            context.Response.ContentLength64 = html.Length;
            await context.Response.OutputStream.WriteAsync(html);
            context.Response.Close();
        }
        catch { /* browser closed before we could respond — non-fatal */ }
        finally { listener.Stop(); }

        if (qs["error"] is string err)
            throw new InvalidOperationException($"Slack returned an error: {err}");

        if (qs["state"] != state)
            throw new InvalidOperationException("OAuth state mismatch — possible CSRF attempt.");

        var code = qs["code"] ?? throw new InvalidOperationException("No code in OAuth callback.");

        // Exchange code → token
        var tokenResp = await Http.PostAsync(
            "https://slack.com/api/oauth.v2.access",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = clientId,
                ["client_secret"] = clientSecret,
                ["code"]          = code,
                ["redirect_uri"]  = redirectUri,
            }));

        await EnsureSlackOkAsync(tokenResp);

        using var doc  = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync());
        var root       = doc.RootElement;
        var token      = root.GetProperty("authed_user").GetProperty("access_token").GetString()!;
        var teamName   = root.GetProperty("team").GetProperty("name").GetString()!;

        return (token, teamName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static async Task EnsureSlackOkAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            throw new InvalidOperationException($"Slack API error: {error}");
        }
    }
}
