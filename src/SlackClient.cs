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
    // to ship in the binary — and the PKCE flow below needs no client secret.
    const string ClientId = "5734148946098.11276600362966";

    // Slack redirects the browser to this otter:// custom scheme, registered on the Slack app and
    // handled here (Windows relaunches Otter with the URL; see UrlProtocol/IpcServer/Program, which hand
    // it to DeliverCallbackUrl below). A custom-scheme redirect is what Slack calls a "desktop redirect":
    // it marks Otter as a public client, which is what lets the oauth.v2.user.access calls below refresh
    // tokens WITHOUT a client secret — a shipped binary can't safely carry one. (A public app can't
    // register an http(s)/localhost redirect, so the custom scheme is the only desktop-redirect option.)
    const string RedirectUri = "otter://callback";

    // The OAuth callback arrives out-of-band (Windows relaunches us via the otter:// scheme), so the
    // in-flight flow parks a completion source here for the callback to resolve.
    static TaskCompletionSource<Uri>? _pendingCallback;
    static readonly object _pendingLock = new();

    // Serialises token refreshes. Slack refresh tokens are single-use — two concurrent refreshes
    // would race, and the loser would present an already-revoked refresh token. Callers re-check
    // state after acquiring this, so only the first of a burst actually hits Slack.
    static readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Refresh a little before the real expiry so an in-flight status update never races the cutoff.
    static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

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
        ThrowIfSlackError(root);

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
        ThrowIfSlackError(root);

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
    /// Delivers an <c>otter://callback?...</c> URL (received via the named-pipe IPC or our own launch
    /// args) to the OAuth flow currently awaiting it. Safe to call from any thread; a no-op when no flow
    /// is in progress.
    /// </summary>
    public static void DeliverCallbackUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            lock (_pendingLock) _pendingCallback?.TrySetResult(uri);
    }

    /// <summary>
    /// The token bundle returned by an <c>oauth.v2.user.access</c> call — both the initial code exchange
    /// and a refresh. <paramref name="RefreshToken"/>/<paramref name="ExpiresAt"/> are null when the app
    /// isn't using token rotation (a legacy non-expiring token).
    /// </summary>
    public record SlackAuth(string Token, string? RefreshToken, DateTime? ExpiresAt, string TeamName);

    /// <summary>
    /// Opens the browser to Slack's auth page, waits for the <c>otter://</c> redirect to be handed back,
    /// exchanges the code via PKCE (no client secret required), and returns the resulting token bundle.
    /// </summary>
    public static async Task<SlackAuth> RunOAuthFlowAsync(CancellationToken ct = default)
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

        // Exchange code → token using the PKCE verifier instead of a client secret. oauth.v2.user.access
        // is the user-scopes-only ("desktop") flow that, paired with the otter:// desktop redirect, lets
        // the refresh below run without a client secret.
        var tokenResp = await Http.PostAsync(
            "https://slack.com/api/oauth.v2.user.access",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = ClientId,
                ["code"]          = code,
                ["redirect_uri"]  = RedirectUri,
                ["code_verifier"] = codeVerifier,
            }));

        using var doc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync());
        ThrowIfSlackError(doc.RootElement);
        return ParseAuthResponse(doc.RootElement, fallbackRefreshToken: null);
    }

    /// <summary>
    /// Exchanges a refresh token for a fresh access token (PKCE app → no client secret, no PKCE
    /// verifier). Slack rotates the refresh token on every call, so the returned bundle's refresh
    /// token must be persisted in place of the one passed in.
    /// </summary>
    public static async Task<SlackAuth> RefreshTokenAsync(string refreshToken)
    {
        var resp = await Http.PostAsync(
            "https://slack.com/api/oauth.v2.user.access",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = ClientId,
                ["grant_type"]    = "refresh_token",
                ["refresh_token"] = refreshToken,
            }));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        ThrowIfSlackError(doc.RootElement);
        // On refresh, Slack returns the new tokens at the top level; the initial exchange nests them
        // under "authed_user". ParseAuthResponse handles both. If Slack ever omits a fresh refresh
        // token, fall back to the current one so we don't lose the ability to refresh again.
        return ParseAuthResponse(doc.RootElement, fallbackRefreshToken: refreshToken);
    }

    // Pulls the token bundle out of an oauth.v2.access response. User-token fields live under
    // "authed_user" on the initial exchange but at the top level on refresh, so try the nested object
    // first and fall back to the root. expires_in (seconds) is turned into an absolute UTC instant.
    static SlackAuth ParseAuthResponse(JsonElement root, string? fallbackRefreshToken)
    {
        var bundle = root.TryGetProperty("authed_user", out var u)
            && u.TryGetProperty("access_token", out _) ? u : root;

        var token = bundle.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Slack OAuth response had no access_token.");

        var refreshToken = bundle.TryGetProperty("refresh_token", out var rt)
            ? rt.GetString() : null;
        if (string.IsNullOrEmpty(refreshToken)) refreshToken = fallbackRefreshToken;

        DateTime? expiresAt = null;
        if (bundle.TryGetProperty("expires_in", out var ei)
            && ei.ValueKind == JsonValueKind.Number && ei.GetInt64() > 0)
            expiresAt = DateTime.UtcNow.AddSeconds(ei.GetInt64());

        var teamName = root.TryGetProperty("team", out var team)
            && team.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";

        return new SlackAuth(token, refreshToken, expiresAt, teamName);
    }

    /// <summary>
    /// Revokes a token server-side via <c>auth.revoke</c> so a disconnect actually kills the credential
    /// on Slack's side rather than just forgetting it locally. Revoking the access token deauthorises the
    /// grant, which also retires its rotating refresh token. Best-effort: callers should clear local
    /// state regardless of whether this succeeds.
    /// </summary>
    public static async Task RevokeTokenAsync(string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/auth.revoke")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        };
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        await EnsureSlackOkAsync(resp);
    }

    // ── Token rotation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a Slack API call with a valid token, refreshing first if the stored one is at/near expiry,
    /// and — should Slack still reject it as expired/invalid (e.g. clock skew, or a revocation mid
    /// session) — refreshing once more and retrying. The refreshed token, rotated refresh token, and
    /// new expiry are persisted to <paramref name="config"/>. Throws <see cref="SlackAuthException"/>
    /// if no valid token can be obtained, signalling the caller to prompt a reconnect.
    /// </summary>
    public static async Task<T> WithTokenAsync<T>(Config config, Func<string, Task<T>> call)
    {
        var token = await GetValidTokenAsync(config);
        try
        {
            return await call(token);
        }
        catch (SlackAuthException) when (UsesRotation(config))
        {
            token = await RefreshIfStaleAsync(config, observedToken: token, force: true);
            return await call(token);
        }
    }

    /// <summary>Void-returning overload of <see cref="WithTokenAsync{T}"/>.</summary>
    public static Task WithTokenAsync(Config config, Func<string, Task> call) =>
        WithTokenAsync(config, async t => { await call(t); return true; });

    /// <summary>
    /// Returns a token good for an imminent call, refreshing first if the stored one is within
    /// <see cref="RefreshSkew"/> of expiry. A no-op (returns the stored token) when the app isn't
    /// using token rotation.
    /// </summary>
    public static Task<string> GetValidTokenAsync(Config config)
    {
        if (!UsesRotation(config))
            return Task.FromResult(config.SlackToken);
        if (DateTime.UtcNow + RefreshSkew < config.SlackTokenExpiresAt!.Value)
            return Task.FromResult(config.SlackToken);
        return RefreshIfStaleAsync(config, observedToken: config.SlackToken, force: false);
    }

    static bool UsesRotation(Config config) =>
        !string.IsNullOrEmpty(config.SlackRefreshToken) && config.SlackTokenExpiresAt is not null;

    // Refreshes under the lock, unless another caller beat us to it. observedToken is the token the
    // caller saw before contending for the lock; if it no longer matches, a concurrent refresh already
    // produced a newer one and we hand that back instead of burning the (now single-use) refresh token.
    static async Task<string> RefreshIfStaleAsync(Config config, string observedToken, bool force)
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (config.SlackToken != observedToken)
                return config.SlackToken;
            if (!force && DateTime.UtcNow + RefreshSkew < config.SlackTokenExpiresAt)
                return config.SlackToken;

            SlackAuth refreshed;
            try
            {
                refreshed = await RefreshTokenAsync(config.SlackRefreshToken);
            }
            catch (SlackAuthException)
            {
                // The refresh token is dead (revoked, or lapsed after 30 days of disuse). Drop the
                // stale credentials so the UI shows "Not connected" and surface a clear reconnect ask.
                config.SlackToken = config.SlackRefreshToken = "";
                config.SlackTokenExpiresAt = null;
                config.Save();
                throw new SlackAuthException("session_expired");
            }

            config.SlackToken          = refreshed.Token;
            config.SlackRefreshToken   = refreshed.RefreshToken ?? config.SlackRefreshToken;
            config.SlackTokenExpiresAt = refreshed.ExpiresAt;
            config.Save();
            return config.SlackToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Parses a URL query string ("?a=1&b=2") into a map, URL-decoding keys and values.
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
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        ThrowIfSlackError(doc.RootElement);
    }

    // Every Slack Web API response carries an "ok" boolean; throw with the "error" string when it's
    // false. Token/credential errors throw SlackAuthException so callers can refresh-and-retry (and,
    // failing that, prompt a reconnect) rather than treat them like an ordinary API failure.
    static void ThrowIfSlackError(JsonElement root)
    {
        if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return;

        var error = (root.TryGetProperty("error", out var e) ? e.GetString() : null) ?? "unknown";
        throw IsAuthError(error)
            ? new SlackAuthException(error)
            : new InvalidOperationException($"Slack API error: {error}");
    }

    static bool IsAuthError(string error) => error is
        "token_expired" or "invalid_auth" or "token_revoked" or "not_authed"
        or "account_inactive" or "invalid_refresh_token";
}

/// <summary>
/// A Slack call failed because the token (or refresh token) is expired, invalid, or revoked —
/// distinct from an ordinary API error so callers can attempt a refresh and, failing that, surface
/// a "reconnect Slack" prompt. The "session_expired" variant means even refreshing failed.
/// </summary>
sealed class SlackAuthException : InvalidOperationException
{
    public SlackAuthException(string error) : base($"Slack authentication error: {error}") { }
}
