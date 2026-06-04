using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Sleams;

class TrayApp : IDisposable
{
    Config _config;
    bool _slackStatusSet;
    SlackClient.SlackStatus? _previousStatus;

    readonly NotifyIcon _tray;
    readonly AudioMonitor _monitor;

    // Menu items updated dynamically
    readonly ToolStripMenuItem _statusItem;
    readonly ToolStripMenuItem _enabledItem;
    readonly ToolStripMenuItem _clearSnoozeItem;

    public TrayApp()
    {
        _config  = Config.Load();
        _monitor = new AudioMonitor("ms-teams");
        _monitor.CallStarted += OnCallStarted;
        _monitor.CallEnded   += OnCallEnded;

        // ── Context menu ──────────────────────────────────────────────────────

        _statusItem = new ToolStripMenuItem("● Monitoring") { Enabled = false };

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
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit", null, (_, _) => Application.Exit()),
        });

        _tray = new NotifyIcon
        {
            Text             = "Sleams",
            ContextMenuStrip = menu,
            Visible          = true,
        };
        _tray.DoubleClick += OnOpenSettings;

        Application.ApplicationExit += OnApplicationExit;

        RefreshUI();
    }

    public void Start() => _monitor.Start();

    // ── State helpers ─────────────────────────────────────────────────────────

    bool IsActive  => _config.Enabled && !IsSnoozed;
    bool IsSnoozed => _config.SnoozedUntil.HasValue && _config.SnoozedUntil.Value > DateTime.UtcNow;

    // ── Audio events (background thread → marshal to UI) ──────────────────────

    void OnCallStarted()
    {
        RunOnUiThread(() =>
        {
            if (!IsActive) return;
            _ = SetSlackStatusAsync();
            _tray.ShowBalloonTip(3_000, "Sleams", "Teams call detected — updating Slack status.", ToolTipIcon.Info);
            RefreshUI();
        });
    }

    void OnCallEnded()
    {
        RunOnUiThread(() =>
        {
            if (_slackStatusSet) _ = ClearSlackStatusAsync();
            RefreshUI();
        });
    }

    // ── Menu handlers ─────────────────────────────────────────────────────────

    void OnToggleEnabled(object? s, EventArgs e)
    {
        _config.Enabled = _enabledItem.Checked;
        if (!_config.Enabled && _slackStatusSet) _ = ClearSlackStatusAsync();
        _config.Save();
        RefreshUI();
    }

    void Snooze(int minutes)
    {
        _config.SnoozedUntil = DateTime.UtcNow.AddMinutes(minutes);
        if (_slackStatusSet) _ = ClearSlackStatusAsync();
        _config.Save();
        RefreshUI();

        // Auto-expire the snooze
        _ = Task.Delay(TimeSpan.FromMinutes(minutes)).ContinueWith(_ =>
        {
            if (IsSnoozed) return; // already manually cleared
            _config.SnoozedUntil = null;
            RunOnUiThread(RefreshUI);
        });
    }

    void OnClearSnooze(object? s, EventArgs e)
    {
        _config.SnoozedUntil = null;
        _config.Save();
        RefreshUI();
    }

    void OnOpenSettings(object? s, EventArgs e)
    {
        using var form = new SettingsWindow(_config);
        if (form.ShowDialog() != DialogResult.OK) return;

        _config = form.Result;
        _config.Save();
        _enabledItem.Checked = _config.Enabled;
        RefreshUI();
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
            _tray.ShowBalloonTip(5_000, "Sleams — Slack error", ex.Message, ToolTipIcon.Error);
        }
    }

    async Task ClearSlackStatusAsync()
    {
        if (string.IsNullOrEmpty(_config.SlackToken)) return;
        try
        {
            var prev = _previousStatus;

            // If the previous status matches what Sleams set (race condition / desync),
            // treat it as empty so we don't re-apply a stale Sleams status.
            var restoreText  = prev?.Text       ?? "";
            var restoreEmoji = prev?.Emoji      ?? "";
            var restoreExp   = prev?.Expiration ?? 0;
            if (restoreText == _config.StatusText && restoreEmoji == _config.StatusEmoji)
            {
                restoreText  = "";
                restoreEmoji = "";
                restoreExp   = 0;
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

    void RefreshUI()
    {
        // Determine visual state
        string label;
        Color  dot;
        string tooltip;

        if (!_config.Enabled)
        {
            label   = "Disabled";
            dot     = Color.Silver;
            tooltip = "Sleams — disabled";
        }
        else if (IsSnoozed)
        {
            var until = _config.SnoozedUntil!.Value.ToLocalTime();
            label   = $"Snoozed until {until:h:mm tt}";
            dot     = Color.SteelBlue;
            tooltip = $"Sleams — snoozed until {until:h:mm tt}";
        }
        else if (_monitor.IsInCall)
        {
            label   = "On a Teams call";
            dot     = Color.OrangeRed;
            tooltip = "Sleams — on a call";
        }
        else
        {
            label   = "Monitoring";
            dot     = Color.LimeGreen;
            tooltip = "Sleams — monitoring";
        }

        _statusItem.Text = $"● {label}";
        _clearSnoozeItem.Enabled = IsSnoozed;

        // Replace tray icon
        var oldIcon = _tray.Icon;
        _tray.Icon  = CreateDotIcon(dot);
        oldIcon?.Dispose();

        // NotifyIcon.Text is capped at 63 characters
        _tray.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    static Icon CreateDotIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var fill = new SolidBrush(color);
        g.FillEllipse(fill, 1, 1, 13, 13);

        // Subtle shadow ring
        using var ring = new Pen(Color.FromArgb(60, 0, 0, 0), 1f);
        g.DrawEllipse(ring, 1, 1, 13, 13);

        // Sheen highlight
        using var shine = new SolidBrush(Color.FromArgb(80, 255, 255, 255));
        g.FillEllipse(shine, 4, 3, 6, 4);

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
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
        _monitor.Dispose();
        _tray.Visible = false;
        _tray.Icon?.Dispose();
        _tray.Dispose();
    }
}
