using System.Text.Json;
using System.Text.Json.Serialization;

namespace Otter;

class Config
{
    public string SlackToken    { get; set; } = "";
    public string SlackTeamName { get; set; } = "";
    public string SlackClientId { get; set; } = "";
    public string StatusText      { get; set; } = "In a Teams call";
    public string StatusEmoji     { get; set; } = ":headphones:";
    public bool   Enabled         { get; set; } = true;
    public bool   NotificationsEnabled { get; set; } = true;
    public DateTime? SnoozedUntil { get; set; }

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
