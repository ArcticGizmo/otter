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

    // Otter's own Slack app, set up for public distribution. The Client ID is not a secret — it's safe
    // to ship in the binary — and the PKCE flow below needs no client secret. The redirect is a custom
    // URI scheme (a "desktop redirect"), which is what lets a distributed app skip the HTTPS-redirect
    // requirement that blocks http://localhost.
    // TODO: paste the Client ID from api.slack.com/apps → Otter → Basic Information before shipping.
    const string ClientId = "5734148946098.11276600362966";

    // Slack redirects the browser here over HTTPS — this is the URL registered on the Slack app and the
    // one the token exchange below must echo back. The page (docs/connected.html, served via GitHub
    // Pages) shows a "connected" confirmation and bounces the query string into otter://callback, which
    // launches Otter; the otter:// scheme itself is never registered with Slack.
    // TODO: set this to where docs/connected.html is published before shipping.
    const string RedirectUri = "https://YOUR-GITHUB-USERNAME.github.io/otter/connected.html";

    // The OAuth callback arrives out-of-band (Windows relaunches us via the otter:// scheme, see
    // IpcServer), so the in-flight flow parks a completion source here for the callback to resolve.
    static TaskCompletionSource<Uri>? _pendingCallback;
    static readonly object _pendingLock = new();

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
    /// Delivers an <c>otter://callback?...</c> URL (received via <see cref="IpcServer"/> or our own launch
    /// args) to the OAuth flow currently awaiting it. Safe to call from any thread; a no-op when no flow
    /// is in progress.
    /// </summary>
    public static void DeliverCallbackUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            lock (_pendingLock) _pendingCallback?.TrySetResult(uri);
    }

    /// <summary>
    /// Opens the browser to Slack's auth page, waits for the <c>otter://</c> redirect to be handed back,
    /// exchanges the code via PKCE (no client secret required), and returns (userToken, teamName).
    /// </summary>
    public static async Task<(string Token, string TeamName)> RunOAuthFlowAsync(CancellationToken ct = default)
    {
        var state = Guid.NewGuid().ToString("N");

        // PKCE: generate verifier + challenge so no client secret is needed
        var verifierBytes = new byte[32];
        RandomNumberGenerator.Fill(verifierBytes);
        var codeVerifier  = Base64UrlEncode(verifierBytes);
        var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        var authUrl = "https://slack.com/oauth/v2/authorize"
            + $"?client_id={Uri.EscapeDataString(ClientId)}"
            + "&user_scope=users.profile%3Awrite%2Cusers.profile%3Aread%2Cemoji%3Aread"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + $"&state={state}"
            + $"&code_challenge={codeChallenge}"
            + "&code_challenge_method=S256";

        // Park a completion source for the callback (delivered via DeliverCallbackUrl). Only one connect
        // can be pending at a time, so cancel any earlier attempt that's somehow still waiting.
        var tcs = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pendingCallback?.TrySetCanceled();
            _pendingCallback = tcs;
        }

        // Open the user's browser
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        // Wait up to 5 minutes for the redirect
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        Uri callback;
        try
        {
            callback = await tcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Slack OAuth timed out — the browser sign-in wasn't completed in time.");
        }
        finally
        {
            lock (_pendingLock) { if (_pendingCallback == tcs) _pendingCallback = null; }
        }

        var qs = ParseQuery(callback.Query);

        if (qs.TryGetValue("error", out var err))
            throw new InvalidOperationException($"Slack returned an error: {err}");

        if (!qs.TryGetValue("state", out var returnedState) || returnedState != state)
            throw new InvalidOperationException("OAuth state mismatch — possible CSRF attempt.");

        var code = qs.TryGetValue("code", out var c) && c.Length > 0
            ? c : throw new InvalidOperationException("No code in OAuth callback.");

        // Exchange code → token using PKCE verifier instead of client secret
        var tokenResp = await Http.PostAsync(
            "https://slack.com/api/oauth.v2.access",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = ClientId,
                ["code"]          = code,
                ["redirect_uri"]  = RedirectUri,
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

    // Parses a URL query string ("?a=1&b=2") into a map, URL-decoding keys and values. Used instead of
    // HttpListener's QueryString now that the callback arrives as a bare otter:// URI.
    static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) result[Uri.UnescapeDataString(part)] = "";
            else        result[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return result;
    }

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
