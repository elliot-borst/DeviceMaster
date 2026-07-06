using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace DeviceMaster.Ui;

/// <summary>
/// Silent elevation without per-launch UAC. The exe is asInvoker; real launches route
/// through highest-run-level scheduled tasks:
///   "DeviceMaster"          — on demand (Start menu/desktop double-click redirects here)
///   "DeviceMaster Startup"  — at logon, --minimized (replaces the shell:startup shortcut,
///                             which Windows silently ignores for elevated apps)
/// Only the very first elevated run (which registers the tasks) shows a UAC prompt.
/// If the user declines elevation, the app runs with reduced features (no SuperIO/SMBus).
/// </summary>
internal static class ElevationBroker
{
    private const string RunTask = "DeviceMaster";
    private const string StartupTaskName = "DeviceMaster Startup";

    /// <summary>Marker the tasks pass so an unelevated relaunch can't loop forever.</summary>
    public const string ViaTaskArg = "--via-task";

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Called when running unelevated. Returns true when another (elevated) instance has been
    /// started and this process should exit; false to continue unelevated.
    /// </summary>
    public static bool TryRelaunchElevated(string[] args)
    {
        if (args.Contains(ViaTaskArg, StringComparer.OrdinalIgnoreCase))
        {
            return false; // task ran us without elevation (non-admin account) — run degraded
        }

        // plain launches go through the registered task: silent elevation
        if (args.Length == 0 && RunSchtasks($"/Run /TN \"{RunTask}\"") == 0)
        {
            return true;
        }

        // no task yet (first run) or special args — one explicit UAC self-elevation
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
            {
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Join(' ', args.Select(a => $"\"{a}\"")),
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch
        {
            return false; // UAC declined — continue without elevation
        }
    }

    /// <summary>
    /// Registers/refreshes the tasks (elevated only) and retires the legacy startup shortcut.
    /// Start-with-Windows defaults ON and is controlled from the app's header toggle.
    /// </summary>
    public static void EnsureTasks(bool startWithWindows)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !IsElevated)
        {
            return;
        }

        RunSchtasks($"/Create /F /TN \"{RunTask}\" /SC ONDEMAND /RL HIGHEST /TR \"\\\"{exe}\\\" {ViaTaskArg}\"");
        SetStartWithWindows(startWithWindows);

        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DeviceMaster.lnk");
        if (File.Exists(shortcut))
        {
            try
            {
                File.Delete(shortcut); // superseded by the logon task (and ignored by Windows anyway)
            }
            catch
            {
                // harmless leftover
            }
        }
    }

    /// <summary>Creates or removes the logon task; called from the header toggle and at startup.</summary>
    public static void SetStartWithWindows(bool enabled)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !IsElevated)
        {
            return;
        }

        if (enabled)
        {
            RunSchtasks($"/Create /F /TN \"{StartupTaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\" --minimized {ViaTaskArg}\"");
        }
        else if (TaskExists(StartupTaskName))
        {
            RunSchtasks($"/Delete /F /TN \"{StartupTaskName}\"");
        }
    }

    private static bool TaskExists(string name) => RunSchtasks($"/Query /TN \"{name}\"") == 0;

    private static int RunSchtasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return -1;
            }

            return process.WaitForExit(15_000) ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }
}
