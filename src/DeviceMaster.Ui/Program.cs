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
        // (--no-elevate is a development escape hatch: run degraded, no task, no UAC.)
        var noElevate = args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));
        if (!noElevate && !ElevationBroker.IsElevated && ElevationBroker.TryRelaunchElevated(args))
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
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "DeviceMaster-ShowWindow");
        if (!isFirst && !allowMulti)
        {
            showSignal.Set(); // bring the running instance's window up instead of nagging
            return;
        }

        // Closing the window hides to the system tray, so app lifetime is explicit
        // (tray menu Exit or the updater hand-off end it).
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        app.MainWindow = window;

        // a second launch signals this event instead of starting — show the existing window
        var showListener = new Thread(() =>
        {
            while (showSignal.WaitOne())
            {
                window.Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
            }
        })
        { IsBackground = true, Name = "DeviceMaster show-signal" };
        showListener.Start();

        // --minimized (startup task / post-update relaunch) stays in the tray — unless the
        // user turned Start Hidden off, or on a true first run (no config yet), where an
        // invisible app would look like a failed install.
        var minimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!minimized || !ControlSettings.Load().StartHidden || !System.IO.File.Exists(ControlSettings.ConfigPath))
        {
            window.Show();
        }

        app.Run();
    }
}
