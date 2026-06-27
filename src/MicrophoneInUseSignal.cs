using Microsoft.Win32;

namespace Otter;

/// <summary>
/// Fires while a configured app is actively capturing the microphone — Otter's signal that you're on a
/// call. Construct one per app (see <see cref="Teams"/> / <see cref="Zoom"/>); the order they're
/// registered with <see cref="SignalCoordinator"/> sets precedence.
///
/// Reads Windows' CapabilityAccessManager (the record behind the "🎤 in use" privacy indicator), which
/// tracks microphone use per *app capability* rather than per audio device. That makes detection
/// independent of which input device the app uses, so virtual soundcards (e.g. SteelSeries Sonar) —
/// which the older WASAPI capture-session approach couldn't see through — no longer break it. An app is
/// capturing right now iff its <c>LastUsedTimeStop</c> is 0 (started, not yet stopped).
/// </summary>
sealed class MicrophoneInUseSignal : IStatusSignal
{
    public string Name              { get; }
    public string ActiveDescription { get; }

    // What this signal counts as "its app" in the mic ConsentStore — see IsAppKey for the two key forms.
    readonly string[] _packagePrefixes;
    readonly string[] _exeNames;

    public bool IsActive { get; private set; }
    public event Action? Changed;

    const string KeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    readonly CancellationTokenSource _cts = new();
    Task? _task;

    /// <param name="exeNames">Exe filenames for unpackaged installs, e.g. "Zoom.exe".</param>
    /// <param name="packagePrefixes">PackageFamilyName prefixes for Store/MSIX apps, e.g. "MSTeams_".</param>
    public MicrophoneInUseSignal(
        string name,
        string activeDescription,
        IEnumerable<string> exeNames,
        IEnumerable<string>? packagePrefixes = null)
    {
        Name              = name;
        ActiveDescription = activeDescription;
        _exeNames         = exeNames.ToArray();
        _packagePrefixes  = packagePrefixes?.ToArray() ?? Array.Empty<string>();
    }

    /// <summary>Microsoft Teams — packaged "new Teams" plus the classic unpackaged client.</summary>
    public static MicrophoneInUseSignal Teams() => new(
        "Teams call", "On a Teams call",
        exeNames:        new[] { "Teams.exe", "ms-teams.exe" },
        packagePrefixes: new[] { "MSTeams_" });

    /// <summary>Zoom desktop client.</summary>
    public static MicrophoneInUseSignal Zoom() => new(
        "Zoom call", "On a Zoom call",
        exeNames: new[] { "Zoom.exe" });

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
        bool active = AnyAppCapturing();
        if (active == IsActive) return;
        IsActive = active;
        Changed?.Invoke();
    }

    bool AnyAppCapturing()
    {
        using var root = Registry.CurrentUser.OpenSubKey(KeyPath);
        return root != null && Scan(root, underApp: false);
    }

    // The mic ConsentStore is keyed differently per app kind: packaged apps (e.g. new Teams) appear as a
    // subkey named for their PackageFamilyName ("MSTeams_8wekyb3d8bbwe"); unpackaged apps (classic Teams,
    // Zoom) live under "NonPackaged" keyed by an encoded exe path ending in the exe name. Values may sit
    // on that key or a child, so once we're under one of our app's keys we check every descendant.
    bool Scan(RegistryKey key, bool underApp)
    {
        if (underApp && IsCapturing(key)) return true;

        foreach (var name in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(name);
            if (sub == null) continue;

            if (Scan(sub, underApp || IsAppKey(name))) return true;
        }
        return false;
    }

    // Identifies a ConsentStore key belonging to this signal's app, matching the package name / exe
    // filename precisely rather than by substring. A loose "contains" test would also fire for unrelated
    // apps (e.g. "Teams" matching TeamSpeak), reporting a call that isn't happening.
    bool IsAppKey(string name)
    {
        // Packaged apps: key is the PackageFamilyName "<prefix><publisherHash>".
        foreach (var prefix in _packagePrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        // NonPackaged keys encode the full path with '#' as the separator; the last segment is the exe.
        string exe = name.Split('#')[^1];
        foreach (var e in _exeNames)
            if (exe.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;

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
