using System.Windows.Forms;

namespace Sleams;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ApplicationConfiguration.Initialize();

        // Ensure only one instance runs at a time
        using var mutex = new System.Threading.Mutex(true, "Sleams_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("Sleams is already running.\nLook for the icon in the system tray.",
                "Sleams", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var app = new TrayApp();
        app.Start();
        Application.Run();
    }
}
