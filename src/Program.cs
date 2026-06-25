using System.Windows.Forms;
using Velopack;

namespace Otter;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Must run before anything else: when the installer/updater relaunches us with its hook
        // arguments, this handles install/update/uninstall and exits before we touch the UI or the
        // single-instance mutex. In normal launches it's a no-op and returns immediately.
        VelopackApp.Build().Run();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ApplicationConfiguration.Initialize();

        // Windows relaunches us with the Slack OAuth redirect as an argument when the browser follows
        // the otter:// scheme. Pick it out so we can route it to the already-running instance.
        var callbackUrl = Array.Find(args,
            a => a.StartsWith(UrlProtocol.Scheme + "://", StringComparison.OrdinalIgnoreCase));

        // Ensure only one instance runs at a time
        using var mutex = new System.Threading.Mutex(true, "Otter_SingleInstance", out var isNew);
        if (!isNew)
        {
            // A second instance carrying a callback URL just hands it to the primary one and exits
            // quietly; anything else is the user trying to launch a duplicate.
            if (callbackUrl != null)
                IpcServer.TrySend(callbackUrl);
            else
                MessageBox.Show("Otter is already running.\nLook for the icon in the system tray.",
                    "Otter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Primary instance: keep the otter:// registration pointed at this exe, and start listening for
        // callback URLs forwarded by future second instances.
        UrlProtocol.Register();
        IpcServer.Start(SlackClient.DeliverCallbackUrl);

        // Defensive: if we were somehow launched directly with a callback URL, deliver it too.
        if (callbackUrl != null) SlackClient.DeliverCallbackUrl(callbackUrl);

        using var app = new TrayApp();
        app.Start();
        Application.Run();
    }
}
