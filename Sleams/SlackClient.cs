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

    public static async Task SetStatusAsync(string token, string text, string emoji)
    {
        var body = JsonSerializer.Serialize(new
        {
            profile = new { status_text = text, status_emoji = emoji, status_expiration = 0 }
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
            + "&user_scope=users.profile%3Awrite"
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

        // Respond to browser before stopping the listener
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

        var qs = context.Request.QueryString;

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
