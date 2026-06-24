namespace Otter;

/// <summary>
/// The single source of colour and font for every Otter surface — the settings window, its custom
/// controls, and the tray status indicator. Otter is dark-only by design; keeping all colour here
/// means a light/system mode could be layered on later without touching individual controls.
/// </summary>
internal static class Theme
{
    // ── Core dark palette ───────────────────────────────────────────────────────
    public static readonly Color FormBg      = Color.FromArgb(24, 24, 32);
    public static readonly Color NavBg        = Color.FromArgb(18, 18, 24);
    public static readonly Color Fg          = Color.FromArgb(225, 225, 235);
    public static readonly Color Title       = Color.FromArgb(245, 245, 250);
    public static readonly Color Muted       = Color.FromArgb(140, 140, 160);
    public static readonly Color Accent      = Color.FromArgb(96, 165, 250);
    public static readonly Color AccentHover = Color.FromArgb(147, 197, 253);
    public static readonly Color Border      = Color.FromArgb(45, 45, 60);
    public static readonly Color ButtonBg    = Color.FromArgb(45, 45, 60);
    public static readonly Color ButtonHover = Color.FromArgb(60, 60, 80);
    public static readonly Color CodeBg      = Color.FromArgb(34, 34, 44);
    public static readonly Color Danger      = Color.FromArgb(248, 113, 113);

    // ── Status palette ───────────────────────────────────────────────────────────
    public static readonly Color Green  = Color.FromArgb(34, 197, 94);
    public static readonly Color Yellow = Color.FromArgb(250, 204, 21);
    public static readonly Color Orange = Color.FromArgb(251, 146, 60);
    public static readonly Color Red    = Color.FromArgb(239, 68, 68);

    /// <summary>The dot/indicator colour for each of Otter's tray states. Drives both the tray icon
    /// (Phase 3) and any in-window status chips, so the two always agree.</summary>
    public static Color StatusColor(OtterState state) => state switch
    {
        OtterState.Monitoring => Green,
        OtterState.InCall      => Orange,
        OtterState.Snoozed     => Accent,
        OtterState.Disabled    => Muted,
        _                      => Muted,
    };

    /// <summary>Linear blend of two colours (t = 0 → a, t = 1 → b). Used to dim controls that are
    /// disabled by fading them toward the background rather than greying them flat.</summary>
    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R * (1 - t) + b.R * t),
        (int)(a.G * (1 - t) + b.G * t),
        (int)(a.B * (1 - t) + b.B * t));
}

/// <summary>Otter's high-level operating state, shared by the tray and settings so the status
/// language is consistent everywhere.</summary>
internal enum OtterState
{
    Monitoring,
    InCall,
    Snoozed,
    Disabled,
}
