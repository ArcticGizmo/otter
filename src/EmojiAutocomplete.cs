namespace Otter;

/// <summary>
/// Slack-style emoji autocomplete bolted onto a plain <see cref="TextBox"/>. As the user types an
/// emoji token — a <c>:</c> followed by name characters — a dark list of matching workspace emoji
/// (with thumbnails) appears beneath the box; accepting one rewrites the token to <c>:name:</c>. The
/// box stays a normal free-text field, so standard emoji (which Slack doesn't enumerate) can still be
/// typed by hand.
///
/// The list is a <see cref="EmojiPopupList"/> added as a child of the owning <see cref="Form"/> and
/// floated over the page. Crucially it is a non-selectable control, so neither showing it nor clicking
/// an item ever moves keyboard focus off the text box — the user keeps typing the whole time, and
/// selection is driven from the box's own KeyDown. (Earlier attempts used a separate popup window,
/// which always lost the fight with the OS over activation and stole focus.)
/// </summary>
sealed class EmojiAutocomplete : IDisposable
{
    const int MaxItems   = 8;
    const int ItemHeight = 26;
    const int ThumbSize  = 18;
    const int PopupWidth = 260;

    readonly TextBox    _box;
    readonly EmojiStore _store;
    readonly EmojiPopupList _list;

    Form? _ownerForm;
    bool  _attached;
    bool  _open;
    int   _tokenStart = -1; // index of the ':' that opened the current token
    bool  _suppress;        // guards the programmatic Text edit in Accept() from re-triggering us

    public EmojiAutocomplete(TextBox box, EmojiStore store)
    {
        _box   = box;
        _store = store;

        _list = new EmojiPopupList(ItemHeight, ThumbSize)
        {
            Width         = PopupWidth,
            Visible       = false,
            ImageProvider = _store.GetImageCached,
        };
        _list.ItemChosen += AcceptName;

        _box.KeyDown     += OnBoxKeyDown;
        _box.TextChanged += (_, _) => UpdateFromCaret();
        _box.LostFocus   += (_, _) => Close(); // focus can't enter the list, so any loss = dismiss

        // Tab is normally swallowed for focus traversal before it reaches KeyDown. While the popup is
        // open, claim it as an input key so Tab accepts the highlighted emoji like Enter does.
        _box.PreviewKeyDown += (_, e) =>
        {
            if (_open && e.KeyCode == Keys.Tab) e.IsInputKey = true;
        };

        // A thumbnail arriving late, or the catalogue refreshing, should repaint the open list.
        _store.Updated += OnStoreUpdated;
    }

    // ── Token detection ─────────────────────────────────────────────────────────────
    void UpdateFromCaret()
    {
        if (_suppress) return;

        var text  = _box.Text;
        int caret = _box.SelectionStart;

        // Walk left from the caret to the ':' that opens this token, bailing at any character that
        // can't appear in an emoji name (which includes whitespace).
        int start = -1;
        for (int i = caret - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == ':') { start = i; break; }
            if (!IsNameChar(c)) break;
        }
        if (start < 0) { Close(); return; }

        _tokenStart = start;
        var query   = text.Substring(start + 1, caret - start - 1);

        var matches = _store.Search(query, MaxItems);
        if (matches.Count == 0) { Close(); return; }

        _list.SetItems(matches);
        ShowPopup();
    }

    static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '+';

    // ── Show / hide ─────────────────────────────────────────────────────────────────
    void ShowPopup()
    {
        if (!EnsureAttached()) return;

        // Position just below the box, in the owner form's client coordinates.
        var screen   = _box.PointToScreen(new Point(0, _box.Height));
        var clientPt = _ownerForm!.PointToClient(screen);
        _list.Bounds = new Rectangle(clientPt, new Size(PopupWidth, _list.PreferredHeight(MaxItems)));

        _list.Visible = true;
        _list.BringToFront();
        _open = true;
    }

    void Close()
    {
        if (!_open) return;
        _open = false;
        _list.Visible = false;
    }

    // Adds the list to the owner form the first time we have one, and wires owner events that should
    // dismiss a floating list (the window moving, or losing activation).
    bool EnsureAttached()
    {
        if (_attached) return true;
        _ownerForm = _box.FindForm();
        if (_ownerForm is null) return false;
        _ownerForm.Controls.Add(_list);
        _ownerForm.Move       += (_, _) => Close();
        _ownerForm.Deactivate += (_, _) => Close();
        _attached = true;
        return true;
    }

    // ── Acceptance ─────────────────────────────────────────────────────────────────
    void Accept()
    {
        if (_list.Selected is string name) AcceptName(name);
        else Close();
    }

    void AcceptName(string name)
    {
        var text  = _box.Text;
        int caret = _box.SelectionStart;
        if (_tokenStart < 0 || _tokenStart > text.Length) { Close(); return; }

        var before = text[.._tokenStart];
        var after  = caret <= text.Length ? text[caret..] : "";
        var insert = $":{name}:";

        _suppress = true;
        _box.Text           = before + insert + after;
        _box.SelectionStart = (before + insert).Length;
        _suppress = false;

        Close();
        _box.Focus();
    }

    // ── Input handlers ─────────────────────────────────────────────────────────────
    void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_open) return;
        switch (e.KeyCode)
        {
            case Keys.Down:   _list.MoveSelection(+1); break;
            case Keys.Up:     _list.MoveSelection(-1); break;
            case Keys.Enter:
            case Keys.Tab:    Accept();       break;
            case Keys.Escape: Close();        break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    void OnStoreUpdated()
    {
        if (_open) _list.Invalidate();
    }

    public void Dispose()
    {
        _store.Updated -= OnStoreUpdated;
        _ownerForm?.Controls.Remove(_list);
        _list.Dispose();
    }
}

/// <summary>
/// A dark, owner-drawn list of emoji names with thumbnails, used as the autocomplete drop-down. It is
/// deliberately <em>non-selectable</em> (<see cref="ControlStyles.Selectable"/> off): it lives inside
/// the settings form and floats over the page, and because it never accepts focus, neither showing it
/// nor clicking a row pulls keyboard focus away from the emoji text box. Mouse hover highlights a row;
/// a click raises <see cref="ItemChosen"/>.
/// </summary>
sealed class EmojiPopupList : Control
{
    readonly List<string> _items = new();
    int _selected = -1;
    readonly int _itemHeight;
    readonly int _thumb;

    /// <summary>Supplies the thumbnail for a name (may return null while it loads).</summary>
    public Func<string, Image?>? ImageProvider;

    /// <summary>Raised when a row is clicked.</summary>
    public event Action<string>? ItemChosen;

    public EmojiPopupList(int itemHeight, int thumb)
    {
        _itemHeight = itemHeight;
        _thumb      = thumb;
        SetStyle(ControlStyles.Selectable, false); // never take focus
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop   = false;
        BackColor = Theme.CodeBg;
        Font      = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    }

    public int Count => _items.Count;

    public string? Selected =>
        _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    public void SetItems(IReadOnlyList<string> items)
    {
        _items.Clear();
        _items.AddRange(items);
        _selected = _items.Count > 0 ? 0 : -1;
        Invalidate();
    }

    public int PreferredHeight(int maxItems) => _itemHeight * Math.Min(_items.Count, maxItems);

    public void MoveSelection(int delta)
    {
        if (_items.Count == 0) return;
        _selected = ((_selected + delta) % _items.Count + _items.Count) % _items.Count; // wrap
        Invalidate();
    }

    int IndexAt(int y)
    {
        if (y < 0) return -1;
        int i = y / _itemHeight;
        return i >= 0 && i < _items.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexAt(e.Y);
        if (i >= 0 && i != _selected) { _selected = i; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int i = IndexAt(e.Y);
        if (i >= 0) { _selected = i; ItemChosen?.Invoke(_items[i]); }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Theme.CodeBg))
            g.FillRectangle(bg, ClientRectangle);

        const int pad = 6;
        for (int i = 0; i < _items.Count; i++)
        {
            var rect = new Rectangle(0, i * _itemHeight, Width, _itemHeight);
            if (!e.ClipRectangle.IntersectsWith(rect)) continue;

            if (i == _selected)
                using (var sel = new SolidBrush(Theme.ButtonHover))
                    g.FillRectangle(sel, rect);

            int imgY = rect.Y + (rect.Height - _thumb) / 2;
            var img  = ImageProvider?.Invoke(_items[i]);
            if (img is not null)
                g.DrawImage(img, pad, imgY, _thumb, _thumb);

            var textRect = new Rectangle(pad + _thumb + pad, rect.Y,
                Width - (pad + _thumb + pad), rect.Height);
            TextRenderer.DrawText(g, $":{_items[i]}:", Font, textRect, Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        using var border = new Pen(Theme.Border);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}
