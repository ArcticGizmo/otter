using Microsoft.Win32;

namespace Otter;

/// <summary>One app observed capturing the microphone, identified by its exe filename (unpackaged) or
/// package family name (packaged). The <see cref="Identifier"/> is what match terms are tested against
/// and what the Detection page's "quick add" turns into a new matcher.</summary>
record MicCapture(string Identifier);

/// <summary>
/// The microphone-usage discovery feed exposed by <see cref="MicrophoneInUseSignal"/>. When
/// <see cref="TrackingEnabled"/> is on it keeps a rolling list of the most recent apps seen capturing
/// the mic, so the Detection settings page can surface apps that aren't matching yet.
/// </summary>
interface IMicUsageFeed
{
    bool TrackingEnabled { get; set; }

    /// <summary>Most-recent-first, distinct by identifier, capped at <see cref="MicrophoneInUseSignal.LogCapacity"/>.</summary>
    IReadOnlyList<MicCapture> RecentCaptures { get; }

    /// <summary>Raised (possibly off the UI thread) when <see cref="RecentCaptures"/> changes.</summary>
    event Action? CapturesChanged;
}

/// <summary>
/// Fires while any configured app is actively capturing the microphone — Otter's signal that you're on
/// a call. Driven by the user's <see cref="DetectionProduct"/> list: an app counts as a match when any
/// enabled product's term is a case-insensitive substring of the app's identifier.
///
/// Reads Windows' CapabilityAccessManager (the record behind the "🎤 in use" privacy indicator), which
/// tracks microphone use per *app capability* rather than per audio device. That makes detection
/// independent of which input device the app uses, so virtual soundcards (e.g. SteelSeries Sonar) —
/// which the older WASAPI capture-session approach couldn't see through — no longer break it. An app is
/// capturing right now iff its <c>LastUsedTimeStop</c> is 0 (started, not yet stopped).
/// </summary>
sealed class MicrophoneInUseSignal : IStatusSignal, IMicUsageFeed
{
    public const int LogCapacity = 20;

    public string Name              => "Call";
    public string ActiveDescription => "On a call";

    public bool IsActive { get; private set; }
    public event Action? Changed;

    // Discovery feed.
    public bool TrackingEnabled { get; set; }
    public event Action? CapturesChanged;

    const string KeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    readonly CancellationTokenSource _cts = new();
    Task? _task;

    // Enabled match terms, lower-cased. Swapped wholesale by UpdateMatchers; read on the poll thread.
    volatile string[] _terms = Array.Empty<string>();

    // Identifiers seen capturing on the previous poll — lets us treat a not-capturing→capturing
    // transition as a fresh "capture start" for the discovery log. Touched only on the poll thread.
    HashSet<string> _prevCapturing = new(StringComparer.OrdinalIgnoreCase);

    // The rolling discovery log, most-recent-first. Guarded by _logLock (poll thread writes, UI reads).
    readonly object _logLock = new();
    readonly List<MicCapture> _log = new();

    /// <summary>
    /// Replaces the active matcher set from the current detection config. The volatile swap is the only
    /// state touched, so this is safe to call from any thread; the next poll (≤5s) picks up the change
    /// and fires <see cref="Changed"/> if it flips detection. We deliberately don't re-poll here —
    /// doing so from the UI thread would race the poll thread, and at construction time (before the
    /// coordinator subscribes) the first transition would be lost.
    /// </summary>
    public void UpdateMatchers(IEnumerable<DetectionProduct> products) =>
        _terms = products
            .Where(p => p.Enabled)
            .SelectMany(p => p.Terms)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToArray();

    public IReadOnlyList<MicCapture> RecentCaptures
    {
        get { lock (_logLock) return _log.ToArray(); }
    }

    public void Start()
    {
        _task = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try { Poll(); }
                catch { /* registry transiently unavailable */ }

                try { await Task.Delay(5_000, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    void Poll()
    {
        var capturing = EnumerateCapturing();

        bool active = capturing.Any(Matches);
        if (active != IsActive)
        {
            IsActive = active;
            Changed?.Invoke();
        }

        if (TrackingEnabled) UpdateLog(capturing);
    }

    bool Matches(string identifier)
    {
        var terms = _terms;   // volatile read once
        if (terms.Length == 0) return false;
        string id = identifier.ToLowerInvariant();
        foreach (var t in terms)
            if (id.Contains(t)) return true;
        return false;
    }

    // Records a fresh capture (an app that wasn't capturing last poll) at the front of the log,
    // de-duplicated by identifier and capped at LogCapacity. Runs on the poll thread.
    void UpdateLog(List<string> capturing)
    {
        var current = new HashSet<string>(capturing, StringComparer.OrdinalIgnoreCase);
        var started = capturing.Where(id => !_prevCapturing.Contains(id)).ToList();
        _prevCapturing = current;
        if (started.Count == 0) return;

        bool changed = false;
        lock (_logLock)
        {
            foreach (var id in started)
            {
                _log.RemoveAll(c => string.Equals(c.Identifier, id, StringComparison.OrdinalIgnoreCase));
                _log.Insert(0, new MicCapture(id));
                changed = true;
            }
            if (_log.Count > LogCapacity) _log.RemoveRange(LogCapacity, _log.Count - LogCapacity);
        }
        if (changed) CapturesChanged?.Invoke();
    }

    // Every app currently holding a mic capture session, as a list of identifiers. The ConsentStore is
    // keyed differently per app kind: packaged apps appear as a subkey named for their PackageFamilyName
    // ("MSTeams_8wekyb3d8bbwe"); unpackaged apps live under "NonPackaged" keyed by an encoded exe path
    // ("C:#Program Files#…#Zoom.exe"). The identifier we expose is the PFN for packaged apps and the exe
    // filename (the last '#'-segment) for unpackaged ones.
    static List<string> EnumerateCapturing()
    {
        var result = new List<string>();
        using var root = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (root == null) return result;

        foreach (var name in root.GetSubKeyNames())
        {
            using var sub = root.OpenSubKey(name);
            if (sub == null) continue;

            if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var appName in sub.GetSubKeyNames())
                {
                    using var app = sub.OpenSubKey(appName);
                    if (app != null && AnyCapturing(app))
                        result.Add(appName.Split('#')[^1]);   // exe filename
                }
            }
            else if (AnyCapturing(sub))
            {
                result.Add(name);   // package family name
            }
        }
        return result;
    }

    // True if this app key (or any descendant) holds an active capture session — values may sit on the
    // app key itself or on a child.
    static bool AnyCapturing(RegistryKey key)
    {
        if (IsCapturing(key)) return true;
        foreach (var name in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(name);
            if (sub != null && AnyCapturing(sub)) return true;
        }
        return false;
    }

    // In use ⇔ a capture session was started (Start > 0) and not yet stopped (Stop == 0). Both values
    // are REG_QWORD FILETIMEs surfaced as Int64.
    static bool IsCapturing(RegistryKey appKey) =>
        appKey.GetValue("LastUsedTimeStop")  is long stop  && stop  == 0 &&
        appKey.GetValue("LastUsedTimeStart") is long start && start >  0;

    public void Dispose()
    {
        _cts.Cancel();
        _task?.Wait(2_000);
        _cts.Dispose();
    }
}
