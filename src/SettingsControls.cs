namespace Otter;

using System.Drawing.Drawing2D;
using System.Diagnostics;

/// <summary>A Material-style on/off switch: a rounded pill track with a sliding knob. Painted to
/// match <see cref="Theme"/> so it reads as part of the app rather than a stock checkbox.</summary>
internal sealed class ToggleSwitch : Control
{
    private bool _on;

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        Size           = new Size(46, 26);
        Cursor         = Cursors.Hand;
        DoubleBuffered = true;
        TabStop        = false;
        BackColor      = Theme.FormBg;
    }

    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Set the state without raising <see cref="CheckedChanged"/> (used when syncing to
    /// external state, so we don't re-trigger the change handler).</summary>
    public void SetCheckedSilently(bool value)
    {
        if (_on == value) return;
        _on = value;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left)
            Checked = !Checked;
        base.OnMouseClick(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(1, 1, Width - 2, Height - 2);
        Color track = _on ? Theme.Green : Color.FromArgb(70, 70, 88);
        if (!Enabled) track = Theme.Blend(track, BackColor, 0.5f);

        using (var path = Pill(rect))
        using (var brush = new SolidBrush(track))
            g.FillPath(brush, path);

        int knobD = rect.Height - 6;
        int knobX = _on ? rect.Right - knobD - 3 : rect.Left + 3;
        Color knob = Color.FromArgb(235, 235, 245);
        if (!Enabled) knob = Theme.Blend(knob, BackColor, 0.4f);
        using var kb = new SolidBrush(knob);
        g.FillEllipse(kb, knobX, rect.Top + 3, knobD, knobD);
    }

    private static GraphicsPath Pill(Rectangle r)
    {
        int d = r.Height;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }
}

/// <summary>A small indeterminate spinner: a rotating accent arc on a faint track. Only animates
/// (and only consumes a timer) while <see cref="Spinning"/> is true and the control is visible —
/// used beside async actions such as the Slack OAuth connect flow.</summary>
internal sealed class Spinner : Control
{
    private readonly System.Windows.Forms.Timer _timer;
    private int _angle;
    private bool _spinning;

    public Spinner()
    {
        Size           = new Size(18, 18);
        DoubleBuffered = true;
        TabStop        = false;
        BackColor      = Theme.FormBg;
        Visible        = false;
        _timer = new System.Windows.Forms.Timer { Interval = 60 };
        _timer.Tick += (_, _) => { _angle = (_angle + 30) % 360; Invalidate(); };
    }

    /// <summary>Start/stop the animation. Also toggles visibility so the spinner only shows while busy.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Spinning
    {
        get => _spinning;
        set
        {
            if (_spinning == value) return;
            _spinning = value;
            Visible   = value;
            if (value) _timer.Start(); else _timer.Stop();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_spinning) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float pad = 2.5f;
        var rect = new RectangleF(pad, pad, Width - pad * 2, Height - pad * 2);
        float thickness = Math.Max(2f, Width / 9f);

        using var track = new Pen(Theme.Border, thickness);
        g.DrawArc(track, rect, 0, 360);

        using var arc = new Pen(Theme.Accent, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arc, rect, _angle, 100);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Tracks the full-width and wrapping controls on a resizable page and reflows them to the current
/// available width. A settings page registers each fluid control once, then calls <see cref="Apply"/>
/// whenever its content area resizes — this is what lets the dark settings window stay readable when
/// dragged wider/narrower and across DPI scales, without hard-coded widths.
/// </summary>
internal sealed class FluidLayout
{
    // Controls whose Width should fill the available area, minus a fixed inset.
    private readonly List<(Control c, int inset)> _width = new();
    // Same, but the inset is computed live (for controls sharing a row with DPI-scaled siblings).
    private readonly List<(Control c, Func<int> inset)> _widthDynamic = new();
    // AutoSize labels whose MaximumSize width is updated so they re-wrap.
    private readonly List<Label> _wrap = new();

    private readonly Func<int> _availableWidth;

    public FluidLayout(Func<int> availableWidth) => _availableWidth = availableWidth;

    public T AddWidth<T>(T c, int inset = 0) where T : Control { _width.Add((c, inset)); return c; }
    public T AddWidthDynamic<T>(T c, Func<int> inset) where T : Control { _widthDynamic.Add((c, inset)); return c; }
    public Label AddWrap(Label l) { _wrap.Add(l); return l; }

    public void Apply()
    {
        int w = Math.Max(200, _availableWidth());
        foreach (var (c, inset) in _width)        c.Width = Math.Max(40, w - inset);
        foreach (var (c, inset) in _widthDynamic) c.Width = Math.Max(40, w - inset());
        foreach (var l in _wrap)                  l.MaximumSize = new Size(w, 0);
    }
}

/// <summary>
/// Factory for the dark-themed building blocks of Otter's settings surface — headings, body copy,
/// toggles, buttons, text boxes, separators, and links. Methods that need to reflow on resize take a
/// <see cref="FluidLayout"/> and register themselves; the rest are plain static factories. Keeping
/// these in one place is what makes every page look consistent for almost no per-page code.
/// </summary>
internal static class Ui
{
    private const string FontName = "Segoe UI";

    // ── Headings & text ──────────────────────────────────────────────────────────
    public static Label SectionTitle(string text) => new()
    {
        Text      = text,
        AutoSize  = true,
        ForeColor = Theme.Title,
        Font      = new Font(FontName, 11f, FontStyle.Bold, GraphicsUnit.Point),
        Margin    = new Padding(0, 4, 0, 8),
    };

    public static Label FieldCaption(string text) => new()
    {
        Text      = text,
        AutoSize  = true,
        ForeColor = Theme.Muted,
        Margin    = new Padding(0, 2, 0, 2),
    };

    public static Label SubHeading(string text) => new()
    {
        Text      = text,
        AutoSize  = true,
        ForeColor = Theme.Fg,
        Font      = new Font(FontName, 9.5f, FontStyle.Bold, GraphicsUnit.Point),
        Margin    = new Padding(0, 6, 0, 4),
    };

    // A wrapping body paragraph; registered so its wrap width tracks the content area.
    public static Label BodyText(FluidLayout fluid, string text)
    {
        var l = new Label
        {
            Text        = text,
            AutoSize    = true,
            MaximumSize = new Size(480, 0),  // updated by FluidLayout.Apply
            ForeColor   = Theme.Muted,
            Margin      = new Padding(0, 0, 0, 6),
        };
        return fluid.AddWrap(l);
    }

    // A bullet-prefixed body paragraph (feature lists, changelog items).
    public static Label BulletText(FluidLayout fluid, string text)
    {
        var l = new Label
        {
            Text        = "•  " + text,
            AutoSize    = true,
            MaximumSize = new Size(480, 0),
            ForeColor   = Theme.Muted,
            Margin      = new Padding(0, 0, 0, 4),
        };
        return fluid.AddWrap(l);
    }

    // An indented italic label for blockquote / editorial asides.
    public static Label BlockQuote(FluidLayout fluid, string text)
    {
        var l = new Label
        {
            Text        = text,
            AutoSize    = true,
            MaximumSize = new Size(480, 0),
            ForeColor   = Theme.Muted,
            Font        = new Font(FontName, 9f, FontStyle.Italic, GraphicsUnit.Point),
            Margin      = new Padding(12, 0, 0, 6),
        };
        return fluid.AddWrap(l);
    }

    // A monospace, boxed block for copy-pasteable commands.
    public static Label CodeBlock(FluidLayout fluid, string text)
    {
        var l = new Label
        {
            Text        = text,
            AutoSize    = true,
            MaximumSize = new Size(480, 0),
            Font        = new Font("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor   = Theme.Fg,
            BackColor   = Theme.CodeBg,
            Padding     = new Padding(10, 8, 10, 8),
            Margin      = new Padding(0, 0, 0, 8),
        };
        return fluid.AddWrap(l);
    }

    // ── Inputs ───────────────────────────────────────────────────────────────────
    public static ToggleSwitch MakeToggle() => new() { Margin = new Padding(0) };

    // A dark single-line text box matching the rest of the surface. The caller registers it with a
    // FluidLayout if it should fill the row.
    public static TextBox MakeTextBox(string value = "", bool password = false) => new()
    {
        Text         = value,
        Width        = 480,
        BackColor    = Theme.ButtonBg,
        ForeColor    = Theme.Fg,
        BorderStyle  = BorderStyle.FixedSingle,
        PasswordChar = password ? '●' : '\0',
        Font         = new Font(FontName, 9.5f, FontStyle.Regular, GraphicsUnit.Point),
        Margin       = new Padding(0, 0, 0, 8),
    };

    public static Button MakeButton(string text)
    {
        var b = new Button
        {
            Text      = text,
            AutoSize  = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Fg,
            BackColor = Theme.ButtonBg,
            Padding   = new Padding(8, 4, 8, 4),
            Margin    = new Padding(0, 0, 8, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor        = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.ButtonHover;
        b.FlatAppearance.MouseDownBackColor = Theme.Border;
        return b;
    }

    // ── Rows & layout ──────────────────────────────────────────────────────────────
    public static FlowLayoutPanel ButtonRow() => new()
    {
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents  = false,
        AutoSize      = true,
        AutoSizeMode  = AutoSizeMode.GrowAndShrink,
        Margin        = new Padding(0, 0, 0, 4),
    };

    // A section header with a right-justified toggle on the same row, re-positioned on resize.
    public static Panel TitleRow(FluidLayout fluid, string title, ToggleSwitch toggle)
    {
        var row = new Panel { Height = 30, Margin = new Padding(0, 4, 0, 8) };
        var label = new Label
        {
            Text      = title,
            AutoSize  = true,
            ForeColor = Theme.Title,
            Font      = new Font(FontName, 11f, FontStyle.Bold, GraphicsUnit.Point),
            Location  = new Point(0, 2),
        };
        row.Controls.Add(label);
        row.Controls.Add(toggle);

        void Position() => toggle.Location = new Point(row.Width - toggle.Width, (row.Height - toggle.Height) / 2);
        row.Resize += (_, _) => Position();
        fluid.AddWidth(row);
        Position();
        return row;
    }

    public static Panel Separator(FluidLayout fluid)
    {
        var p = new Panel
        {
            Height    = 1,
            Width     = 480,
            BackColor = Theme.Border,
            Margin    = new Padding(0, 12, 0, 12),
        };
        return fluid.AddWidth(p);
    }

    public static LinkLabel LinkRow(string text, string url)
    {
        var link = new LinkLabel
        {
            Text             = text,
            AutoSize         = true,
            LinkColor        = Theme.Accent,
            ActiveLinkColor  = Theme.AccentHover,
            VisitedLinkColor = Theme.Accent,
            LinkBehavior     = LinkBehavior.HoverUnderline,
            BackColor        = Theme.FormBg,
            Margin           = new Padding(0, 0, 0, 4),
        };
        link.LinkClicked += (_, _) => OpenUrl(url);
        return link;
    }

    // ── Shared helpers ───────────────────────────────────────────────────────────
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    public static string? LoadEmbeddedText(string resourceName)
    {
        try
        {
            using var stream = typeof(Ui).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    public static Bitmap? LoadEmbeddedBitmap(string resourceName)
    {
        try
        {
            using var stream = typeof(Ui).Assembly.GetManifestResourceStream(resourceName);
            return stream != null ? new Bitmap(stream) : null;
        }
        catch { return null; }
    }
}
