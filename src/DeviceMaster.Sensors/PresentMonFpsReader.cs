using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace DeviceMaster.Sensors;

/// <summary>
/// Measures game FPS the way PresentMon does — a real-time ETW session on the
/// <c>Microsoft-Windows-DXGI</c> provider, counting <c>Present</c> calls (event id 42) per
/// process. FPS is the present rate of the busiest non-compositor process over the last second
/// (the game). Requires administrator rights to start the session; when it can't start (or no
/// app is presenting) <see cref="CurrentFps"/> returns null so the UI shows a placeholder.
///
/// Only the ETW event header is read (provider, event id, process id), so no manifest/payload
/// parsing and no native TraceEvent components are needed. Every ETW call is guarded — a failure
/// degrades to "no FPS", it never propagates.
/// </summary>
public sealed class PresentMonFpsReader : IDisposable
{
    public const string DefaultSessionName = "DeviceMaster-FPS";
    private const string DxgiProvider = "Microsoft-Windows-DXGI";
    private const int DxgiPresentStartEventId = 42; // DXGIPresent_Start
    private const long WindowMs = 1000;
    private const int MinFps = 5; // below this the "game" is really just idle desktop churn

    private readonly object _gate = new();
    private readonly Dictionary<int, Queue<long>> _presents = new(); // pid -> present arrival ticks
    private readonly HashSet<int> _ignoredPids = new();
    private readonly Action<string>? _log;

    private readonly string _sessionName;
    private TraceEventSession? _session;
    private Thread? _thread;
    private bool _sawFirstPresent;

    private PresentMonFpsReader(Action<string>? log, string sessionName)
    {
        _log = log;
        _sessionName = sessionName;
    }

    /// <summary>Starts an FPS monitor, or returns null if the ETW session can't be created (e.g. not elevated).</summary>
    public static PresentMonFpsReader? StartOrNull(Action<string>? log = null, string sessionName = DefaultSessionName)
    {
        var reader = new PresentMonFpsReader(log, sessionName);
        return reader.Start() ? reader : null;
    }

    private bool Start()
    {
        try
        {
            _ignoredPids.Add(Environment.ProcessId);
            foreach (var dwm in Process.GetProcessesByName("dwm"))
            {
                try { _ignoredPids.Add(dwm.Id); } catch { /* ignore */ }
                dwm.Dispose();
            }

            // a session from a previous crash keeps running until stopped — clear it first
            try { TraceEventSession.GetActiveSession(_sessionName)?.Stop(); } catch { /* none active */ }

            _session = new TraceEventSession(_sessionName) { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider);
            _session.Source.Dynamic.All += OnEvent;

            _thread = new Thread(RunProcessing) { IsBackground = true, Name = "DeviceMaster FPS ETW" };
            _thread.Start();
            _log?.Invoke("FPS monitor: DXGI present ETW session started");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FPS monitor unavailable ({ex.GetType().Name}: {ex.Message}) — FPS will show a dash");
            Dispose();
            return false;
        }
    }

    private void RunProcessing()
    {
        try
        {
            _session?.Source.Process(); // blocks until the session is disposed
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FPS monitor stopped: {ex.Message}");
        }
    }

    private void OnEvent(TraceEvent evt)
    {
        if ((int)evt.ID != DxgiPresentStartEventId)
        {
            return;
        }

        var pid = evt.ProcessID;
        if (pid <= 0 || _ignoredPids.Contains(pid))
        {
            return;
        }

        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (!_presents.TryGetValue(pid, out var q))
            {
                _presents[pid] = q = new Queue<long>(256);
            }

            q.Enqueue(now);
            while (q.Count > 0 && now - q.Peek() > WindowMs)
            {
                q.Dequeue();
            }
        }

        if (!_sawFirstPresent)
        {
            _sawFirstPresent = true;
            _log?.Invoke($"FPS monitor: receiving present events (first from pid {pid})");
        }
    }

    /// <summary>Present rate (per second) of the busiest presenting process, or null when nothing is rendering.</summary>
    public int? CurrentFps()
    {
        var now = Environment.TickCount64;
        var best = 0;
        lock (_gate)
        {
            foreach (var (pid, q) in _presents)
            {
                while (q.Count > 0 && now - q.Peek() > WindowMs)
                {
                    q.Dequeue();
                }

                if (!_ignoredPids.Contains(pid) && q.Count > best)
                {
                    best = q.Count;
                }
            }
        }

        return best >= MinFps ? best : null;
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { /* best effort */ }
        _session = null;
    }
}
