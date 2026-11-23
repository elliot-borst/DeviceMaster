using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;

namespace DeviceMaster.Sensors;

/// <summary>
/// Game FPS via Intel PresentMon (the same tool StarMaster uses, so the numbers match). PresentMon
/// captures the full Windows present pipeline over ETW — including Vulkan and DX12, which a
/// DXGI-only capture misses entirely — so it reads correctly for any renderer (Star Citizen etc.).
///
/// PresentMon is a GUI-less console tool that only writes stdout when it has a console, so it is
/// launched through <c>cmd /c "…" &gt; file</c> (cmd gives it a console) and we tail that CSV,
/// reading the <c>msBetweenPresents</c> column and reporting the framerate of the foreground
/// process as a rolling average over the last ~60 frames (matching StarMaster). DeviceMaster runs
/// elevated, so PresentMon captures with no extra elevation. Every failure degrades to "no FPS".
/// </summary>
public sealed class PresentMonFpsReader : IDisposable
{
    private const string PresentMonUrl = "https://github.com/GameTechDev/PresentMon/releases/download/v2.4.1/PresentMon-2.4.1-x64.exe";
    private const string MsColumn = "msBetweenPresents";
    private const string PidColumn = "ProcessID";
    private const string AppColumn = "Application";
    private const string ModeColumn = "PresentMode";
    private const int FrameWindow = 60;   // rolling average length, like StarMaster
    private const long FreshMs = 2000;    // a frame older than this no longer counts toward "presenting now"

    // Processes that always "present" (the desktop compositor, the shell, ourselves) but are never
    // the game whose FPS we want. dwm.exe in particular presents at the refresh rate right alongside
    // a composed game, so it must never win the "what is presenting" pick.
    // Compositor + shell: excluded from the primary "busiest app" pick so a real app (game/video/
    // any self-rendering window) wins over the desktop. dwm.exe is still eligible as the LAST resort
    // (its rate = the monitor refresh), so the readout never blanks on an idle desktop.
    private static readonly HashSet<string> NonGame = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm.exe", "explorer.exe", "DeviceMaster.exe", "ApplicationFrameHost.exe", "SearchHost.exe",
        "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "TextInputHost.exe", "SystemSettings.exe",
    };

    private const string Self = "DeviceMaster.exe"; // never report our own present rate

    private readonly Action<string>? _log;
    private readonly string _sessionName;
    private readonly object _gate = new();
    private readonly Dictionary<int, Queue<(long Tick, double Ms)>> _byPid = new();
    private readonly Dictionary<int, string> _names = new(); // pid → Application, to skip non-game presenters
    private int _fpsPid;                                     // last selected presenter (recomputed each read)
    private readonly HashSet<int> _games = new();            // presenters that have looked like a game (#112)

    private Thread? _reader;
    private Process? _proc;
    private string? _csvPath;
    private volatile bool _running;
    private bool _loggedFrames;

    private PresentMonFpsReader(Action<string>? log, string sessionName)
    {
        _log = log;
        _sessionName = sessionName;
    }

    /// <summary>Starts PresentMon, or returns null if it isn't available and can't be fetched.</summary>
    public static PresentMonFpsReader? StartOrNull(Action<string>? log = null, string sessionName = "DeviceMasterFPS")
    {
        var reader = new PresentMonFpsReader(log, sessionName);
        return reader.Start() ? reader : null;
    }

    private bool Start()
    {
        try
        {
            var exe = EnsurePresentMon();
            if (exe is null)
            {
                return false;
            }

            CleanOldCsvs();
            _csvPath = Path.Combine(Path.GetTempPath(), $"DeviceMaster-fps-{Guid.NewGuid().ToString("N")[..8]}.csv");

            // --stop_existing_session (scoped to OUR session name) clears an orphan from a prior
            // crash without touching StarMaster's PresentMon. No --process_name: capture every
            // process and pick the foreground one in CurrentFps.
            var args = $"--stop_existing_session --session_name {_sessionName} --no_console_stats --v1_metrics --output_stdout";
            // UseShellExecute=false keeps a real parent/child handle so Kill(entireProcessTree)
            // reliably takes PresentMon down with cmd; CreateNoWindow=true still gives cmd a
            // (windowless) console, which PresentMon needs to emit --output_stdout.
            var psi = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c \"\"{exe}\" {args} > \"{_csvPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _proc = Process.Start(psi);

            _running = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "DeviceMaster PresentMon FPS" };
            _reader.Start();
            _log?.Invoke($"FPS monitor: PresentMon started ({Path.GetFileName(exe)})");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FPS monitor (PresentMon) failed to start: {ex.Message}");
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// FPS to display: the rolling ~2 s average of whatever is RENDERING MOST right now, or null only if
    /// nothing is presenting at all. Window focus is ignored on purpose — the reading stays on your game/
    /// video no matter which window you click into, and never blanks on an alt-tab.
    /// </summary>
    public int? CurrentFps()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            _fpsPid = SelectFpsPid(_byPid, _names, now);
            return _fpsPid != 0 ? RollingFps(_byPid[_fpsPid], now) : null;
        }
    }

    /// <summary>
    /// FPS of a real app only (a game, a video, any self-rendering window), or null when only the
    /// desktop compositor/shell is presenting. For the 8.8" V1.x gaming dashboard (#103), where an idle
    /// desktop's compositor rate (e.g. 22) read like a struggling game; <see cref="CurrentFps"/> keeps
    /// its never-blank fallback for the serial dashboard.
    /// </summary>
    public int? CurrentAppFps()
    {
        var now = Environment.TickCount64;
        var foreground = ForegroundFullscreenPid();
        lock (_gate)
        {
            if (foreground != 0 && _byPid.ContainsKey(foreground))
            {
                _games.Add(foreground); // stays a game after an alt-tab, until it stops presenting
            }

            var pid = SelectScreenPid(_byPid, _names, now, _games);
            CurrentAppSource = pid != 0 ? SourceName(_names.GetValueOrDefault(pid)) : null;
            return pid != 0 ? RollingFps(_byPid[pid], now) : null;
        }
    }

    /// <summary>
    /// The busiest real app presenting (0 = none; the compositor/shell never count). Pure. With
    /// <paramref name="games"/>, only those presenters count (#112): an ordinary window that redraws
    /// now and then (a chat app, a browser) is not a game and must not read as "9 FPS".
    /// </summary>
    public static int SelectAppPid(
        IReadOnlyDictionary<int, Queue<(long Tick, double Ms)>> byPid,
        IReadOnlyDictionary<int, string> names,
        long now,
        IReadOnlySet<int>? games = null) => DominantPresenter(byPid, names, now, excludeInfrastructure: true, games);

    /// <summary>What the last <see cref="CurrentAppFps"/> reading came from ("StarCitizen", "Desktop"), or null.</summary>
    public string? CurrentAppSource { get; private set; }

    /// <summary>A presenter's name for the screen: the process without ".exe"; the compositor is "Desktop". Pure.</summary>
    public static string? SourceName(string? application)
    {
        if (string.IsNullOrWhiteSpace(application) || application.StartsWith('<'))
        {
            return null;
        }

        return application.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase)
            ? "Desktop"
            : application.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? application[..^4] : application;
    }

    /// <summary>
    /// The frame rate of what is on screen (#112), pure: the busiest game if one is running, else the
    /// desktop compositor (dwm.exe) — the rate the desktop actually reaches the display at. Never an
    /// ordinary window's own redraw rate (that is not what the screen shows). 0 = nothing presenting.
    /// </summary>
    public static int SelectScreenPid(
        IReadOnlyDictionary<int, Queue<(long Tick, double Ms)>> byPid,
        IReadOnlyDictionary<int, string> names,
        long now,
        IReadOnlySet<int> games)
    {
        var game = SelectAppPid(byPid, names, now, games);
        if (game != 0)
        {
            return game;
        }

        var compositor = byPid.Keys.Where(pid => names.TryGetValue(pid, out var n) && n.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase)).ToHashSet();
        return DominantPresenter(byPid, names, now, excludeInfrastructure: false, compositor);
    }

    /// <summary>
    /// Whether a PresentMon present mode means the app owns the screen (exclusive or borderless
    /// fullscreen flipped straight to the display) — how games present, unlike windowed apps,
    /// which are composed by the desktop ("Composed: Flip", "Composed: Copy …").
    /// </summary>
    public static bool IsFullscreenPresentMode(string? mode) =>
        mode is not null
        && (mode.Contains("Independent Flip", StringComparison.OrdinalIgnoreCase)
            || mode.Contains("Legacy Flip", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Which process's FPS to report (0 = nothing presenting), pure so it can be unit-tested.
    /// "Whatever's rendering most": the app presenting the most frames in the last <see cref="FreshMs"/>,
    /// independent of window focus. The compositor + shell (<see cref="NonGame"/>) are excluded from that
    /// pick so a real app always wins over the desktop; but if NOTHING app-like is presenting we fall back
    /// to the busiest presenter of all — dwm.exe, the compositor, i.e. the monitor's refresh rate — so an
    /// idle desktop shows a number instead of blanking.
    /// </summary>
    public static int SelectFpsPid(
        IReadOnlyDictionary<int, Queue<(long Tick, double Ms)>> byPid,
        IReadOnlyDictionary<int, string> names,
        long now)
    {
        var app = DominantPresenter(byPid, names, now, excludeInfrastructure: true);
        return app != 0 ? app : DominantPresenter(byPid, names, now, excludeInfrastructure: false);
    }

    /// <summary>
    /// The process presenting the most frames in the last <see cref="FreshMs"/> (0 = fewer than 5 from
    /// anything eligible). With <paramref name="excludeInfrastructure"/> the compositor/shell
    /// (<see cref="NonGame"/>) are skipped so a real app wins; without it only ourselves are skipped, so
    /// dwm.exe (the desktop compositor = refresh rate) is eligible as the never-blank last resort.
    /// </summary>
    private static int DominantPresenter(
        IReadOnlyDictionary<int, Queue<(long Tick, double Ms)>> byPid,
        IReadOnlyDictionary<int, string> names,
        long now,
        bool excludeInfrastructure,
        IReadOnlySet<int>? only = null)
    {
        int best = 0, bestCount = 0;
        foreach (var (pid, q) in byPid)
        {
            if (only is not null && !only.Contains(pid))
            {
                continue;
            }

            if (names.TryGetValue(pid, out var name)
                && (name.Equals(Self, StringComparison.OrdinalIgnoreCase)
                    || (excludeInfrastructure && NonGame.Contains(name))))
            {
                continue;
            }

            var count = FreshCount(q, now);
            if (count > bestCount)
            {
                bestCount = count;
                best = pid;
            }
        }

        return bestCount >= 5 ? best : 0;
    }

    private static int FreshCount(Queue<(long Tick, double Ms)> q, long now)
    {
        var count = 0;
        foreach (var e in q)
        {
            if (now - e.Tick <= FreshMs)
            {
                count++;
            }
        }

        return count;
    }

    private static int? RollingFps(Queue<(long Tick, double Ms)> q, long now)
    {
        var sum = 0.0;
        var n = 0;
        foreach (var e in q)
        {
            if (now - e.Tick <= FreshMs)
            {
                sum += e.Ms;
                n++;
            }
        }

        return n >= 5 && sum > 0 ? (int)((1000.0 * n / sum) + 0.5) : null;
    }

    private void ReadLoop()
    {
        var pos = 0L;
        var lineLeft = "";
        var carry = Array.Empty<byte>();
        var utf16 = false;
        var encChecked = false;
        var msCol = -1;
        var pidCol = -1;
        var appCol = -1;
        var modeCol = -1;

        while (_running)
        {
            try
            {
                if (_csvPath is { } path && File.Exists(path))
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length > pos)
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        var nb = new byte[fs.Length - pos];
                        var read = fs.Read(nb, 0, nb.Length);
                        pos += read;

                        var all = new byte[carry.Length + read];
                        Array.Copy(carry, all, carry.Length);
                        Array.Copy(nb, 0, all, carry.Length, read);

                        if (!encChecked)
                        {
                            encChecked = true;
                            utf16 = all.Length >= 2 && all[0] == 0xFF && all[1] == 0xFE;
                        }

                        string chunk;
                        if (utf16)
                        {
                            var usable = all.Length - (all.Length % 2);
                            chunk = Encoding.Unicode.GetString(all, 0, usable);
                            carry = all[usable..];
                        }
                        else
                        {
                            chunk = Encoding.UTF8.GetString(all);
                            carry = Array.Empty<byte>();
                        }

                        if (chunk.Length > 0 && chunk[0] == '﻿')
                        {
                            chunk = chunk[1..];
                        }

                        var lines = (lineLeft + chunk).Split('\n');
                        for (var i = 0; i < lines.Length - 1; i++)
                        {
                            ParseLine(lines[i].TrimEnd('\r'), ref msCol, ref pidCol, ref appCol, ref modeCol);
                        }

                        lineLeft = lines[^1];
                    }
                }
            }
            catch
            {
                // transient file/read issues — retry on the next poll
            }

            Thread.Sleep(200);
        }
    }

    private void ParseLine(string line, ref int msCol, ref int pidCol, ref int appCol, ref int modeCol)
    {
        if (line.Length == 0)
        {
            return;
        }

        if (msCol < 0 || pidCol < 0)
        {
            if (line.IndexOf(MsColumn, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            var cols = line.Split(',');
            for (var j = 0; j < cols.Length; j++)
            {
                var name = cols[j].Trim();
                if (name.Equals(MsColumn, StringComparison.OrdinalIgnoreCase)) msCol = j;
                else if (name.Equals(PidColumn, StringComparison.OrdinalIgnoreCase)) pidCol = j;
                else if (name.Equals(AppColumn, StringComparison.OrdinalIgnoreCase)) appCol = j;
                else if (name.Equals(ModeColumn, StringComparison.OrdinalIgnoreCase)) modeCol = j;
            }

            return;
        }

        var f = line.Split(',');
        if (msCol >= f.Length || pidCol >= f.Length)
        {
            return;
        }

        if (!int.TryParse(f[pidCol].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return;
        }

        if (!double.TryParse(f[msCol].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ms) || ms <= 0.0001)
        {
            return;
        }

        var app = appCol >= 0 && appCol < f.Length ? f[appCol].Trim() : null;
        var fullscreen = modeCol >= 0 && modeCol < f.Length && IsFullscreenPresentMode(f[modeCol]);

        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (!_byPid.TryGetValue(pid, out var q))
            {
                _byPid[pid] = q = new Queue<(long, double)>(FrameWindow + 1);
            }

            if (!string.IsNullOrEmpty(app))
            {
                _names[pid] = app;
            }

            if (fullscreen)
            {
                _games.Add(pid);
            }

            q.Enqueue((now, ms));
            while (q.Count > FrameWindow)
            {
                q.Dequeue();
            }

            // keep the map from growing without bound as games come and go
            if (_byPid.Count > 32)
            {
                PruneStalePids(now);
            }
        }

        if (!_loggedFrames)
        {
            _loggedFrames = true;
            _log?.Invoke("FPS monitor: PresentMon is producing frames");
        }
    }

    private void PruneStalePids(long now)
    {
        var dead = new List<int>();
        foreach (var (pid, q) in _byPid)
        {
            if (q.Count == 0 || now - q.Last().Tick > 5000)
            {
                dead.Add(pid);
            }
        }

        foreach (var pid in dead)
        {
            _byPid.Remove(pid);
            _names.Remove(pid);
            _games.Remove(pid);
        }
    }

    /// <summary>
    /// The process whose window is in front AND fills its whole monitor without being an ordinary
    /// maximized window — a borderless-fullscreen game (or fullscreen video) that the desktop still
    /// composes, e.g. because an overlay is on top. 0 when none.
    /// </summary>
    private static int ForegroundFullscreenPid()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || IsZoomed(hwnd) || !GetWindowRect(hwnd, out var window))
            {
                return 0;
            }

            var monitor = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor))
            {
                return 0;
            }

            var m = monitor.rcMonitor;
            if (window.Left > m.Left || window.Top > m.Top || window.Right < m.Right || window.Bottom < m.Bottom)
            {
                return 0;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            return (int)pid;
        }
        catch
        {
            return 0;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private string? EnsurePresentMon()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var ourExe = Path.Combine(local, "DeviceMaster", "PresentMon.exe");
        if (File.Exists(ourExe))
        {
            return ourExe;
        }

        // reuse StarMaster's copy if present — same tool/version, avoids a redundant download
        var starMaster = Path.Combine(local, "StarMaster", "PresentMon.exe");
        if (File.Exists(starMaster))
        {
            return starMaster;
        }

        try
        {
            _log?.Invoke("FPS monitor: downloading PresentMon…");
            Directory.CreateDirectory(Path.GetDirectoryName(ourExe)!);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DeviceMaster");
            var bytes = http.GetByteArrayAsync(PresentMonUrl).GetAwaiter().GetResult();
            File.WriteAllBytes(ourExe, bytes);
            return ourExe;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FPS monitor: PresentMon download failed ({ex.Message}) — FPS will show a dash");
            return null;
        }
    }

    private void CleanOldCsvs()
    {
        try
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), "DeviceMaster-fps-*.csv"))
            {
                try { File.Delete(f); } catch { /* in use */ }
            }
        }
        catch
        {
            // best effort
        }
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            // kill OUR cmd + its PresentMon child only (never a global by-name kill — that would
            // take down StarMaster's PresentMon too). The scoped session name isolates ETW state.
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // already gone
        }

        _proc = null;
    }
}
