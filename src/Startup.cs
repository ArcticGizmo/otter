namespace Otter;

using Microsoft.Win32;

/// <summary>
/// Run-at-login via the per-user <c>Run</c> registry key. Best-effort: a locked-down registry just
/// reads back as "not enabled" rather than throwing. The value points at the current executable, so
/// moving Otter.exe means toggling this off and on again to refresh the path.
/// </summary>
static class Startup
{
    const string RunKey    = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Otter";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && v.Length > 0;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path))
                    key.SetValue(ValueName, $"\"{path}\"");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* best-effort; registry may be restricted */ }
    }
}
