using Microsoft.Win32;

namespace Otter;

/// <summary>
/// Fires while Microsoft Teams is actively capturing the microphone — Otter's signal that you're on a
/// Teams call.
///
/// Reads Windows' CapabilityAccessManager (the record behind the "🎤 in use" privacy indicator), which
/// tracks microphone use per *app capability* rather than per audio device. That makes detection
/// independent of which input device Teams uses, so virtual soundcards (e.g. SteelSeries Sonar) — which
/// the older WASAPI capture-session approach couldn't see through — no longer break it. An app is
/// capturing right now iff its <c>LastUsedTimeStop</c> is 0 (started, not yet stopped).
/// </summary>
sealed class MicrophoneInUseSignal : IStatusSignal
{
    public string Name              => "Teams call";
    public string ActiveDescription => "On a Teams call";

    public bool IsActive { get; private set; }
    public event Action? Changed;

    const string KeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    readonly CancellationTokenSource _cts = new();
    Task? _task;

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
        bool active = AnyTeamsCapturing();
        if (active == IsActive) return;
        IsActive = active;
        Changed?.Invoke();
    }

    static bool AnyTeamsCapturing()
    {
        using var root = Registry.CurrentUser.OpenSubKey(KeyPath);
        return root != null && Scan(root, underTeams: false);
    }

    // The mic ConsentStore is keyed differently per app kind: packaged apps (new Teams) appear as a
    // subkey named for their PackageFamilyName ("MSTeams_8wekyb3d8bbwe"); unpackaged apps (classic
    // Teams) live under "NonPackaged" keyed by an encoded exe path containing "Teams.exe". Values may
    // sit on that key or a child, so once we're under a Teams-named ancestor we check every descendant.
    static bool Scan(RegistryKey key, bool underTeams)
    {
        if (underTeams && IsCapturing(key)) return true;

        foreach (var name in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(name);
            if (sub == null) continue;

            bool t = underTeams || name.Contains("teams", StringComparison.OrdinalIgnoreCase);
            if (Scan(sub, t)) return true;
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
