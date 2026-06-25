namespace Otter;

using Microsoft.Win32;

/// <summary>
/// Registers the <c>otter://</c> URL scheme under the per-user <c>Software\Classes</c> key so Slack's
/// OAuth redirect can hand the authorization code back to a running Otter. Best-effort (a locked-down
/// registry is swallowed) and refreshed on each launch, so the launch command always points at the
/// current executable — mirroring how <see cref="Startup"/> tracks the exe path.
/// </summary>
static class UrlProtocol
{
    public const string Scheme = "otter";

    public static void Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            if (key is null) return;

            // The "URL Protocol" value (even empty) is what marks this key as a launchable scheme.
            key.SetValue("", "URL:Otter Protocol");
            key.SetValue("URL Protocol", "");

            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd?.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch { /* best-effort; registry may be restricted */ }
    }
}
