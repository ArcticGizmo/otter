namespace Otter;

/// <summary>
/// Fires while Microsoft Teams holds an active microphone capture session — i.e. you're on a call.
/// Polls WASAPI (via <see cref="NativeMethods.GetActiveCaptureSessions"/>) every few seconds on a
/// background thread. Otter's first <see cref="IStatusSignal"/>.
/// </summary>
sealed class TeamsCallSignal : IStatusSignal
{
    public string Name              => "Teams call";
    public string ActiveDescription => "On a Teams call";

    public bool IsActive { get; private set; }
    public event Action? Changed;

    static readonly string[] ProcessNames = { "ms-teams" };

    readonly CancellationTokenSource _cts = new();
    Task? _task;

    public void Start()
    {
        _task = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try { Poll(); }
                catch { /* device temporarily unavailable */ }

                try { await Task.Delay(5_000, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    void Poll()
    {
        var sessions = NativeMethods.GetActiveCaptureSessions();
        bool active = sessions.Any(s => ProcessNames.Contains(s, StringComparer.OrdinalIgnoreCase));

        if (active != IsActive)
        {
            IsActive = active;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _task?.Wait(2_000);
        _cts.Dispose();
    }
}
