using System.Text.Json;
using System.Text.Json.Serialization;

namespace Otter;

class Config
{
    public string SlackToken    { get; set; } = "";
    public string SlackTeamName { get; set; } = "";

    // Token rotation (Slack PKCE apps): the access token above is short-lived (~12h). These back the
    // refresh flow in SlackClient. A non-null SlackTokenExpiresAt with a refresh token means rotation
    // is in play; an empty refresh token means a legacy non-expiring token that's used as-is. The
    // refresh token is itself single-use (each refresh returns a new one) and lapses after 30 days of
    // disuse, after which the user must reconnect. Times are UTC.
    public string    SlackRefreshToken   { get; set; } = "";
    public DateTime? SlackTokenExpiresAt { get; set; }
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
        try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new Config(); }
        catch { return new Config(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
