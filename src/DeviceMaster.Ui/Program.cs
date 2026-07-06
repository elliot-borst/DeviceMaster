using System.Windows;
using DeviceMaster.Control;

namespace DeviceMaster.Ui;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Silent elevation: unelevated launches hand off to the highest-run-level scheduled
        // task (or one UAC self-elevation on first run) BEFORE the single-instance check, so
        // the stub never blocks the real instance. Declined UAC ⇒ run with reduced features.
        if (!ElevationBroker.IsElevated && ElevationBroker.TryRelaunchElevated(args))
        {
            return;
        }

        if (ElevationBroker.IsElevated)
        {
            ElevationBroker.EnsureTasks(ControlSettings.Load().StartWithWindows);
        }

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

        // --minimized (startup task / post-update relaunch) stays in the tray — except on a
        // true first run (no config yet), where an invisible app would look like a failed install.
        var minimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!minimized || !System.IO.File.Exists(ControlSettings.ConfigPath))
        {
            window.Show();
        }

        app.Run();
    }
}
