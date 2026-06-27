namespace Otter;

using System.Text.RegularExpressions;

/// <summary>
/// Otter's first-class settings window: a dark, resizable shell split into a fixed-width left
/// navigation rail and a fluid content area. The nav switches between pages — Getting started, Slack
/// Status, Detection, Snooze, and About — built entirely from the <see cref="Ui"/> control factory so
/// they stay visually consistent. Edits apply directly to the live <see cref="Config"/> and persist as
/// the user makes them — text fields commit when focus leaves, toggles and the Slack connection commit
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
    readonly Action _onCheckForUpdates;

    // The app's otter (transparent) for in-app imagery — the banner and About header.
    readonly Bitmap? _icon = Ui.LoadEmbeddedBitmap("Otter.icon.png");

    // Shell.
    FlowLayoutPanel _navPanel    = null!;
    Panel           _contentHost = null!;
    readonly Dictionary<string, FlowLayoutPanel> _pages = new();
    readonly List<(string key, Panel item, Label label, Panel accent)> _navItems = new();
    string _currentKey = "";

    readonly FluidLayout _fluid;

    // Slack Workspace section (on the Getting started page).
    Label   _connStatus    = null!;
    Button  _connectBtn    = null!;
    Button  _disconnectBtn = null!;
    Spinner _connSpinner   = null!;

    // Status page.
    TextBox    _statusTextBox = null!;
    TextBox    _emojiBox      = null!;
    Label      _statusPreview = null!;
    PictureBox _previewEmoji  = null!;

    // Workspace custom-emoji catalogue (list + cached thumbnails), shared by the emoji autocomplete
    // and the status preview.
    readonly EmojiStore _emojiStore = new();
    EmojiAutocomplete? _emojiAutocomplete;

    // Snooze page.
    Label        _snoozeStatus   = null!;
    Button       _clearSnoozeBtn = null!;

    // Start-at-login toggle (on the Getting started page).
    ToggleSwitch _runAtLoginToggle = null!;

    // Detection page.
    readonly IMicUsageFeed _feed;
    FlowLayoutPanel _productList = null!;
    FlowLayoutPanel _micLog      = null!;
    ToggleSwitch    _trackToggle = null!;

    public SettingsWindow(Config config, Action onChanged, Action<int> onSnooze, Action onClearSnooze,
        Action onCheckForUpdates, IMicUsageFeed feed)
    {
        _config            = config;
        _onChanged         = onChanged;
        _onSnooze          = onSnooze;
        _onClearSnooze     = onClearSnooze;
        _onCheckForUpdates = onCheckForUpdates;
        _feed              = feed;
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

        // A late-arriving thumbnail (or a completed catalogue refresh) should refresh the preview.
        _emojiStore.Updated += () =>
        {
            if (IsHandleCreated) BeginInvoke(UpdateStatusPreview);
        };

        // New mic-usage observations arrive on the signal's poll thread — refresh the live log (only
        // while the Detection page is the one on screen) on the UI thread.
        _feed.CapturesChanged += OnCapturesChanged;
    }

    void OnCapturesChanged()
    {
        if (_currentKey == "detection" && IsHandleCreated) BeginInvoke(RefreshMicLog);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    // Recreates the native window handle. A window's virtual-desktop association is fixed to its
    // HWND, so when the tray can't move the window across desktops any other way, recreating the
    // handle re-homes it: the new HWND is born on whichever desktop the user is currently viewing.
    public void RehomeHandle() => RecreateHandle();

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        foreach (var page in _pages.Values)
            NativeMethods.UseDarkScrollBars(page.Handle);
        _fluid.Apply();
        ApplyDetectionWidths();

        // Pull the latest workspace emoji in the background; the disk cache already backs autocomplete
        // until this lands. Fire-and-forget — RefreshAsync swallows offline/missing-scope failures.
        if (!string.IsNullOrEmpty(_config.SlackToken))
            _ = RefreshWorkspaceEmojiAsync();
    }

    // Refreshes the workspace emoji catalogue, first ensuring the access token is current so a session
    // that's been idle past the ~12h token lifetime still works. Failures are non-fatal — the disk
    // cache keeps backing autocomplete.
    async Task RefreshWorkspaceEmojiAsync()
    {
        try
        {
            var token = await SlackClient.GetValidTokenAsync(_config);
            await _emojiStore.RefreshAsync(token);
        }
        catch { /* offline, or the connection lapsed — last good disk cache still serves autocomplete */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        CommitPendingEdits();   // closing the window doesn't blur the active field — flush it first
        base.OnFormClosing(e);
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
        AddPage("status",        "Slack Status",    BuildStatusPage);
        AddPage("detection",     "Detection",       BuildDetectionPage);
        AddPage("snooze",        "Snooze",          BuildSnoozePage);
        AddPage("about",         "About",           BuildAboutPage);
        AddPage("changelog",     "Changelog",       BuildChangelogPage);

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

    // Flushes text the user typed but hasn't blurred out of yet. TextBox.Leave commits on focus change,
    // but the nav labels aren't focusable — so clicking another page or the window's close button never
    // blurs the active field, and without this the pending edit would be lost. Commits only on a change.
    void CommitPendingEdits()
    {
        bool changed = false;

        var text = _statusTextBox?.Text.Trim();
        if (text != null && text != _config.StatusText) { _config.StatusText = text; changed = true; }

        var emoji = _emojiBox?.Text.Trim();
        if (emoji != null && emoji != _config.StatusEmoji) { _config.StatusEmoji = emoji; changed = true; }

        if (changed) Commit();
    }

    // Builds a page panel, runs its content builder, and registers the matching nav item. Pass
    // nested: true for a child entry (indented beneath the preceding top-level item).
    void AddPage(string key, string title, Action<FlowLayoutPanel> build, bool nested = false)
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
        AddNavItem(key, title, nested);
    }

    // A single nav rail entry: a left accent bar (shown when selected) and a left-aligned label.
    // Nested entries sit a row shorter and indented, reading as children of the item above them.
    void AddNavItem(string key, string title, bool nested = false)
    {
        var item = new Panel
        {
            Width     = NavWidth,
            Height    = nested ? 34 : 42,
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
            Padding   = new Padding(nested ? 34 : 16, 0, 8, 0),
            ForeColor = Theme.Muted,
            BackColor = Theme.NavBg,
            Font      = new Font("Segoe UI", nested ? 9.5f : 10f, FontStyle.Regular, GraphicsUnit.Point),
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
        CommitPendingEdits();   // flush any unsaved text before leaving the current page
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
        ApplyDetectionWidths();

        // The live log may have moved on while another page was showing (we skip refreshes when the
        // Detection page is hidden) — bring it current as the user lands on it.
        if (key == "detection") RefreshMicLog();
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
        page.Controls.Add(Ui.BulletText(_fluid, "Watches for calls (Teams, Zoom, Discord, and any app you add) and sets your Slack status automatically."));
        page.Controls.Add(Ui.BulletText(_fluid, "Clears or restores your previous status the moment the call ends."));
        page.Controls.Add(Ui.BulletText(_fluid, "Snooze it for a while, or disable it entirely, from the tray icon."));

        page.Controls.Add(Ui.Separator(_fluid));

        page.Controls.Add(Ui.SectionTitle("Slack Workspace"));

        page.Controls.Add(Ui.BodyText(_fluid,
            "Connect Otter to your Slack workspace. Your browser opens for sign-in — approve Otter and " +
            "you're done; nothing to copy or paste."));

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

        page.Controls.Add(Ui.Separator(_fluid));

        _runAtLoginToggle = Ui.MakeToggle();
        _runAtLoginToggle.Checked = Startup.IsEnabled();
        // Run-at-login lives in the registry, not Config, so apply it straight away on toggle.
        _runAtLoginToggle.CheckedChanged += (_, _) => Startup.SetEnabled(_runAtLoginToggle.Checked);
        page.Controls.Add(Ui.TitleRow(_fluid, "Start at login", _runAtLoginToggle));

        page.Controls.Add(Ui.BodyText(_fluid,
            "Launch Otter in the background when you sign in to Windows, so your Slack status is " +
            "managed without you having to start it yourself."));
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
            Text      = "Hands-off Slack status for your calls",
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

    // ── Slack Status ──────────────────────────────────────────────────────────────────
    void BuildStatusPage(FlowLayoutPanel page)
    {
        page.Controls.Add(Ui.SectionTitle("Call status"));
        page.Controls.Add(Ui.BodyText(_fluid, "What Otter sets your Slack status to while you're on a call."));

        page.Controls.Add(Ui.FieldCaption("Status text"));
        _statusTextBox = Ui.MakeTextBox(_config.StatusText);
        _fluid.AddWidth(_statusTextBox);
        _statusTextBox.TextChanged += (_, _) => UpdateStatusPreview();
        _statusTextBox.Leave += (_, _) => CommitPendingEdits();
        page.Controls.Add(_statusTextBox);

        page.Controls.Add(Ui.FieldCaption("Emoji"));
        _emojiBox = Ui.MakeTextBox(_config.StatusEmoji);
        _emojiBox.Width = 220;
        _emojiBox.TextChanged += (_, _) => UpdateStatusPreview();
        _emojiBox.Leave += (_, _) => CommitPendingEdits();
        // Slack-style autocomplete: type ':' + a name to pick from the workspace's custom emoji.
        _emojiAutocomplete = new EmojiAutocomplete(_emojiBox, _emojiStore);
        page.Controls.Add(_emojiBox);
        page.Controls.Add(Ui.FieldCaption("Type ':' to search your workspace emoji — e.g. :headphones:"));

        page.Controls.Add(Ui.Separator(_fluid));

        page.Controls.Add(Ui.FieldCaption("Preview"));
        // The preview is a thumbnail (for custom emoji we can render) followed by the status line.
        var previewRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            BackColor     = Theme.CodeBg,
            Padding       = new Padding(10, 7, 12, 7),
            Margin        = new Padding(0, 0, 0, 8),
        };
        _previewEmoji = new PictureBox
        {
            Size     = new Size(20, 20),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin   = new Padding(0, 0, 6, 0),
            Visible  = false,
        };
        _statusPreview = new Label
        {
            AutoSize  = true,
            ForeColor = Theme.Fg,
            BackColor = Theme.CodeBg,
            Font      = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            Margin    = new Padding(0),
            Padding   = new Padding(0, 2, 0, 0),
        };
        previewRow.Controls.Add(_previewEmoji);
        previewRow.Controls.Add(_statusPreview);
        page.Controls.Add(previewRow);
    }

    // ── Detection ─────────────────────────────────────────────────────────────────────
    void BuildDetectionPage(FlowLayoutPanel page)
    {
        page.Controls.Add(Ui.SectionTitle("Detected apps"));
        page.Controls.Add(Ui.BodyText(_fluid,
            "Otter sets your status when any enabled app below is using the microphone. Match terms are " +
            "comma-separated and matched as case-insensitive substrings of the app's name (e.g. \"teams\", " +
            "\"zoom\"). Turn an app off to ignore it."));

        _productList = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 4, 0, 4),
        };
        foreach (var p in _config.DetectionProducts)
            _productList.Controls.Add(BuildProductRow(p));
        page.Controls.Add(_productList);

        var addRow = Ui.ButtonRow();
        var addBtn = Ui.MakeButton("Add app");
        addBtn.Click += (_, _) =>
        {
            var p = new DetectionProduct { Name = "", Match = "", Enabled = true };
            _config.DetectionProducts.Add(p);
            var row = BuildProductRow(p);
            _productList.Controls.Add(row);
            ApplyDetectionWidths();
            Commit();
        };
        addRow.Controls.Add(addBtn);
        page.Controls.Add(addRow);

        page.Controls.Add(Ui.Separator(_fluid));

        _trackToggle = Ui.MakeToggle();
        _trackToggle.Checked = _config.TrackMicUsage;   // set before wiring so this doesn't fire a commit
        _trackToggle.CheckedChanged += (_, _) =>
        {
            _config.TrackMicUsage = _trackToggle.Checked;
            Commit();             // OnSettingsChanged pushes TrackingEnabled to the live signal
            RefreshMicLog();
        };
        page.Controls.Add(Ui.TitleRow(_fluid, "Track mic usage", _trackToggle));
        page.Controls.Add(Ui.BodyText(_fluid,
            "Logs apps recently seen using your microphone, so you can spot anything Otter isn't matching " +
            "yet. Green means it's already matched; otherwise use Quick add to start detecting it."));

        _micLog = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = new Padding(0, 4, 0, 0),
        };
        page.Controls.Add(_micLog);
        RefreshMicLog();
    }

    // One editable product: enable toggle, name, comma-separated match terms, and a remove button.
    // Laid out manually (rather than via FluidLayout) so rows can be added/removed without leaving
    // stale references behind; ApplyDetectionWidths drives the fill width on resize.
    Panel BuildProductRow(DetectionProduct p)
    {
        var row = new Panel { Height = 52, Margin = new Padding(0, 0, 0, 6), BackColor = Theme.CodeBg };

        var toggle = Ui.MakeToggle();
        toggle.BackColor = row.BackColor;   // the toggle paints its own bg — match the row so it isn't a dark cut-out
        toggle.Checked = p.Enabled;   // set before wiring so this doesn't fire a commit during build
        toggle.CheckedChanged += (_, _) => { p.Enabled = toggle.Checked; Commit(); RefreshMicLog(); };

        var nameCap = Ui.FieldCaption("Name");
        var nameBox = Ui.MakeTextBox(p.Name);
        nameBox.Leave += (_, _) =>
        {
            var t = nameBox.Text.Trim();
            if (t != p.Name) { p.Name = t; Commit(); }
        };

        var matchCap = Ui.FieldCaption("Match terms");
        var matchBox = Ui.MakeTextBox(p.Match);
        matchBox.Leave += (_, _) =>
        {
            var t = matchBox.Text.Trim();
            if (t != p.Match) { p.Match = t; Commit(); RefreshMicLog(); }
        };

        var remove = Ui.MakeButton("Remove");
        remove.Click += (_, _) =>
        {
            _config.DetectionProducts.Remove(p);
            _productList.Controls.Remove(row);
            row.Dispose();
            Commit();
            RefreshMicLog();
        };

        row.Controls.Add(toggle);
        row.Controls.Add(nameCap);
        row.Controls.Add(nameBox);
        row.Controls.Add(matchCap);
        row.Controls.Add(matchBox);
        row.Controls.Add(remove);

        void Layout()
        {
            const int pad = 8, nameW = 150;
            toggle.Location = new Point(pad, (row.Height - toggle.Height) / 2);
            int left = pad + toggle.Width + 12;
            remove.Location = new Point(row.Width - remove.Width - pad, (row.Height - remove.Height) / 2);

            nameCap.Location = new Point(left, 5);
            nameBox.Location = new Point(left, 22);
            nameBox.Width    = nameW;

            int matchLeft = left + nameW + 12;
            matchCap.Location = new Point(matchLeft, 5);
            matchBox.Location = new Point(matchLeft, 22);
            matchBox.Width    = Math.Max(60, remove.Location.X - 12 - matchLeft);
        }
        row.Resize += (_, _) => Layout();
        row.Width = FluidWidth();
        Layout();
        return row;
    }

    // Rebuilds the live mic-usage log from the feed, colouring matched apps green and offering a
    // quick-add on the rest. Cheap to call on any change (toggle, edit, new observation).
    void RefreshMicLog()
    {
        if (_micLog is null) return;

        _micLog.SuspendLayout();
        foreach (Control c in _micLog.Controls) c.Dispose();
        _micLog.Controls.Clear();

        if (!_config.TrackMicUsage)
            _micLog.Controls.Add(Ui.FieldCaption("Turn on to start logging microphone usage."));
        else
        {
            var caps = _feed.RecentCaptures;
            if (caps.Count == 0)
                _micLog.Controls.Add(Ui.FieldCaption("No microphone usage seen yet."));
            else
                foreach (var cap in caps)
                    _micLog.Controls.Add(BuildLogRow(cap, Matches(cap.Identifier)));
        }

        _micLog.ResumeLayout();
        ApplyDetectionWidths();
    }

    Panel BuildLogRow(MicCapture cap, bool matched)
    {
        var row = new Panel { Height = 30, Margin = new Padding(0, 0, 0, 4), BackColor = Theme.FormBg };
        var label = new Label
        {
            Text      = matched ? $"{cap.Identifier}  ✓ matched" : cap.Identifier,
            AutoSize  = true,
            ForeColor = matched ? Theme.Green : Theme.Fg,
            Location  = new Point(0, 7),
        };
        row.Controls.Add(label);

        if (!matched)
        {
            var add = Ui.MakeButton("Quick add");
            add.Click += (_, _) => QuickAdd(cap.Identifier);
            row.Controls.Add(add);
            void Layout() => add.Location = new Point(row.Width - add.Width, (row.Height - add.Height) / 2);
            row.Resize += (_, _) => Layout();
            row.Width = FluidWidth();
            Layout();
        }
        return row;
    }

    // Adds a new enabled product from a logged app, using the app's identifier as the match term and a
    // tidied form as the name. Refreshes both lists so the log entry immediately turns green.
    void QuickAdd(string identifier)
    {
        var name = identifier.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? identifier[..^4]
            : identifier;

        var p = new DetectionProduct { Name = name, Match = identifier, Enabled = true };
        _config.DetectionProducts.Add(p);
        _productList.Controls.Add(BuildProductRow(p));
        Commit();
        ApplyDetectionWidths();
        RefreshMicLog();
    }

    // True if an app identifier matches any enabled product's terms, using the same case-insensitive
    // substring rule as the signal — so log rows reflect detection exactly.
    bool Matches(string identifier)
    {
        var id = identifier.ToLowerInvariant();
        return _config.DetectionProducts
            .Where(p => p.Enabled)
            .SelectMany(p => p.Terms)
            .Any(t => id.Contains(t.ToLowerInvariant()));
    }

    // Fills the dynamic Detection rows (which bypass FluidLayout) to the available width on resize.
    void ApplyDetectionWidths()
    {
        int w = FluidWidth();
        if (_productList is not null)
            foreach (Control c in _productList.Controls) c.Width = w;
        if (_micLog is not null)
            foreach (Control c in _micLog.Controls) c.Width = w;
    }

    void UpdateStatusPreview()
    {
        if (_statusPreview is null) return;
        var emoji = _emojiBox.Text.Trim();
        var text  = _statusTextBox.Text.Trim();

        // If the emoji resolves to a workspace custom emoji we have (or can fetch) an image for, show
        // the thumbnail and drop the raw :code: from the text; otherwise fall back to the literal code.
        Image? img = string.IsNullOrEmpty(emoji) ? null : _emojiStore.GetImageCached(emoji);
        if (_previewEmoji is not null)
        {
            _previewEmoji.Image   = img;
            _previewEmoji.Visible = img is not null;
        }

        if (string.IsNullOrEmpty(emoji) && string.IsNullOrEmpty(text))
            _statusPreview.Text = "(no status)";
        else if (img is not null)
            _statusPreview.Text = text.Length > 0 ? text : "(no status text)";
        else
            _statusPreview.Text = $"{emoji}  {text}".Trim();
    }

    // ── Snooze ──────────────────────────────────────────────────────────────────────
    void BuildSnoozePage(FlowLayoutPanel page)
    {
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
        bool snoozed = _config.IsSnoozed;
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
        page.Controls.Add(Ui.BodyText(_fluid, $"Currently running v{AppInfo.Version}."));

        var updateRow = Ui.ButtonRow();
        updateRow.Margin = new Padding(0, 4, 0, 4);
        var checkBtn = Ui.MakeButton("Check for updates");
        checkBtn.Click += (_, _) => _onCheckForUpdates();
        updateRow.Controls.Add(checkBtn);
        page.Controls.Add(updateRow);
    }

    // ── Changelog ─────────────────────────────────────────────────────────────────
    // Renders the embedded CHANGELOG.md into the page using the same factory controls as every other
    // page. Handles the subset of markdown that file actually uses: H1/H2/H3 headings, bullet lists,
    // blockquotes, thematic breaks, and inline emphasis/links.
    void BuildChangelogPage(FlowLayoutPanel page)
    {
        var markdown = Ui.LoadEmbeddedText("Otter.CHANGELOG.md");
        if (markdown is null)
        {
            page.Controls.Add(Ui.BodyText(_fluid, "Changelog not available."));
            return;
        }

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("## "))
                page.Controls.Add(Ui.SectionTitle(StripInlineMarkdown(line[3..])));
            else if (line.StartsWith("### "))
                page.Controls.Add(Ui.SubHeading(StripInlineMarkdown(line[4..])));
            else if (line.StartsWith("# "))
                { /* top-level title — the nav label and page header already say "Changelog" */ }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                page.Controls.Add(Ui.BulletText(_fluid, StripInlineMarkdown(line[2..])));
            else if (line == "---")
                page.Controls.Add(Ui.Separator(_fluid));
            else if (line.StartsWith("> "))
                page.Controls.Add(Ui.BlockQuote(_fluid, StripInlineMarkdown(line[2..])));
            else if (line.Trim().Length > 0)
                page.Controls.Add(Ui.BodyText(_fluid, StripInlineMarkdown(line)));
        }
    }

    // Strips the inline markdown patterns that appear in CHANGELOG.md: bold, italic, inline code, and
    // [text](url) links. Bare [brackets] (the version tags in headings) are intentionally left alone.
    static string StripInlineMarkdown(string text)
    {
        text = Regex.Replace(text, @"\*\*(.*?)\*\*",         "$1");  // **bold**
        text = Regex.Replace(text, @"__(.*?)__",             "$1");  // __bold__
        text = Regex.Replace(text, @"\*(.*?)\*",             "$1");  // *italic*
        text = Regex.Replace(text, @"`([^`]+)`",             "$1");  // `inline code`
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");  // [text](url)
        return text;
    }

    // ── Connection state ────────────────────────────────────────────────────────────
    void UpdateConnectionUI()
    {
        bool connected = !string.IsNullOrEmpty(_config.SlackToken);

        if (connected)
        {
            // oauth.v2.user.access may not return a team name; fall back to a plain "Connected".
            _connStatus.Text      = string.IsNullOrEmpty(_config.SlackTeamName)
                ? "✓  Connected to Slack"
                : $"✓  Connected to {_config.SlackTeamName}";
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
    }

    async void OnConnect(object? s, EventArgs e)
    {
        _connectBtn.Enabled = false;
        _disconnectBtn.Enabled = false;
        _connSpinner.Spinning = true;

        try
        {
            var auth = await SlackClient.RunOAuthFlowAsync();
            _config.SlackToken          = auth.Token;
            _config.SlackRefreshToken   = auth.RefreshToken ?? "";
            _config.SlackTokenExpiresAt = auth.ExpiresAt;
            _config.SlackTeamName       = auth.TeamName;
            Commit();

            // The new token now carries emoji:read — pull the workspace emoji straight away so
            // autocomplete works without waiting for the next time Settings is reopened.
            _ = _emojiStore.RefreshAsync(auth.Token);

            // Slack hands the code back via the otter:// scheme, which leaves the browser sitting on a
            // blank/"stuck" tab. Pull this window to the front so the user sees the success here, and
            // explain that the leftover tab is harmless. Shown on every connect/reconnect — a reconnect
            // may be months apart, by which point the behaviour is easy to forget.
            Activate();
            BringToFront();
            MessageBox.Show(this,
                "Otter is connected to Slack.\n\n"
                + "The browser tab that opened for sign-in may look blank or stuck — that's normal, "
                + "and you can safely close it.",
                "Connected to Slack", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    async void OnDisconnect(object? s, EventArgs e)
    {
        _disconnectBtn.Enabled = false;

        // Revoke server-side so the credential dies on Slack's side, not just locally. Get a valid token
        // first (refreshing a near-expired one) so we revoke a live grant — that also retires its refresh
        // token. Best-effort: a network failure or already-dead session must not block the local clear.
        try
        {
            var token = await SlackClient.GetValidTokenAsync(_config);
            if (!string.IsNullOrEmpty(token))
                await SlackClient.RevokeTokenAsync(token);
        }
        catch { /* fall through — we clear local state regardless */ }

        _config.SlackToken          = "";
        _config.SlackRefreshToken   = "";
        _config.SlackTokenExpiresAt = null;
        _config.SlackTeamName       = "";
        Commit();
        UpdateConnectionUI();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _feed.CapturesChanged -= OnCapturesChanged;
            _icon?.Dispose();
            _emojiAutocomplete?.Dispose();
            _emojiStore.Dispose();
        }
        base.Dispose(disposing);
    }
}
