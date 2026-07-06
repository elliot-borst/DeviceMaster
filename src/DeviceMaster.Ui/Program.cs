using System.Windows;

namespace DeviceMaster.Ui;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Single instance — two control loops would fight over the same devices.
        // (--multi is a development escape hatch used with DEVICEMASTER_CONFIG.)
        var allowMulti = args.Any(a => a.Equals("--multi", StringComparison.OrdinalIgnoreCase));
        using var instanceLock = new Mutex(initiallyOwned: true, "DeviceMaster-SingleInstance", out var isFirst);
        if (!isFirst && !allowMulti)
        {
            MessageBox.Show("DeviceMaster is already running — look for the fan icon in the system tray.",
                "DeviceMaster", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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
