using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
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
    private const int FrameWindow = 60;   // rolling average length, like StarMaster
    private const long FreshMs = 2000;    // a frame older than this no longer counts toward "presenting now"

    // Processes that always "present" (the desktop compositor, the shell, ourselves) but are never
    // the game whose FPS we want. dwm.exe in particular presents at the refresh rate right alongside
    // a composed game, so it must never win the "what is presenting" pick.
    private static readonly HashSet<string> NonGame = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm.exe", "explorer.exe", "DeviceMaster.exe", "ApplicationFrameHost.exe", "SearchHost.exe",
        "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "TextInputHost.exe", "SystemSettings.exe",
    };

    private readonly Action<string>? _log;
    private readonly string _sessionName;
    private readonly object _gate = new();
    private readonly Dictionary<int, Queue<(long Tick, double Ms)>> _byPid = new();
    private readonly Dictionary<int, string> _names = new(); // pid → Application, to skip non-game presenters
    private int _fpsPid;                                     // the game we are locked onto (survives alt-tab)

    private Thread? _reader;
    private Process? _proc;
    private string? _csvPath;
    private volatile bool _running;
    private bool _loggedFrames;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

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
    /// FPS of the game (rolling ~60-frame average), or null if nothing game-like is presenting.
    ///
    /// This does NOT hard-gate on the live foreground PID the way a naïve reader would — that blanks
    /// the moment an alt-tab or a fullscreen⇄composed transition makes the foreground window's PID
    /// stop lining up with the presenting PID, and then fails to re-acquire. Instead we lock onto the
    /// game and keep reporting it (StarMaster stays locked to its target process the same way): prefer
    /// the foreground process when it is itself a game that's presenting, otherwise keep reporting the
    /// process we locked onto while it is still presenting, otherwise re-acquire the dominant non-system
    /// presenter. dwm.exe/the shell/ourselves are excluded so the compositor can never win the pick.
    /// </summary>
    public int? CurrentFps()
    {
        var fg = ForegroundPid();
        var now = Environment.TickCount64;
        lock (_gate)
        {
            _fpsPid = SelectFpsPid(_byPid, _names, fg, _fpsPid, now);
            return _fpsPid != 0 ? RollingFps(_byPid[_fpsPid], now) : null;
        }
    }

    /// <summary>
    /// Chooses which process's FPS to report (0 = none), pure so it can be unit-tested. The order is
    /// what makes the reading survive an alt-tab or a fullscreen⇄composed transition instead of
    /// blanking: (1) the foreground window if it is itself a game presenting now; else (2) the game we
    /// were already locked onto while it keeps presenting (foreground may briefly not line up with the
    /// presenting PID); else (3) re-acquire the non-system process presenting the most frames. Entries
    /// in <see cref="NonGame"/> (dwm/shell/ourselves) are never eligible — dwm.exe presents at the
    /// refresh rate right alongside a composed game and must never win.
    /// </summary>
    public static int SelectFpsPid(
        IReadOnlyDictionary<int, Queue<(long Tick, double Ms)>> byPid,
        IReadOnlyDictionary<int, string> names,
        int foregroundPid,
        int lockedPid,
        long now)
    {
        if (IsPresentingGame(byPid, names, foregroundPid, now))
        {
            return foregroundPid;
        }

        if (lockedPid != 0 && IsPresentingGame(byPid, names, lockedPid, now))
        {
            return lockedPid;
        }

        return DominantGame(byPid, names, now);
    }

    private static int ForegroundPid()
    {
        try { GetWindowThreadProcessId(GetForegroundWindow(), out var p); return p > 0 ? p : 0; }
        catch { return 0; }
    }

    /// <summary>True if <paramref name="pid"/> is a non-system process presenting ≥5 frames recently.</summary>
    private static bool IsPresentingGame(
        IReadOnlyDictionary<int, Queue<(long Tick, double Ms)>> byPid,
        IReadOnlyDictionary<int, string> names,
        int pid,
        long now)
    {
        if (pid <= 0 || (names.TryGetValue(pid, out var name) && NonGame.Contains(name)))
        {
            return false;
        }

        return byPid.TryGetValue(pid, out var q) && FreshCount(q, now) >= 5;
    }

    /// <summary>The non-system process with the most frames presented in the last <see cref="FreshMs"/>.</summary>
    private static int DominantGame(
        IReadOnlyDictionary<int, Queue<(long Tick, double Ms)>> byPid,
        IReadOnlyDictionary<int, string> names,
        long now)
    {
        int best = 0, bestCount = 0;
        foreach (var (pid, q) in byPid)
        {
            if (names.TryGetValue(pid, out var name) && NonGame.Contains(name))
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
                            ParseLine(lines[i].TrimEnd('\r'), ref msCol, ref pidCol, ref appCol);
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

    private void ParseLine(string line, ref int msCol, ref int pidCol, ref int appCol)
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
        }
    }

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
