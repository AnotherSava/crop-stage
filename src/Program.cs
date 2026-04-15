using System.Windows.Forms;

namespace CropStage;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "CropStage_SingleInstance", out var isNew);
        if (!isNew)
            return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        if (System.Windows.Application.Current == null)
        {
            new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        }

        Application.Run(new TrayApplicationContext());
    }
}
