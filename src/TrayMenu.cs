namespace Otter;

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

/// <summary>
/// Dark theming for the tray's <see cref="ContextMenuStrip"/>. WinForms menus are light by default,
/// which looks out of place against Otter's dark settings window; a custom colour table + renderer
/// brings the menu in line with <see cref="Theme"/>. Also mints the small status-dot images shown in
/// the menu's image margin.
/// </summary>
internal static class TrayMenu
{
    public static void ApplyDark(ContextMenuStrip menu)
    {
        menu.RenderMode = ToolStripRenderMode.Professional;
        menu.Renderer   = new DarkMenuRenderer();
        menu.BackColor  = Theme.NavBg;
        menu.ForeColor  = Theme.Fg;
        menu.Font       = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    }

    /// <summary>A small filled status dot in the given colour, for the menu header's image margin.</summary>
    public static Bitmap DotImage(Color color)
    {
        var bmp = new Bitmap(12, 12, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var fill = new SolidBrush(color);
        g.FillEllipse(fill, 1, 1, 9, 9);
        return bmp;
    }
}

/// <summary>Maps the professional renderer's colours onto Otter's dark palette.</summary>
internal sealed class DarkMenuColorTable : ProfessionalColorTable
{
    public DarkMenuColorTable() => UseSystemColors = false;

    public override Color ToolStripDropDownBackground   => Theme.NavBg;
    public override Color ImageMarginGradientBegin      => Theme.NavBg;
    public override Color ImageMarginGradientMiddle     => Theme.NavBg;
    public override Color ImageMarginGradientEnd        => Theme.NavBg;
    public override Color MenuBorder                    => Theme.Border;
    public override Color MenuItemBorder                => Theme.ButtonHover;
    public override Color MenuItemSelected              => Theme.ButtonHover;
    public override Color MenuItemSelectedGradientBegin => Theme.ButtonHover;
    public override Color MenuItemSelectedGradientEnd   => Theme.ButtonHover;
    public override Color MenuItemPressedGradientBegin  => Theme.Border;
    public override Color MenuItemPressedGradientEnd    => Theme.Border;
    public override Color SeparatorDark                 => Theme.Border;
    public override Color SeparatorLight                => Theme.Border;
    // A subtle dark box behind check marks — a solid accent square reads as a glitch on the dark menu.
    public override Color CheckBackground               => Theme.ButtonBg;
    public override Color CheckSelectedBackground       => Theme.ButtonHover;
    public override Color CheckPressedBackground        => Theme.ButtonHover;
}

/// <summary>Professional renderer wired to <see cref="DarkMenuColorTable"/>, with two tweaks: it
/// skips the hover highlight behind the non-interactive status header (Tag == "header"), and draws
/// submenu arrows in the foreground colour rather than near-black.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColorTable()) => RoundedEdges = false;

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Tag as string == "header") return;
        base.OnRenderMenuItemBackground(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Fg;
        base.OnRenderArrow(e);
    }
}
