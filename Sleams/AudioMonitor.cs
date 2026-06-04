namespace Sleams;

class AudioMonitor : IDisposable
{
    public event Action? CallStarted;
    public event Action? CallEnded;

    public bool IsInCall { get; private set; }

    readonly string[] _processNames;
    readonly CancellationTokenSource _cts = new();
    Task? _task;

    public AudioMonitor(params string[] processNames) => _processNames = processNames;

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
        var inCall = sessions.Any(s => _processNames.Contains(s, StringComparer.OrdinalIgnoreCase));

        if (inCall && !IsInCall)  { IsInCall = true;  CallStarted?.Invoke(); }
        if (!inCall && IsInCall)  { IsInCall = false; CallEnded?.Invoke();   }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _task?.Wait(2_000);
        _cts.Dispose();
    }
}
