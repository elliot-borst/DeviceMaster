using System.Windows;

namespace DeviceMaster.Ui;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Closing the window hides to the system tray, so app lifetime is explicit
        // (tray menu Exit or the updater hand-off end it).
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        app.MainWindow = window;

        // The startup shortcut launches with --minimized: tray-only until opened.
        if (!args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            window.Show();
        }

        app.Run();
    }
}
