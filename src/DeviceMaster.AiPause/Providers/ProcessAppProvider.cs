using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeviceMaster.AiPause.Providers;

/// <summary>One stopped instance. The command line is DPAPI-encrypted: it can carry API keys.</summary>
public sealed record StoppedProcess(string Path, string? ProtectedCommandLine, string? WorkingDirectory, bool Relaunch);

public sealed record ProcessAppsRecord(List<StoppedProcess> Stopped);

/// <summary>
/// Other local AI servers (llama-server.exe, koboldcpp.exe, …) picked by exe path. Pause: each
/// running copy of exactly that file is asked to close, then ended after 3 s (console servers
/// have no window, so they're ended at once). Restore: started again with the same command line,
/// minimized. A process whose command line can't be read is left running rather than stopped
/// with no way back. Supervisors that auto-restart a server will fight this.
/// </summary>
public sealed class ProcessAppProvider(AiPauseSettings settings) : IAiProvider
{
    public string Id => "apps";

    public string DisplayName => "Other AI apps";

    public bool IsPresent => settings.ProcessApps.Count > 0;

    public string Describe()
    {
        var running = settings.ProcessApps.Count(app => FindRunning(app.Path).Count > 0);
        return running == 0 ? $"{settings.ProcessApps.Count} app(s), none running" : $"{running} of {settings.ProcessApps.Count} running";
    }

    public Task<CaptureResult?> CaptureAsync(CancellationToken token)
    {
        var stopped = new List<StoppedProcess>();
        var skipped = new List<string>();
        foreach (var app in settings.ProcessApps)
        {
            foreach (var pid in FindRunning(app.Path))
            {
                var (commandLine, workingDir) = ReadStartInfo(pid);
                if (commandLine is null && app.Relaunch)
                {
                    skipped.Add(Path.GetFileName(app.Path));
                    continue; // can't bring it back — leave it running
                }

                stopped.Add(new StoppedProcess(app.Path, commandLine is null ? null : Protect(commandLine), workingDir, app.Relaunch));
            }
        }

        if (stopped.Count == 0)
        {
            return Task.FromResult<CaptureResult?>(null);
        }

        var summary = $"{stopped.Count} process(es): {string.Join(", ", stopped.Select(s => Path.GetFileName(s.Path)).Distinct())}";
        if (skipped.Count > 0)
        {
            summary += $" · left running (start command unreadable): {string.Join(", ", skipped)}";
        }

        return Task.FromResult<CaptureResult?>(new CaptureResult(Json.ToElement(new ProcessAppsRecord(stopped)), summary, null));
    }

    public async Task UnloadAsync(JsonElement record, CancellationToken token)
    {
        foreach (var path in Json.From<ProcessAppsRecord>(record).Stopped.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var pid in FindRunning(path))
            {
                await StopAsync(pid, token);
            }

            if (FindRunning(path).Count > 0)
            {
                throw new InvalidOperationException($"{Path.GetFileName(path)} is still running");
            }
        }
    }

    public Task RestoreAsync(JsonElement record, CancellationToken token)
    {
        var failures = new List<string>();
        var started = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stopped in Json.From<ProcessAppsRecord>(record).Stopped.Where(s => s.Relaunch && s.ProtectedCommandLine is not null))
        {
            // idempotent: a retry (or an unload that never happened) must not start a second copy
            if (!started.Contains(stopped.Path) && FindRunning(stopped.Path).Count > 0)
            {
                continue;
            }

            started.Add(stopped.Path);
            try
            {
                var arguments = ArgumentsOf(Unprotect(stopped.ProtectedCommandLine!));
                Process.Start(new ProcessStartInfo(stopped.Path, arguments)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Minimized,
                    WorkingDirectory = stopped.WorkingDirectory is { Length: > 0 } dir && Directory.Exists(dir)
                        ? dir
                        : Path.GetDirectoryName(stopped.Path) ?? "",
                })?.Dispose();
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(stopped.Path)}: {ex.Message}");
            }
        }

        return failures.Count == 0
            ? Task.CompletedTask
            : throw new InvalidOperationException(string.Join("; ", failures));
    }

    // ---------- helpers ----------

    private static List<int> FindRunning(string exePath)
    {
        var pids = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } file && PathUtil.Canonical(file) == PathUtil.Canonical(exePath))
                    {
                        pids.Add(process.Id);
                    }
                }
                catch
                {
                    // inaccessible — not ours to stop
                }
            }
        }

        return pids;
    }

    private static async Task StopAsync(int pid, CancellationToken token)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(token);
                grace.CancelAfter(3000);
                try
                {
                    await process.WaitForExitAsync(grace.Token);
                    return;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // didn't close in time — end it below
                }
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(token);
        }
        catch (ArgumentException)
        {
            // already exited
        }
    }

    /// <summary>The command line and working directory the process was started with (WMI).</summary>
    private static (string? CommandLine, string? WorkingDirectory) ReadStartInfo(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine, ExecutablePath FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var row in searcher.Get())
            {
                using (row)
                {
                    var commandLine = row["CommandLine"] as string;
                    var exe = row["ExecutablePath"] as string;
                    return (commandLine, exe is null ? null : Path.GetDirectoryName(exe));
                }
            }
        }
        catch
        {
            // access denied (elevated target) or WMI unavailable
        }

        return (null, null);
    }

    /// <summary>The command line minus argv[0] (quoted or bare), as one argument string.</summary>
    public static string ArgumentsOf(string commandLine)
    {
        var text = commandLine.TrimStart();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close < 0 ? "" : text[(close + 1)..].TrimStart();
        }

        var space = text.IndexOf(' ');
        return space < 0 ? "" : text[(space + 1)..].TrimStart();
    }

    private static string Protect(string text) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string protectedText) => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser));
}
