using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeviceMaster.AiPause;

/// <summary>A running process: pid plus creation time is the identity (pids get reused).</summary>
public sealed record ProcInfo(int Pid, long CreatedTicks, string Name, string Path)
{
    public (int, long) Key => (Pid, CreatedTicks);
}

/// <summary>
/// Lists running processes with their full image paths. Uses PROCESS_QUERY_LIMITED_INFORMATION,
/// which works for nearly everything (including elevated games) without reading process memory.
/// Paths are cached per (pid, creation time), so a steady-state scan is cheap.
/// </summary>
public sealed class ProcessScanner
{
    private readonly Dictionary<(int, long), string?> _paths = [];

    /// <summary>Processes whose path could not be read on the last scan (protected/system).</summary>
    public int Inaccessible { get; private set; }

    public IReadOnlyList<ProcInfo> Scan()
    {
        var result = new List<ProcInfo>();
        var seen = new HashSet<(int, long)>();
        var inaccessible = 0;
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            throw new InvalidOperationException("process list unavailable");
        }

        foreach (var process in processes)
        {
            using (process)
            {
                var pid = process.Id;
                if (pid is 0 or 4)
                {
                    continue;
                }

                var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                if (handle == IntPtr.Zero)
                {
                    inaccessible++;
                    continue;
                }

                try
                {
                    if (!GetProcessTimes(handle, out var created, out _, out _, out _))
                    {
                        inaccessible++;
                        continue;
                    }

                    var key = (pid, created);
                    seen.Add(key);
                    if (!_paths.TryGetValue(key, out var path))
                    {
                        path = ImagePath(handle);
                        _paths[key] = path;
                    }

                    if (path is null)
                    {
                        inaccessible++;
                        continue;
                    }

                    result.Add(new ProcInfo(pid, created, System.IO.Path.GetFileName(path), path));
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }

        foreach (var stale in _paths.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _paths.Remove(stale);
        }

        Inaccessible = inaccessible;
        return result;
    }

    private static string? ImagePath(IntPtr handle)
    {
        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    /// <summary>
    /// The pid of a borderless window covering its whole monitor in the foreground, or null.
    /// Reads only the foreground window's size and owner. Desktop/shell windows don't count.
    /// </summary>
    public static int? FullscreenForegroundPid()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect))
        {
            return null;
        }

        var className = new StringBuilder(64);
        _ = GetClassName(window, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd")
        {
            return null;
        }

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        var covers = rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top
            && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
        var captioned = (GetWindowLongPtr(window, GwlStyle).ToInt64() & WsCaption) == WsCaption;
        _ = GetWindowThreadProcessId(window, out var pid);
        return covers && !captioned && pid != 0 ? (int)pid : null;
    }

    // ---------- interop ----------

    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
