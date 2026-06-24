namespace Otter;

/// <summary>
/// Aggregates the registered <see cref="IStatusSignal"/>s and exposes the single highest-precedence
/// active one (list order = precedence). Raises <see cref="ActiveChanged"/> only when that winner
/// changes, so <see cref="TrayApp"/> drives the Slack status from one place no matter how many
/// signals exist.
/// </summary>
sealed class SignalCoordinator : IDisposable
{
    readonly IReadOnlyList<IStatusSignal> _signals;

    /// <summary>The current highest-precedence active signal, or null when nothing is firing.</summary>
    public IStatusSignal? Active { get; private set; }

    /// <summary>Raised when the active signal changes (to a different signal, or to/from null).</summary>
    public event Action<IStatusSignal?>? ActiveChanged;

    public SignalCoordinator(IEnumerable<IStatusSignal> signals)
    {
        _signals = signals.ToList();
        foreach (var s in _signals) s.Changed += OnSignalChanged;
    }

    public void Start()
    {
        foreach (var s in _signals) s.Start();
    }

    // Recompute the winner whenever any signal flips; only notify when it actually changed.
    void OnSignalChanged()
    {
        var active = _signals.FirstOrDefault(s => s.IsActive);
        if (ReferenceEquals(active, Active)) return;
        Active = active;
        ActiveChanged?.Invoke(active);
    }

    public void Dispose()
    {
        foreach (var s in _signals)
        {
            s.Changed -= OnSignalChanged;
            s.Dispose();
        }
    }
}
