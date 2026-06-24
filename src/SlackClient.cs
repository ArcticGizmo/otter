using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace Otter;

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

    // ── Emoji ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the workspace's custom emoji via the <c>emoji.list</c> method (requires the
    /// <c>emoji:read</c> scope). Returns a map of emoji name (no colons) → image URL. Built-in
    /// standard emoji aren't returned by Slack; only custom uploads are. Aliases (values of the
    /// form <c>alias:other</c>) are followed to the underlying image, and any that resolve to a
    /// standard emoji (no URL) are dropped.
    /// </summary>
    public static async Task<Dictionary<string, string>> GetEmojiListAsync(string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://slack.com/api/emoji.list")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        };
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            throw new InvalidOperationException($"Slack API error: {error}");
        }

        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("emoji", out var emoji) && emoji.ValueKind == JsonValueKind.Object)
            foreach (var p in emoji.EnumerateObject())
                raw[p.Name] = p.Value.GetString() ?? "";

        // Resolve alias chains down to a real image URL; drop anything that doesn't end at one.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in raw.Keys)
        {
            var url = ResolveEmoji(raw, name, 0);
            if (url is not null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                result[name] = url;
        }
        return result;
    }

    // Follows alias: references to the underlying value. Returns null on a missing target,
    // a standard-emoji alias (no URL), or a suspiciously deep chain.
    static string? ResolveEmoji(Dictionary<string, string> map, string name, int depth)
    {
        if (depth > 10) return null;
        if (!map.TryGetValue(name, out var value)) return null;
        const string aliasPrefix = "alias:";
        return value.StartsWith(aliasPrefix, StringComparison.Ordinal)
            ? ResolveEmoji(map, value[aliasPrefix.Length..], depth + 1)
            : value;
    }

    // ── OAuth ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the browser to Slack's auth page, waits for the local redirect,
    /// exchanges the code via PKCE (no client secret required), and returns (userToken, teamName).
    /// </summary>
    public static async Task<(string Token, string TeamName)> RunOAuthFlowAsync(
        string clientId, CancellationToken ct = default)
    {
        var state = Guid.NewGuid().ToString("N");
        var redirectUri = $"http://localhost:{OAuthPort}/callback";

        // PKCE: generate verifier + challenge so no client secret is needed
        var verifierBytes = new byte[32];
        RandomNumberGenerator.Fill(verifierBytes);
        var codeVerifier  = Base64UrlEncode(verifierBytes);
        var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        var authUrl = "https://slack.com/oauth/v2/authorize"
            + $"?client_id={Uri.EscapeDataString(clientId)}"
            + "&user_scope=users.profile%3Awrite%2Cusers.profile%3Aread%2Cemoji%3Aread"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&state={state}"
            + $"&code_challenge={codeChallenge}"
            + "&code_challenge_method=S256";

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
                "<html><head><title>Otter</title></head><body style='font-family:sans-serif;padding:40px'>" +
                "<h2>✅ Connected to Slack!</h2><p>You can close this tab and return to Otter.</p></body></html>");
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

        // Exchange code → token using PKCE verifier instead of client secret
        var tokenResp = await Http.PostAsync(
            "https://slack.com/api/oauth.v2.access",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = clientId,
                ["code"]          = code,
                ["redirect_uri"]  = redirectUri,
                ["code_verifier"] = codeVerifier,
            }));

        await EnsureSlackOkAsync(tokenResp);

        using var doc  = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync());
        var root       = doc.RootElement;
        var token      = root.GetProperty("authed_user").GetProperty("access_token").GetString()!;
        var teamName   = root.GetProperty("team").GetProperty("name").GetString()!;

        return (token, teamName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
