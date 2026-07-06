using System.Diagnostics;
using System.IO;

namespace DeviceMaster.Ui;

/// <summary>
/// Windows silently refuses to launch administrator apps from the shell:startup folder, so
/// once the app became elevated (v16) the installer's startup shortcut stopped working.
/// This migrates it: when the shortcut exists, register a highest-privilege logon task and
/// remove the shortcut. The installer keeps creating the shortcut on updates when the user
/// has the startup option enabled; migration simply runs again.
/// </summary>
internal static class StartupTask
{
    private const string TaskName = "DeviceMaster";

    public static void EnsureElevatedAutostart()
    {
        try
        {
            var shortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DeviceMaster.lnk");
            if (!File.Exists(shortcut))
            {
                return; // user did not opt into autostart — leave things alone
            }

            var exe = Environment.ProcessPath;
            if (exe is null)
            {
                return;
            }

            var create = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\" --minimized\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            create?.WaitForExit(10_000);

            if (create?.ExitCode == 0)
            {
                File.Delete(shortcut);
            }
        }
        catch
        {
            // autostart is a convenience — never let it break app startup
        }
    }
}
