using System.Windows.Forms;

namespace Otter;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ApplicationConfiguration.Initialize();

        // Ensure only one instance runs at a time
        using var mutex = new System.Threading.Mutex(true, "Otter_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("Otter is already running.\nLook for the icon in the system tray.",
                "Otter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var app = new TrayApp();
        app.Start();
        Application.Run();
    }
}
