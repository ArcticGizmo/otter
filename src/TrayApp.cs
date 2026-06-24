using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Otter;

class TrayApp : IDisposable
{
    Config _config;
    bool _slackStatusSet;
    bool _settingsOpen;
    SlackClient.SlackStatus? _previousStatus;

    readonly NotifyIcon _tray;
    readonly SignalCoordinator _coordinator;

    // Menu items updated dynamically
    readonly ToolStripMenuItem _statusItem;
    readonly ToolStripMenuItem _enabledItem;
    readonly ToolStripMenuItem _notificationsItem;
    readonly ToolStripMenuItem _clearSnoozeItem;

    // The app icon (brown otter on the accent tile) used as the tray icon base.
    readonly Bitmap? _otterArt = Ui.LoadEmbeddedBitmap("Otter.icon.png");

    // GDI handle backing the current tray icon. Icon.FromHandle doesn't own the HICON that
    // Bitmap.GetHicon creates, so we track it and DestroyIcon it ourselves to avoid a handle leak
    // every time the icon changes.
    IntPtr _trayHicon;

    public TrayApp()
    {
        _config = Config.Load();

        // The set of "you're busy" signals. Teams is the only one today; add more here and the rest
        // of the app (status updates, tray state) follows automatically.
        _coordinator = new SignalCoordinator(new IStatusSignal[] { new TeamsCallSignal() });
        _coordinator.ActiveChanged += OnActiveChanged;

        // ── Context menu ──────────────────────────────────────────────────────

        // Non-interactive status header — a bold label with a colour-coded state dot in the image
        // margin. Tag "header" tells the renderer to skip the hover highlight.
        _statusItem = new ToolStripMenuItem("Monitoring")
        {
            Tag  = "header",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point),
        };

        _enabledItem = new ToolStripMenuItem("Enabled", null, OnToggleEnabled)
        {
            CheckOnClick = true,
            Checked      = _config.Enabled,
        };

        _notificationsItem = new ToolStripMenuItem("Show notifications", null, OnToggleNotifications)
        {
            CheckOnClick = true,
            Checked      = _config.NotificationsEnabled,
        };

        _clearSnoozeItem = new ToolStripMenuItem("Clear snooze", null, OnClearSnooze);

        var snoozeMenu = new ToolStripMenuItem("Snooze", null,
            new ToolStripMenuItem("30 minutes", null, (_, _) => Snooze(30)),
            new ToolStripMenuItem("1 hour",     null, (_, _) => Snooze(60)),
            new ToolStripMenuItem("2 hours",    null, (_, _) => Snooze(120)),
            new ToolStripSeparator(),
            _clearSnoozeItem);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            _enabledItem,
            snoozeMenu,
            _notificationsItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Settings…", null, OnOpenSettings),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit", null, (_, _) => Application.Exit()),
        });
        TrayMenu.ApplyDark(menu);

        _tray = new NotifyIcon
        {
            Text             = "Otter",
            ContextMenuStrip = menu,
            Visible          = true,
        };
        // Left-click opens settings (more intuitive); right-click still shows the context menu.
        _tray.MouseClick += OnTrayMouseClick;

        Application.ApplicationExit += OnApplicationExit;

        RefreshUI();
    }

    public void Start() => _coordinator.Start();

    // ── State helpers ─────────────────────────────────────────────────────────

    bool IsEnabled => _config.Enabled && !IsSnoozed;
    bool IsSnoozed => _config.SnoozedUntil.HasValue && _config.SnoozedUntil.Value > DateTime.UtcNow;

    // Brings the Slack status in line with the current state: set it when Otter is enabled and a
    // signal is firing, clear/restore it otherwise. Idempotent, so it's safe to call from any state
    // change (signal flip, enable toggle, snooze start/clear/expire).
    void ReevaluateStatus()
    {
        bool shouldShow = IsEnabled && _coordinator.Active != null;
        if (shouldShow && !_slackStatusSet)      _ = SetSlackStatusAsync();
        else if (!shouldShow && _slackStatusSet) _ = ClearSlackStatusAsync();
    }

    // ── Signal events (background thread → marshal to UI) ──────────────────────

    void OnActiveChanged(IStatusSignal? activeSignal)
    {
        RunOnUiThread(() =>
        {
            bool announce = activeSignal != null && IsEnabled && !_slackStatusSet && _config.NotificationsEnabled;
            ReevaluateStatus();
            if (announce)
                _tray.ShowBalloonTip(3_000, "Otter",
                    $"{activeSignal!.ActiveDescription} — updating your Slack status.", ToolTipIcon.Info);
            RefreshUI();
        });
    }

    // ── Menu handlers ─────────────────────────────────────────────────────────

    void OnToggleEnabled(object? s, EventArgs e)
    {
        _config.Enabled = _enabledItem.Checked;
        _config.Save();
        ReevaluateStatus();   // clears when disabling, or re-applies if a signal is already firing
        RefreshUI();
    }

    void Snooze(int minutes)
    {
        _config.SnoozedUntil = DateTime.UtcNow.AddMinutes(minutes);
        _config.Save();
        ReevaluateStatus();   // snoozed ⇒ status cleared
        RefreshUI();

        // Auto-expire the snooze, then re-apply the status if a signal is still firing.
        _ = Task.Delay(TimeSpan.FromMinutes(minutes)).ContinueWith(_ =>
        {
            if (IsSnoozed) return; // already manually cleared or re-snoozed further out
            _config.SnoozedUntil = null;
            _config.Save();
            RunOnUiThread(() => { ReevaluateStatus(); RefreshUI(); });
        });
    }

    void OnClearSnooze(object? s, EventArgs e)
    {
        _config.SnoozedUntil = null;
        _config.Save();
        ReevaluateStatus();
        RefreshUI();
    }

    void OnToggleNotifications(object? s, EventArgs e)
    {
        _config.NotificationsEnabled = _notificationsItem.Checked;
        _config.Save();
    }

    // Open settings on a left-click only — a right-click is reserved for the context menu, which the
    // NotifyIcon shows itself.
    void OnTrayMouseClick(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            OnOpenSettings(s, e);
    }

    void OnOpenSettings(object? s, EventArgs e)
    {
        // ShowDialog runs a nested message loop, so a second tray click could otherwise re-enter and
        // stack another window on top. Guard against it.
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            using var form = new SettingsWindow(_config);
            if (form.ShowDialog() != DialogResult.OK) return;

            _config = form.Result;
            _config.Save();
            _enabledItem.Checked = _config.Enabled;
            RefreshUI();
        }
        finally { _settingsOpen = false; }
    }

    void OnApplicationExit(object? s, EventArgs e)
    {
        if (_slackStatusSet) SlackClient.ClearStatusAsync(_config.SlackToken).GetAwaiter().GetResult();
    }

    // ── Slack ─────────────────────────────────────────────────────────────────

    async Task SetSlackStatusAsync()
    {
        if (string.IsNullOrEmpty(_config.SlackToken)) return;
        try
        {
            _previousStatus = await SlackClient.GetStatusAsync(_config.SlackToken);
            await SlackClient.SetStatusAsync(_config.SlackToken, _config.StatusText, _config.StatusEmoji);
            _slackStatusSet = true;
        }
        catch (Exception ex)
        {
            _previousStatus = null;
            _tray.ShowBalloonTip(5_000, "Otter — Slack error", ex.Message, ToolTipIcon.Error);
        }
    }

    async Task ClearSlackStatusAsync()
    {
        if (string.IsNullOrEmpty(_config.SlackToken)) return;
        try
        {
            var prev = _previousStatus;

            // If the previous status matches what Otter set (race condition / desync),
            // treat it as empty so we don't re-apply a stale Otter status.
            var restoreText  = prev?.Text       ?? "";
            var restoreEmoji = prev?.Emoji      ?? "";
            var restoreExp   = prev?.Expiration ?? 0;
            
            if (restoreText == _config.StatusText && restoreEmoji == _config.StatusEmoji)
            {
                await SlackClient.ClearStatusAsync(_config.SlackToken);
                return;                
            }
            
            // If expiration has passed, clear status
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (restoreExp > 0 && restoreExp <= now)
            {
                await SlackClient.ClearStatusAsync(_config.SlackToken);
                return;
            }

            await SlackClient.SetStatusAsync(_config.SlackToken, restoreText, restoreEmoji, restoreExp);
        }
        catch { /* best-effort */ }
        finally
        {
            _slackStatusSet = false;
            _previousStatus = null;
        }
    }

    // ── UI refresh ────────────────────────────────────────────────────────────

    // Otter's current operating state, driving both the tray icon colour and the menu header.
    OtterState CurrentState =>
        !_config.Enabled            ? OtterState.Disabled :
        IsSnoozed                   ? OtterState.Snoozed  :
        _coordinator.Active != null ? OtterState.Active   :
                                      OtterState.Monitoring;

    void RefreshUI()
    {
        var state = CurrentState;
        var color = Theme.StatusColor(state);

        string label, tooltip;
        switch (state)
        {
            case OtterState.Disabled:
                label = "Disabled";        tooltip = "Otter — disabled"; break;
            case OtterState.Snoozed:
                var until = _config.SnoozedUntil!.Value.ToLocalTime();
                label = $"Snoozed until {until:h:mm tt}"; tooltip = $"Otter — snoozed until {until:h:mm tt}"; break;
            case OtterState.Active:
                label = _coordinator.Active?.ActiveDescription ?? "Active";
                tooltip = $"Otter — {label.ToLowerInvariant()}"; break;
            default:
                label = "Monitoring";      tooltip = "Otter — monitoring"; break;
        }

        // Header: bold label with a colour-coded state dot in the image margin.
        _statusItem.Text = label;
        var oldImg = _statusItem.Image;
        _statusItem.Image = TrayMenu.DotImage(color);
        oldImg?.Dispose();

        _clearSnoozeItem.Enabled = IsSnoozed;

        UpdateTrayIcon(color);

        // NotifyIcon.Text is capped at 63 characters
        _tray.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    // Draws the otter app icon with a small state-colour badge in the corner, and swaps it onto the
    // tray — freeing the previous GDI handle. The badge keeps the operating state readable at a
    // glance now that the icon itself is the otter rather than a plain state dot.
    void UpdateTrayIcon(Color color)
    {
        const int s = 32;
        using var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            if (_otterArt != null)
                g.DrawImage(_otterArt, new Rectangle(0, 0, s, s));
            else
            {
                using var baseFill = new SolidBrush(color);
                g.FillEllipse(baseFill, 1, 1, s - 2, s - 2);
            }

            // State badge, bottom-right, with a dark ring so it reads on the icon and the taskbar.
            float d  = s * 0.40f;
            float bx = s - d - s * 0.04f, by = s - d - s * 0.04f;
            float r  = s * 0.06f;
            using var ring = new SolidBrush(Color.FromArgb(235, 18, 18, 24));
            g.FillEllipse(ring, bx - r, by - r, d + 2 * r, d + 2 * r);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, bx, by, d, d);
        }

        var hicon    = bmp.GetHicon();
        var newIcon  = Icon.FromHandle(hicon);
        var oldIcon  = _tray.Icon;
        var oldHicon = _trayHicon;

        _tray.Icon = newIcon;
        _trayHicon = hicon;

        oldIcon?.Dispose();
        if (oldHicon != IntPtr.Zero) NativeMethods.DestroyIcon(oldHicon);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void RunOnUiThread(Action action)
    {
        if (_tray.ContextMenuStrip?.IsHandleCreated == true)
            _tray.ContextMenuStrip.Invoke(action);
        else
            action();
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _tray.Visible = false;
        _tray.Icon?.Dispose();
        if (_trayHicon != IntPtr.Zero) NativeMethods.DestroyIcon(_trayHicon);
        _statusItem.Image?.Dispose();
        _otterArt?.Dispose();
        _tray.Dispose();
    }
}
