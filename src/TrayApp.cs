using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Velopack;
using Velopack.Sources;

namespace Otter;

class TrayApp : IDisposable
{
    Config _config;
    bool _slackStatusSet;

    // Latched while a check/download/apply is in flight so a second click (the tray menu and the
    // settings window both reach CheckForUpdates) can't kick off a parallel run and race two installs.
    bool _updateInProgress;
    SettingsWindow? _settingsForm;
    SlackClient.SlackStatus? _previousStatus;

    readonly NotifyIcon _tray;
    readonly SignalCoordinator _coordinator;

    // Menu items updated dynamically
    readonly ToolStripMenuItem _statusItem;
    readonly ToolStripMenuItem _enabledItem;
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

        // The set of "you're busy" signals. The microphone signal detects Teams calls device-agnostically
        // (works with virtual soundcards) and is the only one today; add more here and the rest of the app
        // (status updates, tray state) follows automatically.
        _coordinator = new SignalCoordinator(new IStatusSignal[] { new MicrophoneInUseSignal() });
        _coordinator.ActiveChanged += OnActiveChanged;

        // ── Context menu ──────────────────────────────────────────────────────

        // Non-interactive status header — a bold label with a colour-coded state dot in the image
        // margin. It has no click handler, so clicking it is a no-op.
        _statusItem = new ToolStripMenuItem("Monitoring")
        {
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point),
        };

        _enabledItem = new ToolStripMenuItem("Enabled", null, OnToggleEnabled)
        {
            CheckOnClick = true,
            Checked      = _config.Enabled,
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
            new ToolStripSeparator(),
            new ToolStripMenuItem("Settings…", null, OnOpenSettings),
            new ToolStripMenuItem("Check for updates…", null, (_, _) => CheckForUpdates()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit", null, (_, _) => Application.Exit()),
        });

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

    bool IsEnabled => _config.Enabled && !_config.IsSnoozed;

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

    void OnActiveChanged(IStatusSignal? _)
    {
        RunOnUiThread(() =>
        {
            ReevaluateStatus();
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
            if (_config.IsSnoozed) return; // already manually cleared or re-snoozed further out
            _config.SnoozedUntil = null;
            _config.Save();
            RunOnUiThread(() => { ReevaluateStatus(); RefreshUI(); });
        });
    }

    void OnClearSnooze(object? s, EventArgs e) => ClearSnooze();

    void ClearSnooze()
    {
        _config.SnoozedUntil = null;
        _config.Save();
        ReevaluateStatus();
        RefreshUI();
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
        // When the window is already open it may be sitting on another virtual desktop — pull it onto
        // the one the user is currently viewing and focus it, rather than stacking a copy or yanking
        // the user back to the desktop it was opened on.
        if (_settingsForm is { IsDisposed: false })
        {
            FocusSettingsOnCurrentDesktop();
            return;
        }

        // Shown modeless (no owner) so it's a free-standing top-level window we can move between
        // virtual desktops. It edits the live config and persists each change itself, calling back
        // here so the tray reflects edits as they happen — there's no Save/Cancel round-trip.
        var form = new SettingsWindow(_config, OnSettingsChanged, Snooze, ClearSnooze, CheckForUpdates);
        _settingsForm = form;
        form.FormClosed += (_, _) => { form.Dispose(); _settingsForm = null; };
        form.Show();
        form.Activate();
    }

    // Brings the open settings window to the user's current virtual desktop and focuses it.
    void FocusSettingsOnCurrentDesktop()
    {
        var form = _settingsForm;
        if (form is null || form.IsDisposed || !form.IsHandleCreated) return;

        // If it's parked on another virtual desktop, move it onto the one the user is viewing —
        // otherwise Activate() below would drag the user over to the window's desktop instead.
        // Try the virtual-desktop manager first; if it can't move our window, recreating the
        // handle re-homes it onto the active desktop.
        if (!NativeMethods.IsWindowOnCurrentDesktop(form.Handle) &&
            !NativeMethods.TryMoveWindowToCurrentDesktop(form.Handle))
        {
            form.RehomeHandle();
        }
        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;
        form.Activate();
        form.BringToFront();
    }

    void OnSettingsChanged()
    {
        ReevaluateStatus();   // a new status text/emoji or a fresh/dropped connection may change what's shown
        RefreshUI();
    }

    void OnApplicationExit(object? s, EventArgs e)
    {
        if (_slackStatusSet)
            SlackClient.WithTokenAsync(_config, SlackClient.ClearStatusAsync).GetAwaiter().GetResult();
    }

    // ── Slack ─────────────────────────────────────────────────────────────────

    async Task SetSlackStatusAsync()
    {
        if (string.IsNullOrEmpty(_config.SlackToken)) return;
        try
        {
            _previousStatus = await SlackClient.WithTokenAsync(_config, SlackClient.GetStatusAsync);
            await SlackClient.WithTokenAsync(_config,
                t => SlackClient.SetStatusAsync(t, _config.StatusText, _config.StatusEmoji));
            _slackStatusSet = true;
        }
        catch (Exception ex)
        {
            _previousStatus = null;
            _tray.ShowBalloonTip(5_000, "Otter — Slack error", SlackErrorMessage(ex), ToolTipIcon.Error);
        }
    }

    async Task ClearSlackStatusAsync()
    {
        if (string.IsNullOrEmpty(_config.SlackToken)) return;
        try
        {
            var prev = _previousStatus;
            var restoreText  = prev?.Text       ?? "";
            var restoreEmoji = prev?.Emoji      ?? "";
            var restoreExp   = prev?.Expiration ?? 0;

            // If the captured "previous" status is in fact the one Otter set (a race or desync),
            // restoring it would re-apply our own status — clear instead.
            if (restoreText == _config.StatusText && restoreEmoji == _config.StatusEmoji)
            {
                await SlackClient.WithTokenAsync(_config, SlackClient.ClearStatusAsync);
                return;
            }

            // The status expired during the call: nothing left to restore, so clear.
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (restoreExp > 0 && restoreExp <= now)
            {
                await SlackClient.WithTokenAsync(_config, SlackClient.ClearStatusAsync);
                return;
            }

            await SlackClient.WithTokenAsync(_config,
                t => SlackClient.SetStatusAsync(t, restoreText, restoreEmoji, restoreExp));
        }
        catch { /* best-effort */ }
        finally
        {
            _slackStatusSet = false;
            _previousStatus = null;
        }
    }

    // A token that couldn't be refreshed means the Slack connection is gone (refresh token revoked or
    // lapsed) — point the user at Settings to reconnect rather than show a raw API error string.
    static string SlackErrorMessage(Exception ex) => ex is SlackAuthException
        ? "Your Slack connection expired. Open Settings to reconnect."
        : ex.Message;

    // ── Updates ─────────────────────────────────────────────────────────────────

    // Checks GitHub releases for a newer build and, if found, downloads and applies it (relaunching
    // Otter). Reachable from both the tray menu and the About page; _updateInProgress guards against a
    // second click racing a download already under way. A non-installed (dev) build has no Velopack
    // package to update, so CheckForUpdatesAsync throws and we surface that on the failure path.
    async void CheckForUpdates()
    {
        // A run is already in flight — just ignore the click.
        if (_updateInProgress) return;
        _updateInProgress = true;

        try
        {
            // Querying GitHub can take a few seconds; show an immediate balloon so the click feels
            // acknowledged rather than dead until the check resolves.
            _tray.ShowBalloonTip(3_000, "Otter", "Checking for updates…", ToolTipIcon.Info);

            var mgr = new UpdateManager(new GithubSource(AppInfo.RepoUrl, null, false));
            var update = await mgr.CheckForUpdatesAsync();
            if (update == null)
            {
                _tray.ShowBalloonTip(4_000, "Otter", "You're on the latest version.", ToolTipIcon.Info);
                return;
            }

            _tray.ShowBalloonTip(5_000, "Otter — Updating",
                $"Downloading v{update.TargetFullRelease.Version}…", ToolTipIcon.Info);

            // Close the settings window up front: the closing window is the visible signal the update
            // is under way, and it stops the button being clicked again mid-download. The tray stays
            // up so the message loop survives the awaits below — ApplyUpdatesAndRestart tears
            // everything down when it relaunches.
            if (_settingsForm is { IsDisposed: false })
                _settingsForm.Close();

            await mgr.DownloadUpdatesAsync(update);
            mgr.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(6_000, "Otter — Update Failed", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            // ApplyUpdatesAndRestart exits the process, so this only runs on the "up to date" or
            // failure paths — both of which should allow another check later.
            _updateInProgress = false;
        }
    }

    // ── UI refresh ────────────────────────────────────────────────────────────

    // Otter's current operating state, driving both the tray icon colour and the menu header.
    OtterState CurrentState =>
        !_config.Enabled            ? OtterState.Disabled :
        _config.IsSnoozed           ? OtterState.Snoozed  :
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

        _clearSnoozeItem.Enabled = _config.IsSnoozed;

        // Keep the tray toggle in step with the live config (the settings window can change it).
        _enabledItem.Checked = _config.Enabled;

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
