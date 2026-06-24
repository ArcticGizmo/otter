namespace Otter;

/// <summary>
/// A source of "you're busy" signal — e.g. an active Teams call. Otter aggregates one or more of
/// these through <see cref="SignalCoordinator"/> and drives a single Slack status from the
/// highest-precedence active one. A Teams call is the only provider today; this seam lets future
/// signals (screen-lock, focused app, calendar) be added without touching <see cref="TrayApp"/>.
/// </summary>
interface IStatusSignal : IDisposable
{
    /// <summary>Short identifier, e.g. "Teams call".</summary>
    string Name { get; }

    /// <summary>Human phrase shown while active, e.g. "On a Teams call".</summary>
    string ActiveDescription { get; }

    /// <summary>True while this signal is firing.</summary>
    bool IsActive { get; }

    /// <summary>Raised (possibly from a background thread) whenever <see cref="IsActive"/> changes.</summary>
    event Action? Changed;

    /// <summary>Begin monitoring on a background thread.</summary>
    void Start();
}
