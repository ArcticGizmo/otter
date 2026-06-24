namespace Otter;

class SettingsWindow : Form
{
    public Config Result { get; private set; }

    readonly TextBox _clientIdBox;
    readonly Label   _connectionLabel;
    readonly Button  _connectBtn;
    readonly TextBox _statusTextBox;
    readonly TextBox _emojiBox;

    public SettingsWindow(Config config)
    {
        Result = config.Clone();

        Text            = "Otter Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(430, 278);

        // ── Slack group ───────────────────────────────────────────────────────

        var slackGroup = new GroupBox
        {
            Text     = "Slack Connection",
            Location = new Point(12, 8),
            Size     = new Size(406, 118),
        };

        slackGroup.Controls.Add(Label("Client ID", new Point(10, 24)));
        _clientIdBox = TextBox(new Point(104, 21), 290, Result.SlackClientId);

        _connectionLabel = new Label
        {
            Location  = new Point(10, 54),
            Size      = new Size(386, 20),
            AutoSize  = false,
        };

        _connectBtn = new Button
        {
            Text     = "Connect Slack",
            Location = new Point(10, 80),
            Size     = new Size(120, 28),
        };
        _connectBtn.Click += OnConnect;

        var disconnectBtn = new Button
        {
            Text     = "Disconnect",
            Location = new Point(138, 80),
            Size     = new Size(100, 28),
        };
        disconnectBtn.Click += OnDisconnect;

        slackGroup.Controls.AddRange(new Control[]
        {
            _clientIdBox, _connectionLabel, _connectBtn, disconnectBtn
        });

        // ── Status group ──────────────────────────────────────────────────────

        var statusGroup = new GroupBox
        {
            Text     = "Call Status",
            Location = new Point(12, 134),
            Size     = new Size(406, 88),
        };

        statusGroup.Controls.Add(Label("Status text",  new Point(10, 24)));
        _statusTextBox = TextBox(new Point(104, 21), 290, Result.StatusText);

        statusGroup.Controls.Add(Label("Emoji",        new Point(10, 54)));
        _emojiBox = TextBox(new Point(104, 51), 180, Result.StatusEmoji);

        var emojiHint = new Label
        {
            Text      = "e.g. :headphones:",
            Location  = new Point(292, 54),
            Size      = new Size(106, 20),
            ForeColor = Color.Gray,
            Font      = new Font(Font.FontFamily, 7.5f),
        };

        statusGroup.Controls.AddRange(new Control[]
        {
            _statusTextBox, _emojiBox, emojiHint
        });

        // ── Buttons ───────────────────────────────────────────────────────────

        var saveBtn = new Button
        {
            Text         = "Save",
            DialogResult = DialogResult.OK,
            Location     = new Point(258, 236),
            Size         = new Size(75, 28),
        };
        saveBtn.Click += OnSave;

        var cancelBtn = new Button
        {
            Text         = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location     = new Point(343, 236),
            Size         = new Size(75, 28),
        };

        Controls.AddRange(new Control[] { slackGroup, statusGroup, saveBtn, cancelBtn });
        AcceptButton = saveBtn;
        CancelButton = cancelBtn;

        UpdateConnectionUI();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static Label Label(string text, Point location) => new()
    {
        Text      = text + ":",
        Location  = location,
        Size      = new Size(90, 20),
        TextAlign = ContentAlignment.MiddleRight,
        AutoSize  = false,
    };

    TextBox TextBox(Point location, int width, string value, bool password = false)
    {
        var tb = new TextBox
        {
            Location      = location,
            Size          = new Size(width, 23),
            Text          = value,
            PasswordChar  = password ? '●' : '\0',
        };
        // Add to parent via caller
        return tb;
    }

    void UpdateConnectionUI()
    {
        if (!string.IsNullOrEmpty(Result.SlackToken))
        {
            _connectionLabel.Text      = $"✓  Connected to {Result.SlackTeamName}";
            _connectionLabel.ForeColor = Color.DarkGreen;
            _connectBtn.Text           = "Reconnect";
        }
        else
        {
            _connectionLabel.Text      = "Not connected";
            _connectionLabel.ForeColor = Color.Gray;
            _connectBtn.Text           = "Connect Slack";
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
        _connectBtn.Text    = "Waiting for browser…";

        try
        {
            var (token, teamName) = await SlackClient.RunOAuthFlowAsync(clientId);
            Result.SlackToken    = token;
            Result.SlackTeamName = teamName;
            Result.SlackClientId = clientId;
            UpdateConnectionUI();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not connect to Slack:\n{ex.Message}",
                "Otter", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _connectBtn.Enabled = true;
            UpdateConnectionUI();
        }
    }

    void OnDisconnect(object? s, EventArgs e)
    {
        Result.SlackToken    = "";
        Result.SlackTeamName = "";
        UpdateConnectionUI();
    }

    void OnSave(object? s, EventArgs e)
    {
        Result.StatusText  = _statusTextBox.Text.Trim();
        Result.StatusEmoji = _emojiBox.Text.Trim();
        Result.SlackClientId = _clientIdBox.Text.Trim();
    }
}
