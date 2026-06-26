using System.Text.Json;
using System.Text.Json.Serialization;

namespace Otter;

class Config
{
    // The two Slack credentials are held in memory as plaintext (the rest of the app reads them as-is)
    // but stored DPAPI-encrypted via the *Stored shims below. JsonIgnore here, [JsonPropertyName] there,
    // so the on-disk keys stay "SlackToken"/"SlackRefreshToken" and only the values change.
    [JsonIgnore] public string SlackToken        { get; set; } = "";
    [JsonIgnore] public string SlackRefreshToken { get; set; } = "";

    public string SlackTeamName { get; set; } = "";

    // Token rotation (Slack PKCE apps): the access token above is short-lived (~12h). These back the
    // refresh flow in SlackClient. A non-null SlackTokenExpiresAt with a refresh token means rotation
    // is in play; an empty refresh token means a legacy non-expiring token that's used as-is. The
    // refresh token is itself single-use (each refresh returns a new one) and lapses after 30 days of
    // disuse, after which the user must reconnect. Times are UTC.
    public DateTime? SlackTokenExpiresAt { get; set; }

    // DPAPI-encrypted on-disk forms of the two tokens. Get encrypts the in-memory plaintext; set decrypts
    // (transparently accepting a legacy plaintext value, and flagging a re-save so it's upgraded).
    [JsonPropertyName("SlackToken")]
    public string SlackTokenStored
    {
        get => Dpapi.Protect(SlackToken);
        set { SlackToken = Dpapi.Resolve(value, out var r); _needsResave |= r; }
    }

    [JsonPropertyName("SlackRefreshToken")]
    public string SlackRefreshTokenStored
    {
        get => Dpapi.Protect(SlackRefreshToken);
        set { SlackRefreshToken = Dpapi.Resolve(value, out var r); _needsResave |= r; }
    }

    // Set during load when a stored token was legacy plaintext (or unusable ciphertext); triggers a
    // one-time re-save to migrate the file to encrypted form. Never serialized.
    [JsonIgnore] bool _needsResave;

    public string StatusText      { get; set; } = "In a Teams call";
    public string StatusEmoji     { get; set; } = ":headphones:";
    public bool   Enabled         { get; set; } = true;
    public DateTime? SnoozedUntil { get; set; }

    [JsonIgnore]
    public bool IsSnoozed => SnoozedUntil is { } until && until > DateTime.UtcNow;

    // ── Persistence ───────────────────────────────────────────────────────────

    [JsonIgnore]
    static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Otter");

    [JsonIgnore]
    static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    [JsonIgnore]
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Config Load()
    {
        if (!File.Exists(ConfigPath)) return new Config();
        try
        {
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new Config();
            // Migrate a legacy plaintext (or upgrade an unusable) token file to encrypted form, once.
            if (config._needsResave) config.Save();
            return config;
        }
        catch { return new Config(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
