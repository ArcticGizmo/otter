namespace Otter;

/// <summary>
/// Otter's first-class settings window: a dark, resizable shell split into a fixed-width left
/// navigation rail and a fluid content area. The nav switches between pages — Getting started,
/// Slack, Status, and About — built entirely from the <see cref="Ui"/> control factory so they stay
/// visually consistent. Edits apply directly to the live <see cref="Config"/> and persist as the
/// user makes them — text fields commit when focus leaves, toggles and the Slack connection commit
/// instantly — so there is no Save/Cancel step. Each commit invokes <c>onChanged</c> so the caller
/// can persist the config and refresh the tray.
/// </summary>
class SettingsWindow : Form
{
    const int NavWidth = 168;
    const int PagePad  = 18;

    readonly Config _config;
    readonly Action _onChanged;
    readonly Action<int> _onSnooze;
    readonly Action _onClearSnooze;

    // The app's otter (transparent) for in-app imagery — the banner and About header.
    readonly Bitmap? _icon = Ui.LoadEmbeddedBitmap("Otter.icon.png");

    // Shell.
    FlowLayoutPanel _navPanel    = null!;
    Panel           _contentHost = null!;
    readonly Dictionary<string, FlowLayoutPanel> _pages = new();
    readonly List<(string key, Panel item, Label label, Panel accent)> _navItems = new();
    string _currentKey = "";

    readonly FluidLayout _fluid;

    // Slack page.
    TextBox _clientIdBox   = null!;
    Label   _connStatus    = null!;
    Button  _connectBtn    = null!;
    Button  _disconnectBtn = null!;
    Spinner _connSpinner   = null!;

    // Status page.
    TextBox _statusTextBox = null!;
    TextBox _emojiBox      = null!;
    Label   _statusPreview = null!;

    // Getting started echoes the same connection line.
    Label _startConn = null!;

    // Notifications page.
    ToggleSwitch _notificationsToggle = null!;
    Label        _snoozeStatus        = null!;
    Button       _clearSnoozeBtn      = null!;

    // Automation page.
    ToggleSwitch _runAtLoginToggle = null!;

    public SettingsWindow(Config config, Action onChanged, Action<int> onSnooze, Action onClearSnooze)
    {
        _config        = config;
        _onChanged     = onChanged;
        _onSnooze      = onSnooze;
        _onClearSnooze = onClearSnooze;
        _fluid = new FluidLayout(FluidWidth);

        Text            = "Otter Settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = true;
        MinimizeBox     = true;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Theme.FormBg;
        ForeColor       = Theme.Fg;
        Font            = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        MinimumSize     = new Size(640, 500);
        ClientSize      = new Size(760, 560);
        Icon = Ui.LoadEmbeddedIcon("Otter.icon.ico");

        BuildLayout();
        UpdateConnectionUI();
        UpdateStatusPreview();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        foreach (var page in _pages.Values)
            NativeMethods.UseDarkScrollBars(page.Handle);
        _fluid.Apply();
    }

    // ── Shell ─────────────────────────────────────────────────────────────────────
    void BuildLayout()
    {
        _navPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Left,
            Width         = NavWidth,
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            BackColor     = Theme.NavBg,
            Padding       = new Padding(0, 8, 0, 0),
        };

        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.FormBg };
        _contentHost.Resize += (_, _) => _fluid.Apply();

        AddPage("start",         "Getting started", BuildGettingStartedPage);
        AddPage("slack",         "Slack",           BuildSlackPage);
        AddPage("status",        "Status",          BuildStatusPage);
        AddPage("notifications", "Notifications",   BuildNotificationsPage);
        AddPage("automation",    "Automation",      BuildAutomationPage);
        AddPage("about",         "About",           BuildAboutPage);

        Controls.Add(_contentHost); // Fill added first…
        Controls.Add(_navPanel);    // …then the Left rail claims its edge and the content fills the rest.

        SelectPage("start");
    }

    // Persists the live config and lets the caller refresh the tray. Called from each control as the
    // user edits, so changes take effect immediately without a Save step.
    void Commit()
    {
        _config.Save();
        _onChanged();
    }

    // Builds a page panel, runs its content builder, and registers the matching nav item.
    void AddPage(string key, string title, Action<FlowLayoutPanel> build)
    {
        var page = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoScroll    = true,
            Padding       = new Padding(PagePad),
            BackColor     = Theme.FormBg,
            Visible       = false,
        };
        build(page);
        _pages[key] = page;
        _contentHost.Controls.Add(page);
        AddNavItem(key, title);
    }

    // A single nav rail entry: a left accent bar (shown when selected) and a left-aligned label.
    void AddNavItem(string key, string title)
    {
        var item = new Panel
        {
            Width     = NavWidth,
            Height    = 42,
            Margin    = new Padding(0),
            Cursor    = Cursors.Hand,
            BackColor = Theme.NavBg,
        };
        var accent = new Panel { Dock = DockStyle.Left, Width = 3, BackColor = Theme.Accent, Visible = false };
        var label  = new Label
        {
            Text      = title,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(16, 0, 8, 0),
            ForeColor = Theme.Muted,
            BackColor = Theme.NavBg,
            Font      = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
        };
        item.Controls.Add(label);
        item.Controls.Add(accent);

        void Select() => SelectPage(key);
        item.Click  += (_, _) => Select();
        label.Click += (_, _) => Select();

        void Enter()
        {
            if (_currentKey == key) return;
            item.BackColor = label.BackColor = Theme.ButtonBg;
        }
        void Leave()
        {
            if (_currentKey == key) return;
            item.BackColor = label.BackColor = Theme.NavBg;
        }
        item.MouseEnter  += (_, _) => Enter();
        item.MouseLeave  += (_, _) => Leave();
        label.MouseEnter += (_, _) => Enter();
        label.MouseLeave += (_, _) => Leave();

        _navPanel.Controls.Add(item);
        _navItems.Add((key, item, label, accent));
    }

    // Shows the chosen page (hiding the rest) and restyles the nav rail to mark it active.
    void SelectPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        _currentKey = key;

        foreach (var kv in _pages)
            kv.Value.Visible = kv.Key == key;
        page.BringToFront();

        foreach (var (k, item, label, accent) in _navItems)
        {
            bool sel = k == key;
            accent.Visible  = sel;
            item.BackColor  = label.BackColor = sel ? Theme.ButtonBg : Theme.NavBg;
            label.ForeColor = sel ? Theme.Title : Theme.Muted;
        }

        _fluid.Apply();
    }

    // The width available to full-width page controls: the content area minus page padding and a
    // reserved vertical-scrollbar gutter (so a scrolling page never also shows a horizontal bar).
    int FluidWidth()
    {
        if (_contentHost is null) return 400;
        int w = _contentHost.ClientSize.Width - PagePad * 2 - (SystemInformation.VerticalScrollBarWidth + 4);
        return Math.Max(200, w);
    }

    // ── Getting started ─────────────────────────────────────────────────────────────
    void BuildGettingStartedPage(FlowLayoutPanel page)
    {
        BuildBanner(page);

        page.Controls.Add(Ui.SectionTitle("What it does"));
        page.Controls.Add(Ui.BulletText(_fluid, "Watches for Microsoft Teams calls and sets your Slack status automatically."));
        page.Controls.Add(Ui.BulletText(_fluid, "Clears or restores your previous status the moment the call ends."));
        page.Controls.Add(Ui.BulletText(_fluid, "Snooze it for a while, or disable it entirely, from the tray icon."));

        page.Controls.Add(Ui.Separator(_fluid));

        page.Controls.Add(Ui.SectionTitle("Connection"));
        _startConn = Ui.BodyText(_fluid, "");
        page.Controls.Add(_startConn);
    }

    // Centred app banner: logo (when present), the app name, and the tagline.
    void BuildBanner(FlowLayoutPanel page)
    {
        var banner = new Panel { Height = _icon != null ? 150 : 86, Margin = new Padding(0, 8, 0, 8), BackColor = Theme.FormBg };

        PictureBox? pic = _icon != null
            ? new PictureBox { Image = _icon, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(64, 64) }
            : null;
        var name = new Label
        {
            Text      = "Otter",
            AutoSize  = true,
            ForeColor = Theme.Title,
            Font      = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Point),
        };
        var tag = new Label
        {
            Text      = "Hands-off Slack status — starting with Teams calls",
            AutoSize  = true,
            ForeColor = Theme.Muted,
            Font      = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
        };
        if (pic != null) banner.Controls.Add(pic);
        banner.Controls.Add(name);
        banner.Controls.Add(tag);

        void Layout()
        {
            int cx = banner.Width / 2;
            int y  = 8;
            if (pic != null) { pic.Location = new Point(cx - pic.Width / 2, y); y += pic.Height + 10; }
            name.Location = new Point(cx - name.Width / 2, y); y += name.Height + 4;
            tag.Location  = new Point(cx - tag.Width  / 2, y);
        }
        banner.Resize += (_, _) => Layout();
        _fluid.AddWidth(banner);
        Layout();

        page.Controls.Add(banner);
    }

    // ── Slack ───────────────────────────────────────────────────────────────────────
    void BuildSlackPage(FlowLayoutPanel page)
    {
        page.Controls.Add(Ui.SectionTitle("Slack connection"));
        page.Controls.Add(Ui.BodyText(_fluid,
            "Otter connects to your Slack workspace with your app's Client ID using a secure browser " +
            "sign-in (PKCE) — no client secret is stored."));

        page.Controls.Add(Ui.FieldCaption("Client ID"));
        _clientIdBox = Ui.MakeTextBox(_config.SlackClientId);
        _clientIdBox.Leave += (_, _) =>
        {
            var clientId = _clientIdBox.Text.Trim();
            if (clientId == _config.SlackClientId) return;
            _config.SlackClientId = clientId;
            Commit();
        };
        _fluid.AddWidth(_clientIdBox);
        page.Controls.Add(_clientIdBox);

        _connStatus = new Label
        {
            AutoSize = true,
            Margin   = new Padding(0, 2, 0, 8),
        };
        page.Controls.Add(_connStatus);

        var row = Ui.ButtonRow();
        _connectBtn = Ui.MakeButton("Connect Slack");
        _connectBtn.Click += OnConnect;
        _disconnectBtn = Ui.MakeButton("Disconnect");
        _disconnectBtn.Click += OnDisconnect;
        _connSpinner = new Spinner { Margin = new Padding(2, 6, 0, 0) };
        row.Controls.Add(_connectBtn);
        row.Controls.Add(_disconnectBtn);
        row.Controls.Add(_connSpinner);
        page.Controls.Add(row);

        page.Controls.Add(Ui.BodyText(_fluid,
            "Get your Client ID from api.slack.com/apps → your app → Basic Information. Add the redirect " +
            "URL shown in Otter's docs and the users.profile scopes before connecting."));
    }

    // ── Status ────────────────────────────────────────────────────────────────────────
    void BuildStatusPage(FlowLayoutPanel page)
    {
        page.Controls.Add(Ui.SectionTitle("Call status"));
        page.Controls.Add(Ui.BodyText(_fluid, "What Otter sets your Slack status to while you're on a Teams call."));

        page.Controls.Add(Ui.FieldCaption("Status text"));
        _statusTextBox = Ui.MakeTextBox(_config.StatusText);
        _fluid.AddWidth(_statusTextBox);
        _statusTextBox.TextChanged += (_, _) => UpdateStatusPreview();
        _statusTextBox.Leave += (_, _) =>
        {
            var text = _statusTextBox.Text.Trim();
            if (text == _config.StatusText) return;
            _config.StatusText = text;
            Commit();
        };
        page.Controls.Add(_statusTextBox);

        page.Controls.Add(Ui.FieldCaption("Emoji"));
        _emojiBox = Ui.MakeTextBox(_config.StatusEmoji);
        _emojiBox.Width = 220;
        _emojiBox.TextChanged += (_, _) => UpdateStatusPreview();
        _emojiBox.Leave += (_, _) =>
        {
            var emoji = _emojiBox.Text.Trim();
            if (emoji == _config.StatusEmoji) return;
            _config.StatusEmoji = emoji;
            Commit();
        };
        page.Controls.Add(_emojiBox);
        page.Controls.Add(Ui.FieldCaption("e.g. :headphones:"));

        page.Controls.Add(Ui.Separator(_fluid));

        page.Controls.Add(Ui.FieldCaption("Preview"));
        _statusPreview = new Label
        {
            AutoSize  = true,
            ForeColor = Theme.Fg,
            BackColor = Theme.CodeBg,
            Padding   = new Padding(10, 7, 12, 7),
            Font      = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            Margin    = new Padding(0, 0, 0, 8),
        };
        page.Controls.Add(_statusPreview);
    }

    void UpdateStatusPreview()
    {
        if (_statusPreview is null) return;
        var emoji = _emojiBox.Text.Trim();
        var text  = _statusTextBox.Text.Trim();
        _statusPreview.Text = string.IsNullOrEmpty(emoji) && string.IsNullOrEmpty(text)
            ? "(no status)"
            : $"{emoji}  {text}".Trim();
    }

    // ── Notifications ───────────────────────────────────────────────────────────────
    void BuildNotificationsPage(FlowLayoutPanel page)
    {
        _notificationsToggle = Ui.MakeToggle();
        _notificationsToggle.Checked = _config.NotificationsEnabled;
        _notificationsToggle.CheckedChanged += (_, _) =>
        {
            if (_notificationsToggle.Checked == _config.NotificationsEnabled) return;
            _config.NotificationsEnabled = _notificationsToggle.Checked;
            Commit();
        };
        page.Controls.Add(Ui.TitleRow(_fluid, "Show notifications", _notificationsToggle));

        page.Controls.Add(Ui.BodyText(_fluid,
            "Show a brief desktop notification when Otter detects a call and updates your Slack status. " +
            "Turn this off to update your status silently."));

        page.Controls.Add(Ui.Separator(_fluid));

        page.Controls.Add(Ui.SectionTitle("Snooze"));
        page.Controls.Add(Ui.BodyText(_fluid,
            "Pause Otter for a while. Your Slack status is cleared and won't be updated again until the " +
            "snooze ends."));

        var row = Ui.ButtonRow();
        var b30  = Ui.MakeButton("30 minutes"); b30.Click  += (_, _) => DoSnooze(30);
        var b60  = Ui.MakeButton("1 hour");     b60.Click  += (_, _) => DoSnooze(60);
        var b120 = Ui.MakeButton("2 hours");    b120.Click += (_, _) => DoSnooze(120);
        row.Controls.Add(b30);
        row.Controls.Add(b60);
        row.Controls.Add(b120);
        page.Controls.Add(row);

        var clearRow = Ui.ButtonRow();
        _clearSnoozeBtn = Ui.MakeButton("Clear snooze");
        _clearSnoozeBtn.Click += (_, _) => { _onClearSnooze(); UpdateSnoozeUI(); };
        clearRow.Controls.Add(_clearSnoozeBtn);
        page.Controls.Add(clearRow);

        _snoozeStatus = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        page.Controls.Add(_snoozeStatus);

        UpdateSnoozeUI();
    }

    void DoSnooze(int minutes)
    {
        _onSnooze(minutes);
        UpdateSnoozeUI();
    }

    void UpdateSnoozeUI()
    {
        bool snoozed = _config.SnoozedUntil.HasValue && _config.SnoozedUntil.Value > DateTime.UtcNow;
        if (snoozed)
        {
            var until = _config.SnoozedUntil!.Value.ToLocalTime();
            _snoozeStatus.Text      = $"Snoozed until {until:h:mm tt}";
            _snoozeStatus.ForeColor = Theme.Accent;
        }
        else
        {
            _snoozeStatus.Text      = "Not snoozed";
            _snoozeStatus.ForeColor = Theme.Muted;
        }
        _clearSnoozeBtn.Enabled = snoozed;
    }

    // ── Automation ────────────────────────────────────────────────────────────────
    void BuildAutomationPage(FlowLayoutPanel page)
    {
        _runAtLoginToggle = Ui.MakeToggle();
        _runAtLoginToggle.Checked = Startup.IsEnabled();
        // Run-at-login lives in the registry, not Config, so apply it straight away on toggle.
        _runAtLoginToggle.CheckedChanged += (_, _) => Startup.SetEnabled(_runAtLoginToggle.Checked);
        page.Controls.Add(Ui.TitleRow(_fluid, "Start at login", _runAtLoginToggle));

        page.Controls.Add(Ui.BodyText(_fluid,
            "Launch Otter in the background when you sign in to Windows, so your Slack status is " +
            "managed without you having to start it yourself."));
        page.Controls.Add(Ui.BodyText(_fluid,
            "Otter registers its current location. If you move or reinstall the app, toggle this off " +
            "and on again to refresh it."));
    }

    // ── About ───────────────────────────────────────────────────────────────────────
    void BuildAboutPage(FlowLayoutPanel page)
    {
        page.Controls.Add(Ui.SectionTitle("About"));

        var header = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 0, 0, 6),
        };
        if (_icon != null)
            header.Controls.Add(new PictureBox
            {
                Image    = _icon,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size     = new Size(32, 32),
                Margin   = new Padding(0, 0, 10, 0),
            });
        header.Controls.Add(new Label
        {
            Text      = $"Otter\nv{AppInfo.Version}",
            AutoSize  = true,
            ForeColor = Theme.Fg,
            Margin    = new Padding(0, 2, 0, 0),
        });
        page.Controls.Add(header);

        if (!string.IsNullOrEmpty(AppInfo.RepoUrl))
        {
            page.Controls.Add(Ui.LinkRow("GitHub repository", AppInfo.RepoUrl));
            page.Controls.Add(Ui.LinkRow("Report an issue", AppInfo.IssuesUrl));
        }
        else
        {
            page.Controls.Add(Ui.BodyText(_fluid, "Local build — repository links appear once Otter is published."));
        }

        page.Controls.Add(Ui.Separator(_fluid));

        page.Controls.Add(Ui.SectionTitle("Updates"));
        page.Controls.Add(Ui.BodyText(_fluid, $"Currently running v{AppInfo.Version}. Automatic updates arrive in a later release."));
    }

    // ── Connection state ────────────────────────────────────────────────────────────
    void UpdateConnectionUI()
    {
        bool connected = !string.IsNullOrEmpty(_config.SlackToken);

        if (connected)
        {
            _connStatus.Text      = $"✓  Connected to {_config.SlackTeamName}";
            _connStatus.ForeColor = Theme.Green;
            _connectBtn.Text      = "Reconnect";
        }
        else
        {
            _connStatus.Text      = "Not connected";
            _connStatus.ForeColor = Theme.Muted;
            _connectBtn.Text      = "Connect Slack";
        }
        _disconnectBtn.Enabled = connected;

        if (_startConn != null)
        {
            _startConn.Text      = connected ? $"Connected to {_config.SlackTeamName}." : "Not connected yet — set up Slack on the Slack page.";
            _startConn.ForeColor = connected ? Theme.Fg : Theme.Muted;
        }
    }

    async void OnConnect(object? s, EventArgs e)
    {
        var clientId = _clientIdBox.Text.Trim();
        if (string.IsNullOrEmpty(clientId))
        {
            MessageBox.Show(
                "Enter your Slack app's Client ID first.\n\n" +
                "Get it from api.slack.com/apps → your app → Basic Information.",
                "Otter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _connectBtn.Enabled = false;
        _disconnectBtn.Enabled = false;
        _connSpinner.Spinning = true;

        try
        {
            var (token, teamName) = await SlackClient.RunOAuthFlowAsync(clientId);
            _config.SlackToken    = token;
            _config.SlackTeamName = teamName;
            _config.SlackClientId = clientId;
            Commit();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not connect to Slack:\n{ex.Message}",
                "Otter", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _connSpinner.Spinning = false;
            _connectBtn.Enabled = true;
            UpdateConnectionUI();
        }
    }

    void OnDisconnect(object? s, EventArgs e)
    {
        _config.SlackToken    = "";
        _config.SlackTeamName = "";
        Commit();
        UpdateConnectionUI();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon?.Dispose();
        base.Dispose(disposing);
    }
}
