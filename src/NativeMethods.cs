using System.Runtime.InteropServices;

namespace Otter;

/// <summary>
/// P/Invoke and COM interop helpers: dark title bar/scrollbars, the tray icon handle, and virtual
/// desktop placement. (Teams-call detection lives in <see cref="MicrophoneInUseSignal"/>.)
/// </summary>
static class NativeMethods
{
    // ── Dark title bar ─────────────────────────────────────────────────────────
    // Opts a window's non-client area (title bar, border) into the system dark theme so a
    // WinForms form matches Otter's dark content. Best-effort: silently no-ops on builds that
    // don't support the attribute.

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        int enabled = 1;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE on Win10 20H1+/Win11; 19 on earlier 20xx builds.
        if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
    }

    // ── Dark scrollbars ──────────────────────────────────────────────────────────
    // Opting the app into dark mode (uxtheme ordinal #135) then applying the explorer dark theme
    // to a scrolling control gives it the dark non-client scrollbar instead of the default light
    // one. Best-effort: unsupported on builds older than Win10 1809.

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    static extern int SetPreferredAppMode(int appMode);

    static bool _darkAppModeSet;

    public static void UseDarkScrollBars(IntPtr hWnd)
    {
        try
        {
            if (!_darkAppModeSet)
            {
                SetPreferredAppMode(1); // PreferredAppMode.AllowDark
                _darkAppModeSet = true;
            }
            SetWindowTheme(hWnd, "DarkMode_Explorer", null);
        }
        catch { }
    }

    // ── Tray icon ──────────────────────────────────────────────────────────────────
    // Frees a GDI icon handle created with Bitmap.GetHicon, so swapping the tray icon doesn't
    // leak a handle on every state change.

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ── Virtual desktops ─────────────────────────────────────────────────────────
    // Follows the user across virtual desktops: when the settings window is parked on another
    // desktop, move it onto whichever one the user is currently viewing. A window's desktop is a
    // property of its HWND, so the move is done through the virtual-desktop manager (and, if that
    // can't move our window, the caller recreates the HWND, which re-homes it). Best-effort:
    // silently no-ops where virtual desktops are unavailable (older Windows, some RDP sessions).

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    public static bool IsWindowOnCurrentDesktop(IntPtr hwnd)
    {
        IVirtualDesktopManager? mgr = null;
        try
        {
            mgr = (IVirtualDesktopManager)new VirtualDesktopManagerCoClass();
            if (mgr.IsWindowOnCurrentVirtualDesktop(hwnd, out bool onCurrent) == 0)
                return onCurrent;
        }
        catch { /* virtual desktops unavailable */ }
        finally { if (mgr != null) Marshal.ReleaseComObject(mgr); }
        return true;
    }

    // Tries to move the window onto the user's current virtual desktop. Returns true only if the
    // window is verifiably there afterwards, so the caller knows whether a fallback is needed.
    public static bool TryMoveWindowToCurrentDesktop(IntPtr hwnd)
    {
        IVirtualDesktopManager? mgr = null;
        try
        {
            mgr = (IVirtualDesktopManager)new VirtualDesktopManagerCoClass();

            if (mgr.IsWindowOnCurrentVirtualDesktop(hwnd, out bool onCurrent) == 0 && onCurrent)
                return true;

            // No API gives the current desktop's id directly, so read it off the foreground window —
            // which is, by definition, on the desktop the user is viewing.
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero &&
                mgr.GetWindowDesktopId(fg, out Guid current) == 0 &&
                current != Guid.Empty &&
                mgr.MoveWindowToDesktop(hwnd, ref current) == 0 &&
                // Some Windows builds return S_OK without actually moving — verify it landed.
                mgr.IsWindowOnCurrentVirtualDesktop(hwnd, out bool moved) == 0)
            {
                return moved;
            }
        }
        catch { /* virtual desktops unavailable */ }
        finally { if (mgr != null) Marshal.ReleaseComObject(mgr); }
        return false;
    }

    [ComImport, Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    class VirtualDesktopManagerCoClass { }

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow,
            [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

}
