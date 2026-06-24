namespace Otter;

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

/// <summary>
/// Helpers for the tray's <see cref="ContextMenuStrip"/>. The menu itself uses WinForms' default
/// (system-themed) rendering — good enough for a tray menu and free of the dark-theme glitches the
/// old custom renderer caused. This just mints the small status-dot image shown in the menu's image
/// margin beside the status header.
/// </summary>
internal static class TrayMenu
{
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
