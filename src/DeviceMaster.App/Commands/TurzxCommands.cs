using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Text;
using DeviceMaster.Control;
using DeviceMaster.Devices.Turzx;
using DeviceMaster.Sensors;
using Serilog;

namespace DeviceMaster.App.Commands;

/// <summary>
/// Diagnostic for the Turzx 8.8" serial screen. `turzx probe` acquires the COM port (retrying
/// through the desktop app's retry-backoff windows, since a serial port is exclusive) and tries
/// a matrix of line-state configurations, reporting which one lets a write complete and whether
/// the panel replies to HELLO. Read-only intent — it only sends the harmless HELLO command.
/// </summary>
public static class TurzxCommands
{
    public static int Run(string[] args)
    {
        var sub = args.Length > 1 && !args[1].StartsWith('-') ? args[1].ToLowerInvariant() : "probe";
        return sub switch
        {
            "probe" => Probe(GetCom(args)),
            "frame" => Frame(GetCom(args), GetColor(args)),
            "metric" => MetricFrame(GetCom(args)),
            "dash" => Dash(args),
            "fps" => FpsProbe(GetSeconds(args, 15)),
            "bench" => Bench(GetCom(args)),
            "baud" => BaudScan(GetCom(args)),
            "hello" => HelloLoop(GetCom(args)),
            "listen" => Listen(GetCom(args), GetSeconds(args, 8)),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Log.Information("usage: turzx probe [--com COM3]");
        Log.Information("       turzx frame [--color white|black|red|green|blue] [--com COM3]  (drive a solid full frame)");
        Log.Information("       turzx metric [--com COM3]                        (render a sample metric frame through the real renderer)");
        Log.Information("       turzx dash [--live] [--out path.png]             (render the CPU/FPS/GPU dashboard to a PNG for inspection)");
        Log.Information("       turzx fps [--seconds 15]                         (ELEVATED: PresentMon — prints live FPS; run a game)");
        Log.Information("       turzx listen [--seconds 8] [--com COM3]          (passive: log whatever the panel emits, no protocol write)");
        return 1;
    }

    /// <summary>Renders a sample metric (CPU 63 °C) via the control loop's exact renderer and pushes it — verifies text + orientation.</summary>
    private static int MetricFrame(string? comOverride)
    {
        var com = comOverride ?? TurzxScreen.Find().FirstOrDefault().ComPort;
        if (com is null)
        {
            Log.Error("No Turzx screen found in the serial scan (pass --com COMx to force).");
            return 1;
        }

        var frame = LcdMetricRenderer.Render(1920, 480, "CPU", "63", "°C", (122, 167, 255));

        TurzxScreen? screen = null;
        var deadline = Environment.TickCount64 + 25_000;
        while (Environment.TickCount64 < deadline)
        {
            try { screen = TurzxScreen.Open(com); break; }
            catch (UnauthorizedAccessException) { Thread.Sleep(500); }
            catch (Exception ex) { Log.Error("open {Com} failed: {Msg}", com, ex.Message); return 1; }
        }

        if (screen is null)
        {
            Log.Warning("could not acquire {Com} within 25 s (desktop app holding it?)", com);
            return 1;
        }

        try
        {
            Log.Information("Rendering a sample metric (CPU 63 °C) and pushing to {Com}...", com);
            var sw = Stopwatch.StartNew();
            screen.SetBrightness(100);
            screen.SendJpegFrame(frame);
            sw.Stop();
            Log.Information("Metric frame pushed in {Ms} ms (rom={Rom}). LOOK AT THE PANEL — is 'CPU 63 °C' shown upright?",
                sw.ElapsedMilliseconds, screen.RomVersion?.ToString() ?? "?");
        }
        catch (Exception ex)
        {
            Log.Error("push failed: {Type}: {Msg}", ex.GetType().Name, ex.Message);
        }
        finally
        {
            screen.Dispose();
        }

        return 0;
    }

    private static int GetSeconds(string[] args, int fallback)
    {
        var i = Array.FindIndex(args, a => a.Equals("--seconds", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var s) ? Math.Clamp(s, 1, 120) : fallback;
    }

    private static string GetColor(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--color", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : "white";
    }

    /// <summary>Named test colors — red/green/blue reveal the panel's channel order (BGRA vs RGBA).</summary>
    private static Color NamedColor(string name) => name switch
    {
        "black" => Color.Black,
        "red" => Color.Red,
        "green" => Color.Lime,
        "blue" => Color.Blue,
        _ => Color.White,
    };

    /// <summary>Drives a solid full frame through the real TurzxScreen path (brightness + BGRA push).</summary>
    private static int Frame(string? comOverride, string color)
    {
        var com = comOverride ?? TurzxScreen.Find().FirstOrDefault().ComPort;
        if (com is null)
        {
            Log.Error("No Turzx screen found in the serial scan (pass --com COMx to force).");
            return 1;
        }

        var jpeg = SolidJpeg(1920, 480, NamedColor(color));

        TurzxScreen? screen = null;
        var deadline = Environment.TickCount64 + 25_000;
        while (Environment.TickCount64 < deadline)
        {
            try { screen = TurzxScreen.Open(com); break; }
            catch (UnauthorizedAccessException) { Thread.Sleep(500); } // app holds it; wait for a backoff window
            catch (Exception ex) { Log.Error("open {Com} failed: {Msg}", com, ex.Message); return 1; }
        }

        if (screen is null)
        {
            Log.Warning("could not acquire {Com} within 25 s (desktop app holding it?)", com);
            return 1;
        }

        try
        {
            Log.Information("Pushing a {Color} full frame + brightness 100 to {Com}...", color, com);
            var sw = Stopwatch.StartNew();
            screen.SetBrightness(100);
            screen.SendJpegFrame(jpeg);
            sw.Stop();
            Log.Information("Frame pushed in {Ms} ms (rom={Rom}). LOOK AT THE PANEL — did it turn {Color}?",
                sw.ElapsedMilliseconds, screen.RomVersion?.ToString() ?? "?", color);
        }
        catch (Exception ex)
        {
            Log.Error("push failed: {Type}: {Msg}", ex.GetType().Name, ex.Message);
        }
        finally
        {
            screen.Dispose();
        }

        return 0;
    }

    /// <summary>Renders the CPU/FPS/GPU dashboard to a PNG (sample data, or --live from real sensors) for inspection.</summary>
    private static int Dash(string[] args)
    {
        var outPath = GetOut(args) ?? Path.Combine(Path.GetTempPath(), "turzx-dash.png");
        var live = args.Any(a => a.Equals("--live", StringComparison.OrdinalIgnoreCase));

        SystemStats stats;
        int? fps;
        if (live)
        {
            using var lhm = new LhmSensorSource();
            _ = lhm.ReadSystemStats(); // first read primes some LHM counters (e.g. loads)
            using var fpsReader = PresentMonFpsReader.StartOrNull(m => Log.Information("  {Msg}", m));
            Thread.Sleep(1300); // let LHM counters settle + the present monitor fill a 1 s window
            stats = lhm.ReadSystemStats();
            fps = fpsReader?.CurrentFps() ?? RtssFpsReader.ReadCurrentFps();
        }
        else
        {
            stats = new SystemStats("AMD Ryzen 7 9800X3D", 80, 60, 324, 36,
                "NVIDIA GeForce RTX 5090", 76, 45, 467, 22);
            fps = 144;
        }

        var png = TurzxDashboardRenderer.Render(1920, 480, stats, fps);
        File.WriteAllBytes(outPath, png);
        Log.Information("dashboard PNG ({Mode}) written: {Path}", live ? "live" : "sample", outPath);
        Log.Information("  CPU {Name}: load={Load} temp={Temp} pow={Pow} ram={Ram}GB",
            stats.CpuName, F(stats.CpuLoadPercent, "%"), F(stats.CpuTempC, "°C"), F(stats.CpuPowerW, "W"), F(stats.RamUsedGb, ""));
        Log.Information("  GPU {Name}: load={Load} temp={Temp} pow={Pow} vram={Vram}GB",
            stats.GpuName, F(stats.GpuLoadPercent, "%"), F(stats.GpuTempC, "°C"), F(stats.GpuPowerW, "W"), F(stats.VramUsedGb, ""));
        Log.Information("  FPS {Fps}", fps?.ToString() ?? "— (no app presenting right now)");
        return 0;

        static string F(double? v, string u) => v is { } x ? $"{x:F0}{u}" : "—";
    }

    private static string? GetOut(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Runs the ETW present monitor and prints live FPS — needs an elevated terminal.</summary>
    private static int FpsProbe(int seconds)
    {
        Log.Information("Starting PresentMon for {Sec}s (needs admin; downloads PresentMon on first run). Run a game/3D app to see FPS...", seconds);
        using var reader = PresentMonFpsReader.StartOrNull(msg => Log.Information("  {Msg}", msg), "DeviceMaster-FPS-Probe");
        if (reader is null)
        {
            Log.Warning("Could not start the FPS monitor (PresentMon unavailable) — run this from an ELEVATED terminal.");
            return 1;
        }

        var deadline = Environment.TickCount64 + seconds * 1000L;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(500);
            var fps = reader.CurrentFps();
            Log.Information("FPS = {Fps}", fps?.ToString() ?? "— (nothing presenting)");
        }

        return 0;
    }

    /// <summary>Writes increasing raw payloads and reports throughput — reveals if the device drains fast (USB) or slow (real UART).</summary>
    private static int Bench(string? comOverride)
    {
        var com = comOverride ?? TurzxScreen.Find().FirstOrDefault().ComPort;
        if (com is null) { Log.Error("No Turzx screen found (pass --com COMx)."); return 1; }

        var port = Acquire(com, Handshake.None, dtr: true, rts: true, writeTimeoutMs: 20_000);
        if (port is null) { Log.Warning("could not acquire {Com}", com); return 1; }

        try
        {
            foreach (var size in new[] { 1_024, 4_096, 16_384, 65_536, 262_144, 1_048_576 })
            {
                var data = new byte[size];
                try { port.DiscardOutBuffer(); } catch { /* ignore */ }
                var sw = Stopwatch.StartNew();
                try
                {
                    port.Write(data, 0, data.Length);
                    sw.Stop();
                    var kbps = size / 1024.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                    Log.Information("wrote {Size,8} B in {Ms,6} ms  (~{Rate:F0} KB/s)", size, sw.ElapsedMilliseconds, kbps);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Log.Warning("wrote {Size,8} B — FAILED after {Ms} ms: {Msg}", size, sw.ElapsedMilliseconds, ex.Message);
                    break;
                }
            }
        }
        finally { port.Dispose(); }

        return 0;
    }

    /// <summary>Sends HELLO at several baud rates, looking for the panel's "chs_" ID reply.</summary>
    private static int BaudScan(string? comOverride)
    {
        var com = comOverride ?? TurzxScreen.Find().FirstOrDefault().ComPort;
        if (com is null) { Log.Error("No Turzx screen found (pass --com COMx)."); return 1; }

        var hello = TurzxProtocol.BuildCommand(TurzxProtocol.Hello);
        foreach (var baud in new[] { 115200, 230400, 460800, 921600, 1_500_000, 2_000_000 })
        {
            SerialPort? port = null;
            var deadline = Environment.TickCount64 + 15_000;
            while (Environment.TickCount64 < deadline)
            {
                try { port = new SerialPort(com, baud) { Handshake = Handshake.None, DtrEnable = true, RtsEnable = true, ReadTimeout = 2000, WriteTimeout = 4000 }; port.Open(); break; }
                catch (UnauthorizedAccessException) { port?.Dispose(); port = null; Thread.Sleep(500); }
                catch (Exception ex) { Log.Warning("[{Baud}] open failed: {Msg}", baud, ex.Message); port = null; break; }
            }
            if (port is null) { Log.Warning("[{Baud}] not acquired", baud); continue; }

            try
            {
                try { port.DiscardInBuffer(); port.DiscardOutBuffer(); } catch { }
                try { port.Write(hello, 0, hello.Length); } catch (Exception ex) { Log.Warning("[{Baud}] write failed: {Msg}", baud, ex.Message); continue; }
                Log.Information("[{Baud}] reply='{Reply}'", baud, ReadReply(port));
            }
            finally { port.Dispose(); Thread.Sleep(300); }
        }

        return 0;
    }

    /// <summary>Reference-faithful HELLO handshake: send, read 23 bytes, retry up to 8× (1 s apart), looking for "chs_".</summary>
    private static int HelloLoop(string? comOverride)
    {
        var com = comOverride ?? TurzxScreen.Find().FirstOrDefault().ComPort;
        if (com is null) { Log.Error("No Turzx screen found (pass --com COMx)."); return 1; }

        var port = Acquire(com, Handshake.None, dtr: true, rts: true, writeTimeoutMs: 4000);
        if (port is null) { Log.Warning("could not acquire {Com}", com); return 1; }

        var hello = TurzxProtocol.BuildCommand(TurzxProtocol.Hello);
        try
        {
            port.ReadTimeout = 1000;
            for (var attempt = 1; attempt <= 8; attempt++)
            {
                try { port.DiscardInBuffer(); port.DiscardOutBuffer(); } catch { /* ignore */ }
                try { port.Write(hello, 0, hello.Length); }
                catch (Exception ex) { Log.Warning("attempt {N}: write failed: {Msg}", attempt, ex.Message); Thread.Sleep(1000); continue; }

                var reply = ReadReply(port);
                Log.Information("attempt {N}: reply='{Reply}'", attempt, reply);
                if (reply.StartsWith("chs_", StringComparison.Ordinal))
                {
                    Log.Information("HANDSHAKE OK — the panel speaks our protocol. ID={Reply}", reply);
                    return 0;
                }

                Thread.Sleep(1000);
            }

            Log.Warning("No 'chs_' reply after 8 attempts — the panel is not answering our HELLO.");
        }
        finally { port.Dispose(); }

        return 0;
    }

    /// <summary>
    /// Passive listener. Opens the port and reads WITHOUT sending any protocol command, watching
    /// for a boot banner / unsolicited device ID / heartbeat — the tell-tale of a "speak-first"
    /// panel (our HELLO handshake always writes first, so it would miss one). Then two of the most
    /// harmless possible nudges: a fresh DTR assertion (CDC "terminal present" edge, which makes
    /// many WCH panels emit their ID) and a single CR/LF. No large writes — stays inside the
    /// proven-safe envelope (big writes with no valid receive-mode command knock COM3 off the bus).
    /// </summary>
    private static int Listen(string? comOverride, int seconds)
    {
        var com = comOverride ?? TurzxScreen.Find().FirstOrDefault().ComPort;
        if (com is null) { Log.Error("No Turzx screen found in the serial scan (pass --com COMx to force)."); return 1; }

        var port = Acquire(com, Handshake.None, dtr: true, rts: true, writeTimeoutMs: 4000);
        if (port is null)
        {
            Log.Warning("could not acquire {Com} (the desktop app holds it; note it backs off 5 min after a failed handshake, so retry then or exit the app)", com);
            return 1;
        }

        try
        {
            port.ReadTimeout = 250;
            Log.Information("Listening on {Com} for {Sec}s — NOT sending any protocol command. Watching for a banner / ID / heartbeat...", com, seconds);
            var total = ListenWindow(port, seconds * 1000, "passive");

            Log.Information("Toggling DTR (drop 150 ms, re-assert) then listening 3 s...");
            try { port.DtrEnable = false; Thread.Sleep(150); port.DtrEnable = true; } catch (Exception ex) { Log.Warning("DTR toggle failed: {Msg}", ex.Message); }
            total += ListenWindow(port, 3000, "after DTR re-assert");

            Log.Information("Sending a single CR/LF then listening 3 s...");
            try { port.Write(new byte[] { 0x0d, 0x0a }, 0, 2); } catch (Exception ex) { Log.Warning("CR/LF write failed: {Msg}", ex.Message); }
            total += ListenWindow(port, 3000, "after CR/LF");

            Log.Information(total == 0
                ? "Panel emitted NOTHING unsolicited across all windows — it is a write-first protocol (only replies when addressed with the command it expects)."
                : "Panel emitted {Total} byte(s) total — see the hex dumps above; a leading printable run is likely its device ID.", total);
        }
        finally { port.Dispose(); }

        return 0;
    }

    /// <summary>Reads for <paramref name="windowMs"/>, dumping any bytes as hex + printable ASCII. Returns the byte count.</summary>
    private static int ListenWindow(SerialPort port, int windowMs, string label)
    {
        var buffer = new List<byte>();
        var scratch = new byte[512];
        var deadline = Environment.TickCount64 + windowMs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                if (port.BytesToRead > 0)
                {
                    var n = port.Read(scratch, 0, Math.Min(scratch.Length, port.BytesToRead));
                    for (var i = 0; i < n; i++)
                    {
                        buffer.Add(scratch[i]);
                    }

                    deadline = Environment.TickCount64 + 400; // extend a little while bytes keep flowing
                }
                else
                {
                    Thread.Sleep(20);
                }
            }
            catch (TimeoutException) { /* keep polling until the window closes */ }
            catch (Exception ex) { Log.Warning("[{Label}] read error: {Msg}", label, ex.Message); break; }
        }

        if (buffer.Count == 0)
        {
            Log.Information("[{Label}] (silence)", label);
            return 0;
        }

        var bytes = buffer.ToArray();
        var hex = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 64)));
        var ascii = new StringBuilder();
        foreach (var b in bytes)
        {
            ascii.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
        }

        Log.Information("[{Label}] {Count} byte(s): {Hex}{More}", label, bytes.Length, hex, bytes.Length > 64 ? "…" : "");
        Log.Information("[{Label}] ascii: {Ascii}", label, ascii.ToString());
        return bytes.Length;
    }

    private static SerialPort? Acquire(string com, Handshake hs, bool dtr, bool rts, int writeTimeoutMs)
    {
        var deadline = Environment.TickCount64 + 25_000;
        while (Environment.TickCount64 < deadline)
        {
            SerialPort? port = null;
            try
            {
                port = new SerialPort(com, 115200) { Handshake = hs, ReadTimeout = 1500, WriteTimeout = writeTimeoutMs };
                port.Open();
                if (hs == Handshake.None) { port.RtsEnable = rts; }
                port.DtrEnable = dtr;
                return port;
            }
            catch (UnauthorizedAccessException) { port?.Dispose(); Thread.Sleep(500); }
            catch (Exception ex) { Log.Error("open {Com} failed: {Msg}", com, ex.Message); port?.Dispose(); return null; }
        }

        return null;
    }

    private static byte[] SolidJpeg(int width, int height, Color color)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(color);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg);
        return stream.ToArray();
    }

    private static string? GetCom(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--com", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Probe(string? comOverride)
    {
        var com = comOverride ?? TurzxScreen.Find().FirstOrDefault().ComPort;
        if (com is null)
        {
            Log.Error("No Turzx screen found in the serial scan (pass --com COMx to force).");
            return 1;
        }

        var hello = TurzxProtocol.BuildCommand(TurzxProtocol.Hello);
        Log.Information("Probing Turzx on {Com}. HELLO is {Len} bytes.", com, hello.Length);

        // Session A — Handshake.None, all four DTR/RTS line-state combinations.
        var combos = new (bool Dtr, bool Rts)[] { (false, false), (true, false), (false, true), (true, true) };
        foreach (var (dtr, rts) in combos)
        {
            RunOne($"none DTR={(dtr ? 1 : 0)} RTS={(rts ? 1 : 0)}", com, hello, Handshake.None, dtr, rts, toggleDtr: false);
        }

        // Session B — a DTR pulse before writing (WCH parts often gate the data path on DTR).
        RunOne("none DTR-pulse RTS=0", com, hello, Handshake.None, dtr: true, rts: false, toggleDtr: true);

        // Session C — the reference's rtscts.
        RunOne("rtscts DTR=1", com, hello, Handshake.RequestToSend, dtr: true, rts: false, toggleDtr: false);

        Log.Information("Probe complete. The config that reports 'write OK' (ideally with a chs_ reply) is the one to use.");
        return 0;
    }

    private static void RunOne(string name, string com, byte[] hello, Handshake hs, bool dtr, bool rts, bool toggleDtr)
    {
        SerialPort? port = null;
        var deadline = Environment.TickCount64 + 25_000;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                port = new SerialPort(com, 115200) { Handshake = hs, ReadTimeout = 1500, WriteTimeout = 5000 };
                port.Open();
                break;
            }
            catch (UnauthorizedAccessException)
            {
                port?.Dispose();
                port = null;
                Thread.Sleep(500); // the desktop app holds it; wait for its backoff window
            }
            catch (Exception ex)
            {
                Log.Warning("[{Name}] open failed: {Msg}", name, ex.Message);
                port?.Dispose();
                return;
            }
        }

        if (port is null)
        {
            Log.Warning("[{Name}] could not acquire {Com} within 25 s (app holding it?)", name, com);
            return;
        }

        try
        {
            if (hs == Handshake.None)
            {
                port.RtsEnable = rts;
            }

            if (toggleDtr)
            {
                port.DtrEnable = false;
                Thread.Sleep(60);
                port.DtrEnable = true;
                Thread.Sleep(120);
            }
            else
            {
                port.DtrEnable = dtr;
            }

            try { port.DiscardInBuffer(); port.DiscardOutBuffer(); } catch { /* ignore */ }

            var sw = Stopwatch.StartNew();
            string writeResult;
            try
            {
                port.Write(hello, 0, hello.Length);
                sw.Stop();
                writeResult = $"write OK in {sw.ElapsedMilliseconds} ms";
            }
            catch (Exception ex)
            {
                sw.Stop();
                writeResult = $"write FAILED in {sw.ElapsedMilliseconds} ms — {ex.GetType().Name}: {ex.Message}";
            }

            var reply = ReadReply(port);
            Log.Information("[{Name}] {Write} | reply='{Reply}'", name, writeResult, reply);
        }
        finally
        {
            port.Dispose();
            Thread.Sleep(400); // let the port settle before the next session
        }
    }

    private static string ReadReply(SerialPort port)
    {
        try
        {
            var buffer = new byte[23];
            var read = port.Read(buffer, 0, buffer.Length);
            var sb = new StringBuilder();
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] is >= 0x20 and < 0x7f)
                {
                    sb.Append((char)buffer[i]);
                }
            }

            return read == 0 ? "(0 bytes)" : sb.ToString();
        }
        catch (Exception ex)
        {
            return $"(no reply: {ex.GetType().Name})";
        }
    }
}
