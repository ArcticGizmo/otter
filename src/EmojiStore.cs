namespace Otter;

using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Text.Json;

/// <summary>
/// The workspace's custom-emoji catalogue, with two layers of caching so the emoji autocomplete and
/// status preview have something to show instantly and offline:
/// <list type="bullet">
/// <item>the name→URL list, fetched from Slack's <c>emoji.list</c> and mirrored to a small JSON file
/// under <c>%AppData%\Otter\emoji-cache</c> so it survives restarts;</item>
/// <item>downloaded emoji images, scaled to a thumbnail and kept both on disk (the original bytes)
/// and in memory (the scaled <see cref="Image"/>).</item>
/// </list>
/// Standard built-in emoji (<c>:headphones:</c> and friends) aren't part of this — Slack only serves
/// custom uploads — so the catalogue covers exactly the emoji unique to the connected workspace.
///
/// All public members are expected to be touched on the UI thread only. The async loaders await with
/// the WinForms synchronization context captured, so their continuations (and the <see cref="Updated"/>
/// event) run back on the UI thread, which keeps the dictionaries free of cross-thread access.
/// </summary>
sealed class EmojiStore : IDisposable
{
    static readonly HttpClient Http = new();

    static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Otter", "emoji-cache");
    static readonly string ListPath = Path.Combine(CacheDir, "list.json");

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // name → image URL (aliases already resolved). Replaced wholesale on refresh.
    Dictionary<string, string> _urls = new(StringComparer.OrdinalIgnoreCase);

    // name → scaled thumbnail. A cached null means "tried and failed" so we don't retry every paint.
    readonly Dictionary<string, Image?> _images = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);

    readonly int _thumb;

    /// <summary>Raised when the catalogue or a thumbnail changes, so the UI can repaint. Fires on the
    /// UI thread (the loaders capture the WinForms context).</summary>
    public event Action? Updated;

    public EmojiStore(int thumbSize = 18)
    {
        _thumb = thumbSize;
        LoadListFromDisk();
    }

    /// <summary>True once we have any emoji to offer (from disk or a live refresh).</summary>
    public bool HasData => _urls.Count > 0;

    /// <summary>Is <paramref name="name"/> (with or without surrounding colons) a known custom emoji?</summary>
    public bool Knows(string name) => _urls.ContainsKey(Normalize(name));

    // ── List ─────────────────────────────────────────────────────────────────────

    /// <summary>Re-fetches the catalogue from Slack and mirrors it to disk. Swallows failures (offline,
    /// or a token granted before the emoji:read scope) so the last good disk cache keeps working.</summary>
    public async Task RefreshAsync(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        try
        {
            var map = await SlackClient.GetEmojiListAsync(token);
            _urls = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            SaveListToDisk();
            Updated?.Invoke();
        }
        catch
        {
            // Keep whatever we loaded from disk; autocomplete simply won't gain new entries this run.
        }
    }

    void LoadListFromDisk()
    {
        try
        {
            if (!File.Exists(ListPath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ListPath));
            if (map is not null)
                _urls = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* corrupt cache — ignore, a refresh will rebuild it */ }
    }

    void SaveListToDisk()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(ListPath, JsonSerializer.Serialize(_urls, JsonOpts));
        }
        catch { /* non-fatal — the in-memory list still works this session */ }
    }

    // ── Search ─────────────────────────────────────────────────────────────────────

    /// <summary>Names matching <paramref name="query"/> (colons optional), prefix matches first then
    /// substring matches, each alphabetical, capped at <paramref name="max"/>.</summary>
    public IReadOnlyList<string> Search(string query, int max)
    {
        query = Normalize(query);
        var prefix   = new List<string>();
        var contains = new List<string>();
        foreach (var name in _urls.Keys)
        {
            if (query.Length == 0 || name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                prefix.Add(name);
            else if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                contains.Add(name);
        }
        prefix.Sort(StringComparer.OrdinalIgnoreCase);
        contains.Sort(StringComparer.OrdinalIgnoreCase);
        return prefix.Concat(contains).Take(max).ToList();
    }

    // ── Images ─────────────────────────────────────────────────────────────────────

    /// <summary>The thumbnail for <paramref name="name"/> if it's already in memory; otherwise null,
    /// having kicked off a background load that raises <see cref="Updated"/> when the image is ready.
    /// Safe to call from a paint handler — it never blocks.</summary>
    public Image? GetImageCached(string name)
    {
        name = Normalize(name);
        if (_images.TryGetValue(name, out var img)) return img;
        if (_urls.ContainsKey(name)) _ = LoadImageAsync(name);
        return null;
    }

    async Task LoadImageAsync(string name)
    {
        if (_images.ContainsKey(name) || !_loading.Add(name)) return;

        Image? thumb = null;
        try
        {
            if (_urls.TryGetValue(name, out var url))
            {
                var file = DiskPath(name, url);
                byte[] bytes;
                if (File.Exists(file))
                {
                    bytes = await File.ReadAllBytesAsync(file);
                }
                else
                {
                    bytes = await Http.GetByteArrayAsync(url);
                    Directory.CreateDirectory(CacheDir);
                    await File.WriteAllBytesAsync(file, bytes);
                }
                thumb = MakeThumbnail(bytes, _thumb);
            }
        }
        catch
        {
            thumb = null; // cache the miss below so we don't hammer a broken URL
        }
        finally
        {
            _images[name] = thumb;
            _loading.Remove(name);
            Updated?.Invoke();
        }
    }

    static Image MakeThumbnail(byte[] bytes, int size)
    {
        using var ms  = new MemoryStream(bytes);
        using var src = Image.FromStream(ms); // animated GIFs collapse to their first frame — fine here
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, size, size);
        return bmp;
    }

    static string DiskPath(string name, string url)
    {
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".png";

        // Slack emoji names are mostly filesystem-safe; sanitise the rest defensively.
        var safe = new string(name.Select(c =>
            char.IsLetterOrDigit(c) || c is '_' or '-' or '+' ? c : '_').ToArray());
        return Path.Combine(CacheDir, safe + ext);
    }

    static string Normalize(string name) => name.Trim().Trim(':').ToLowerInvariant();

    public void Dispose()
    {
        foreach (var img in _images.Values) img?.Dispose();
        _images.Clear();
    }
}
