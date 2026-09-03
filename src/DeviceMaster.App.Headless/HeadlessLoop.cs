using System.Text.Json;
using DeviceMaster.Core.Curves;
using DeviceMaster.Core.Safety;
using DeviceMaster.Core.Sensors;
using DeviceMaster.Control;
using DeviceMaster.Devices.CorsairLink;
using DeviceMaster.Devices.CorsairLink.Protocol;
using DeviceMaster.Devices.EneRgb;
using DeviceMaster.Platform.Linux;
using DeviceMaster.Sensors.Linux;

namespace DeviceMaster.App.Headless;

/// <summary>
/// The headless (Linux/container) control loop. Same 1 Hz policy as the Windows loop, scoped
/// to the device set that exists on the Linux host: every iCUE LINK hub found (fans + pump,
/// all hubs), the GPU's ENE RGB chip over raw i2c, and the pump LCD. Reuses the exact same
/// device sessions (LinkHub, EneRgbDevice, CorsairLcdDevice) and safety primitives as the
/// Windows app — no protocol code is duplicated.
///
/// Safety (mirrors CLAUDE.md): pump duty hard-floored, sensor failure ⇒ 100%, hub writes only
/// through LinkHub (VID/PID-identified), Stop() restores hardware mode on every hub.
/// </summary>
public sealed class HeadlessLoop : IDisposable
{
    // tick policy — same constants as the Windows ControlLoop
    private const int TickMs = 1000;
    private const int CorsairRefreshTicks = 10;   // rewrite unchanged duties every N ticks
    private const int CorsairRescanTicks = 30;    // re-enumerate the chains every N ticks
    private const int LcdSolidKeepaliveMs = 30_000; // pump panel reasserts its own screen ~30 s
    private const int RgbPersistSettleMs = 10_000;

    // v91 hub-color policy: color writes are ACKed with no readback, so colors re-stream on a
    // steady cadence; pump-bearing hubs additionally get a periodic DEEP repaint (software-mode
    // re-assert + full color-path rebuild) — the only proven heal for a link-blipped device
    // that ACKs frames without painting them (2026-08-29).
    private const int HubRgbRefreshMs = 20_000;
    private const int HubDeepRepaintMs = 3 * 60_000;

    private readonly object _gate = new();
    private readonly Action<string> _log;
    private readonly HeadlessConfig _initial;
    private volatile HeadlessConfig _config;
    private long _configMtimeTicks;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private readonly AutoResetEvent _wake = new(false);

    private readonly List<LinkHub> _hubs = [];
    private readonly Dictionary<string, long> _hubOpenNotBefore = []; // serial -> cooldown (failed opens)
    private readonly HashSet<string> _hubOpenWarned = [];             // warned about this serial's failures
    private readonly Dictionary<string, int> _hubReceivedDuty = [];   // serial -> last duty that reached it
    private readonly Dictionary<string, long> _hubDeepRepaintDue = []; // serial -> next deep-repaint deadline
    private readonly Dictionary<string, string> _appliedHubRgb = [];
    private readonly Dictionary<string, long> _hubRgbRetryAt = [];
    private readonly Dictionary<string, long> _hubRgbRefreshDue = []; // serial -> next steady re-stream

    private LinuxI2cSmBus? _gpuBus;
    private EneRgbDevice? _gpuRgb;
    private bool _gpuRgbScanned;
    private string? _appliedGpuRgb;
    private string? _gpuPersistedKey;
    private long _gpuRgbRetryAt;
    private long _gpuPersistDue;

    private CorsairLcdDevice? _lcd;
    private long _lcdRetryAt;
    private long _lcdFrameDue;
    private long _lcdSolidKeepaliveDue;
    private string _lcdShownKey = "";
    private int _appliedLcdBrightness = -1;

    private int _ticksSinceWrite;
    private int _ticksSinceRescan;
    private long _statusFileDue;
    private long _lastTickLogAt;
    private volatile ControlStatus _status = new();

    public ControlStatus Status => _status;

    public HeadlessLoop(HeadlessConfig config, Action<string>? log = null)
    {
        _initial = config;
        _config = config;
        _log = log ?? (static _ => { });
        // skip the "reload" on the first tick: remember the file as we loaded it
        try
        {
            var info = new FileInfo(HeadlessConfig.DefaultPath);
            _configMtimeTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
        }
        catch
        {
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "DeviceMaster headless loop" };
            _thread.Start();
            _log("headless: control loop started");
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_thread is null)
            {
                return;
            }

            _cts?.Cancel();
            thread = _thread;
            _thread = null;
        }

        thread?.Join(TimeSpan.FromSeconds(10));
        ReleaseHardware();
        _status = new ControlStatus();
        _log("headless: control loop stopped, hubs restored to hardware mode");
    }

    public void Dispose() => Stop();

    public void Wake() => _wake.Set();

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            try
            {
                CheckConfig();
                Tick();
            }
            catch (Exception ex)
            {
                _log($"headless: tick failed: {ex.Message}");
                TryFailsafeWrite();
            }

            var elapsed = Environment.TickCount64 - started;
            if (Environment.TickCount64 >= _lastTickLogAt)
            {
                _lastTickLogAt = Environment.TickCount64 + 30_000;
                _log($"headless: tick took {elapsed} ms (target {TickMs})");
            }

            var wait = (int)Math.Max(50, TickMs - elapsed);
            if (WaitHandle.WaitAny([token.WaitHandle, _wake], wait) == 0)
            {
                break;
            }
        }
    }

    // ---- config (live-reload on file change) ----

    private void CheckConfig()
    {
        try
        {
            var path = HeadlessConfig.DefaultPath;
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return;
            }

            var ticks = info.LastWriteTimeUtc.Ticks;
            if (ticks == _configMtimeTicks)
            {
                return;
            }

            _configMtimeTicks = ticks;
            var reloaded = HeadlessConfig.Load(path);
            _log("headless: config reloaded from " + path);
            _config = reloaded;
            _hubReceivedDuty.Clear(); // the new settings' duty must reach every hub on the next tick
        }
        catch (Exception ex)
        {
            _log($"headless: config reload failed: {ex.Message}");
        }
    }

    // ---- one 1 Hz tick ----

    private void Tick()
    {
        var cfg = _config;
        var warnings = new List<string>();

        EnsureHubs(cfg, warnings);
        EnsureGpuRgb(cfg, warnings);
        EnsureLcd(cfg, warnings);

        var readings = new List<DeviceReading>();
        var coolant = TryReadCoolant(readings);

        // Mode Off: leave the hardware's own curves in charge — exit software mode if we entered it
        if (cfg.Control.Mode == ControlMode.Off)
        {
            foreach (var hub in _hubs)
            {
                if (hub.InSoftwareMode)
                {
                    try
                    {
                        hub.EnterHardwareMode();
                        _log($"hub {hub.SerialNumber[..8]}… back to hardware mode (mode=Off)");
                    }
                    catch
                    {
                    }
                }
            }

            var sourceTempForStatus = cfg.Control.Source == CurveSource.Coolant
                ? coolant
                : ReadSourceTemperature(cfg);
            Publish(cfg, sourceTempForStatus, coolant, 0, false, readings, warnings);
            return;
        }

        // ---- decide the duty ----
        double? sourceTemp = null;
        var failsafe = false;
        int duty;
        if (cfg.Control.Mode == ControlMode.Manual)
        {
            duty = SafetyGuard.ClampFanDuty(cfg.Control.ManualDutyPercent);
        }
        else
        {
            sourceTemp = ReadSourceTemperature(cfg);
            if (!SensorValidity.IsPlausibleTemperature(sourceTemp))
            {
                duty = SafetyGuard.DutyOnSensorFailure();
                failsafe = true;
                warnings.Add($"{cfg.Control.Source} temperature unavailable — failsafe 100%");
            }
            else
            {
                duty = cfg.Control.Curve.EvaluateDuty(sourceTemp.Value);
            }
        }

        var pumpDuty = SafetyGuard.ClampPumpDuty(cfg.Control.PumpDutyPercent);

        // ---- write duties (per-hub write-on-change + periodic refresh, per-hub error isolation) ----
        // Per-hub tracking (not global) so a hub that just (re)opened gets its duty THIS tick —
        // the v90 fix: a reopened hub must not wait for the next refresh window.
        var refreshDue = _ticksSinceWrite >= CorsairRefreshTicks;
        if (refreshDue)
        {
            _ticksSinceWrite = 0;
        }
        else
        {
            _ticksSinceWrite++;
        }

        foreach (var hub in _hubs.ToList())
        {
            // deep repaint (pump-bearing hubs): a link-blipped device ACKs re-streamed frames
            // without painting them and gives no protocol-visible signal, so run the
            // app-restart init sequence on a timer — the only proven heal
            var deepRepaint = hub.Channels.Any(c => c.IsPump)
                && Environment.TickCount64 >= _hubDeepRepaintDue.GetValueOrDefault(hub.SerialNumber);
            if (deepRepaint)
            {
                _hubDeepRepaintDue[hub.SerialNumber] = Environment.TickCount64 + HubDeepRepaintMs;
                try
                {
                    hub.EnterSoftwareMode(); // re-assert even though already in software mode
                    hub.InvalidateColorPath();
                    _appliedHubRgb.Remove(hub.SerialNumber);
                    _log($"hub {hub.SerialNumber[..8]}… deep repaint: software mode re-asserted, "
                        + "color path invalidated — colors re-stream this tick");
                }
                catch (Exception ex)
                {
                    _log($"hub {hub.SerialNumber[..8]}… deep repaint failed: {ex.Message}");
                }
            }

            var needsWrite = _hubReceivedDuty.GetValueOrDefault(hub.SerialNumber) != duty
                || refreshDue
                || deepRepaint; // re-assert duties right behind the mode re-entry
            if (!needsWrite)
            {
                continue;
            }

            try
            {
                var requested = new Dictionary<int, int>();
                foreach (var channel in hub.Channels)
                {
                    if (!channel.IsPump && channel.Info is { Flags: var f } && f.HasFlag(LinkDeviceFlags.ControlsSpeed))
                    {
                        requested[channel.Channel] = duty;
                    }
                }

                var written = hub.WriteFixedDuties(requested, pumpDuty);
                _hubReceivedDuty[hub.SerialNumber] = duty;
                if (failsafe || deepRepaint || refreshDue)
                {
                    _log($"hub {hub.SerialNumber[..8]}… duties written: fans={duty}% pump={pumpDuty}% ({written} channels)"
                        + (failsafe ? " (FAILSAFE)" : ""));
                }
            }
            catch (Exception ex)
            {
                _hubReceivedDuty.Remove(hub.SerialNumber);
                DropHub(hub, $"speed write failed: {ex.Message}", warnings);
            }
        }

        // ---- rescan the chains (topology changes: a device moved/removed) ----
        if (++_ticksSinceRescan >= CorsairRescanTicks)
        {
            _ticksSinceRescan = 0;
            foreach (var hub in _hubs.ToList())
            {
                try
                {
                    var before = hub.ChannelSignature();
                    hub.EnumerateChannels(allowEnterSoftwareMode: true);
                    if (before != hub.ChannelSignature())
                    {
                        _log($"hub {hub.SerialNumber[..8]}… chain changed: [{hub.ChannelSignature()}]");
                        _appliedHubRgb.Remove(hub.SerialNumber); // slots rebuilt — re-apply color
                        _hubReceivedDuty.Remove(hub.SerialNumber); // duties must reach the new map this tick
                    }

                    try
                    {
                        if (hub.SyncLedRegistry(_log))
                        {
                            _appliedHubRgb.Remove(hub.SerialNumber);
                        }
                    }
                    catch (Exception ex)
                    {
                        // registry maintenance is best-effort — it must never take down the
                        // speed-control session (v21.0 did exactly that, every rescan)
                        _log($"hub {hub.SerialNumber[..8]}… LED registry sync skipped: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    DropHub(hub, $"rescan failed: {ex.Message}", warnings);
                }
            }
        }

        // ---- RGB (hubs + GPU ENE) ----
        if (cfg.Control.RgbEnabled)
        {
            ApplyHubRgb(cfg, warnings);
        }

        ApplyGpuRgb(cfg, warnings);

        // ---- pump LCD ----
        ApplyLcd(cfg, coolant, sourceTemp, duty, pumpDuty, readings, warnings);

        // ---- telemetry into readings (RPMs) + publish ----
        Publish(cfg, sourceTemp, coolant, duty, failsafe, readings, warnings);
    }

    private void Publish(HeadlessConfig cfg, double? sourceTemp, double? coolant, int duty,
        bool failsafe, List<DeviceReading> readings, List<string> warnings)
    {
        var cpu = SafeRead(() => Hwmon.CpuTemperatureC());
        var gpu = SafeRead(() => NvidiaSmi.ReadFirst(cfg.NvidiaSmiPath)?.TemperatureC);

        _status = new ControlStatus
        {
            Running = true,
            Mode = cfg.Control.Mode,
            SourceName = cfg.Control.Mode == ControlMode.Manual ? "manual" : cfg.Control.Source.ToString(),
            SourceTemperatureC = sourceTemp,
            CoolantTemperatureC = coolant,
            CpuTemperatureC = cpu,
            GpuTemperatureC = gpu,
            TargetDutyPercent = duty,
            FailsafeActive = failsafe,
            Devices = readings,
            Warnings = warnings,
        };

        // optional status snapshot file (dashboards / external monitoring)
        if (cfg.StatusFile is { Length: > 0 } && Environment.TickCount64 >= _statusFileDue)
        {
            _statusFileDue = Environment.TickCount64 + Math.Max(1, cfg.StatusFileEverySeconds) * 1000L;
            try
            {
                var tmp = cfg.StatusFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_status,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, cfg.StatusFile, overwrite: true);
            }
            catch (Exception ex)
            {
                _log($"headless: status file write failed: {ex.Message}");
            }
        }
    }

    private static T? SafeRead<T>(Func<T?> read) where T : struct
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    // ---- hubs ----

    private void EnsureHubs(HeadlessConfig cfg, List<string> warnings)
    {
        foreach (var device in LinkHub.FindHubDevices())
        {
            var serial = TryGetSerial(device);
            if (_hubs.Any(h => h.SerialNumber == serial))
            {
                continue;
            }

            if (Environment.TickCount64 < _hubOpenNotBefore.GetValueOrDefault(serial))
            {
                continue;
            }

            try
            {
                var hub = LinkHub.Open(device, cfg.Trace ? _log : null);
                hub.EnumerateChannels(allowEnterSoftwareMode: true);
                if (hub.HasUnknownChannels)
                {
                    _log($"hub {hub.SerialNumber[..8]}… has unrecognized chain devices — not managed");
                    hub.Dispose();
                    continue;
                }

                _hubs.Add(hub);
                _appliedHubRgb.Remove(hub.SerialNumber);
                _hubRgbRefreshDue.Remove(hub.SerialNumber);
                _hubReceivedDuty.Remove(hub.SerialNumber); // first duty write lands this tick
                _hubDeepRepaintDue[hub.SerialNumber] = Environment.TickCount64 + HubDeepRepaintMs;
                _hubOpenNotBefore.Remove(hub.SerialNumber);
                _log($"headless: opened Link hub {hub.SerialNumber[..8]}… fw {hub.FirmwareVersion}: [{hub.ChannelSignature()}]");

                try
                {
                    hub.SyncLedRegistry(_log); // phantom-registry cleanup + LED enrollment pulse (once per open)
                }
                catch (Exception ex)
                {
                    _log($"hub {hub.SerialNumber[..8]}… LED registry check failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                var firstFailure = !_hubOpenWarned.Contains(serial);
                _hubOpenNotBefore[serial] = Environment.TickCount64 + 30_000;
                _hubOpenWarned.Add(serial);
                if (firstFailure)
                {
                    warnings.Add($"Link hub open failed: {ex.Message} (retrying in 30 s)");
                }
            }
        }
    }

    private static string TryGetSerial(HidSharp.HidDevice device)
    {
        try
        {
            return device.GetSerialNumber();
        }
        catch
        {
            return device.DevicePath;
        }
    }

    /// <summary>A session that errors is dropped (the hub may have lost power) — it reopens on a later tick.</summary>
    private void DropHub(LinkHub hub, string reason, List<string> warnings)
    {
        _log($"headless: dropping hub {hub.SerialNumber[..8]}… session ({reason})");
        try
        {
            hub.Dispose();
        }
        catch
        {
        }

        _hubs.Remove(hub);
        _appliedHubRgb.Remove(hub.SerialNumber);
        _hubRgbRefreshDue.Remove(hub.SerialNumber);
        _hubReceivedDuty.Remove(hub.SerialNumber);
        _hubDeepRepaintDue.Remove(hub.SerialNumber);
        _hubOpenNotBefore[hub.SerialNumber] = Environment.TickCount64 + 5_000;
        warnings.Add($"hub {hub.SerialNumber[..8]}…: {reason}");
    }

    // failsafe on a crashed tick: 100% everywhere, best effort
    private void TryFailsafeWrite()
    {
        foreach (var hub in _hubs.ToList())
        {
            try
            {
                hub.WriteSafeDefaults();
                _log($"headless: FAILSAFE duties (100%) written to hub {hub.SerialNumber[..8]}…");
            }
            catch (Exception ex)
            {
                _log($"headless: failsafe write to hub {hub.SerialNumber[..8]}… failed: {ex.Message}");
            }
        }
    }

    // ---- sensors ----

    private double? ReadSourceTemperature(HeadlessConfig cfg)
    {
        return cfg.Control.Source switch
        {
            CurveSource.Coolant => TryReadCoolant(null),
            CurveSource.Cpu => Hwmon.CpuTemperatureC(),
            CurveSource.Gpu => NvidiaSmi.ReadFirst(cfg.NvidiaSmiPath)?.TemperatureC,
            _ => null,
        };
    }

    /// <summary>Loop coolant temperature from the hub chain's pump channel (all hubs, first live reading).</summary>
    private double? TryReadCoolant(List<DeviceReading>? readings)
    {
        double? coolant = null;
        foreach (var hub in _hubs)
        {
            try
            {
                var speeds = hub.ReadSpeeds();
                var temps = hub.ReadTemperatures();

                if (readings is not null)
                {
                    foreach (var channel in hub.Channels)
                    {
                        var speed = speeds.FirstOrDefault(s => s.Channel == channel.Channel);
                        var temp = temps.FirstOrDefault(t => t.Channel == channel.Channel);
                        readings.Add(new DeviceReading(
                            "corsair-link", channel.Name, speed.IsAvailable ? speed.Rpm : null,
                            0, channel.IsPump, HubSerial: hub.SerialNumber, Channel: channel.Channel));
                    }
                }

                if (coolant is null)
                {
                    foreach (var channel in hub.Channels.Where(c => c.IsPump))
                    {
                        var reading = temps.FirstOrDefault(t => t.Channel == channel.Channel && t.IsAvailable);
                        if (reading.TemperatureCelsius is { } t)
                        {
                            coolant = t;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"hub {hub.SerialNumber[..8]}… telemetry read failed: {ex.Message}");
                DropHub(hub, $"telemetry read failed: {ex.Message}", []);
            }
        }

        return coolant;
    }

    // ---- hub RGB ----

    private void ApplyHubRgb(HeadlessConfig cfg, List<string> warnings)
    {
        var (r, g, b) = RgbTarget(cfg);
        var key = $"{r},{g},{b}";

        foreach (var hub in _hubs.ToList())
        {
            // steady re-stream (every HubRgbRefreshMs): color writes are ACKed with no readback
            var isNewColor = _appliedHubRgb.GetValueOrDefault(hub.SerialNumber) != key;
            if (!isNewColor && Environment.TickCount64 < _hubRgbRefreshDue.GetValueOrDefault(hub.SerialNumber))
            {
                continue;
            }

            if (Environment.TickCount64 < _hubRgbRetryAt.GetValueOrDefault(hub.SerialNumber))
            {
                continue;
            }

            try
            {
                hub.ApplyStaticColor(r, g, b);
                _appliedHubRgb[hub.SerialNumber] = key;
                _hubRgbRetryAt.Remove(hub.SerialNumber);
                _hubRgbRefreshDue[hub.SerialNumber] = Environment.TickCount64 + HubRgbRefreshMs;
                if (isNewColor)
                {
                    _log($"RGB applied on hub {hub.SerialNumber[..8]}…: {hub.TotalLeds} LEDs");
                }
            }
            catch (Exception ex)
            {
                // drop the applied key so a failed steady-state refresh recovers through the
                // same retry path as a failed first apply
                _appliedHubRgb.Remove(hub.SerialNumber);
                _hubRgbRetryAt[hub.SerialNumber] = Environment.TickCount64 + 10_000;
                warnings.Add($"RGB on hub {hub.SerialNumber[..8]}… failed: {ex.Message} (retry in 10 s)");
                _log($"RGB on hub {hub.SerialNumber[..8]}… failed: {ex.Message}");
            }
        }
    }

    private static (byte, byte, byte) RgbTarget(HeadlessConfig cfg)
    {
        var bright = Math.Clamp(cfg.Control.RgbBrightness, 0, 100);
        byte Scale(int channel) => (byte)Math.Clamp(Math.Clamp(channel, 0, 255) * bright / 100, 0, 255);
        return cfg.Control.RgbOff
            ? ((byte)0, (byte)0, (byte)0)
            : (Scale(cfg.Control.RgbR), Scale(cfg.Control.RgbG), Scale(cfg.Control.RgbB));
    }

    // ---- GPU ENE RGB ----

    private void EnsureGpuRgb(HeadlessConfig cfg, List<string> warnings)
    {
        if (_gpuRgbScanned || !cfg.GpuRgbEnabled)
        {
            return;
        }

        _gpuRgbScanned = true;
        try
        {
            string? path = cfg.I2cDevice;
            if (path is null)
            {
                path = GpuI2cLocator.Find(cfg.GpuPciAddress)?.DevicePath;
            }

            if (path is null)
            {
                _log("headless: no NVIDIA i2c adapter found — GPU RGB unavailable");
                return;
            }

            using (var probe = new LinuxI2cSmBus(path))
            {
                if (!EneRgbDevice.Fingerprint(probe, 0x67))
                {
                    _log($"headless: no ENE controller at 0x67 on {path} — GPU RGB unavailable");
                    return;
                }
            }

            _gpuBus = new LinuxI2cSmBus(path);
            _gpuRgb = new EneRgbDevice(_gpuBus, 0x67);
            if (_gpuRgb.Initialize())
            {
                _log($"headless: ENE controller on {path}: '{_gpuRgb.Version}', {_gpuRgb.LedCount} LEDs");
            }
            else
            {
                _log($"headless: ENE at 0x67 on {path} did not initialize — GPU RGB unavailable");
                _gpuRgb = null;
                _gpuBus.Dispose();
                _gpuBus = null;
            }
        }
        catch (Exception ex)
        {
            _log($"headless: GPU ENE scan failed: {ex.Message}");
            warnings.Add($"GPU ENE scan failed: {ex.Message}");
        }
    }

    private void ApplyGpuRgb(HeadlessConfig cfg, List<string> warnings)
    {
        if (_gpuRgb is null || !cfg.GpuRgbEnabled)
        {
            return;
        }

        var (r, g, b) = RgbTarget(cfg);
        var key = $"{r},{g},{b}";
        if (_appliedGpuRgb != key && Environment.TickCount64 >= _gpuRgbRetryAt)
        {
            try
            {
                _gpuRgb.ApplyStaticColor(r, g, b, persist: false);
                _appliedGpuRgb = key;
                _gpuRgbRetryAt = 0;
                _log($"RGB applied on GPU ENE ({_gpuRgb.LedCount} LEDs)");
            }
            catch (Exception ex)
            {
                _gpuRgbRetryAt = Environment.TickCount64 + 30_000;
                _appliedGpuRgb = null;
                warnings.Add($"GPU ENE RGB failed: {ex.Message} (retry in 30 s)");
            }
        }

        // volatile chip: persist to flash once the color has settled (same policy as the Windows loop)
        if (_appliedGpuRgb == key && _gpuPersistedKey != key)
        {
            if (_gpuPersistDue == 0)
            {
                _gpuPersistDue = Environment.TickCount64 + RgbPersistSettleMs; // start the settle window
            }
            else if (Environment.TickCount64 >= _gpuPersistDue)
            {
                try
                {
                    _gpuRgb.Persist();
                    _gpuPersistedKey = key;
                    _gpuPersistDue = 0;
                    _log("GPU ENE RGB persisted to flash");
                }
                catch (Exception ex)
                {
                    _log($"GPU ENE persist failed: {ex.Message}");
                }
            }
        }
        else
        {
            _gpuPersistDue = 0; // not applied (or key changed) — reset the settle window
        }
    }

    // ---- pump LCD ----

    private void EnsureLcd(HeadlessConfig cfg, List<string> warnings)
    {
        if (cfg.Control.LcdScreens == LcdMode.Unmanaged || _lcd is not null)
        {
            return;
        }

        if (Environment.TickCount64 < _lcdRetryAt)
        {
            return;
        }

        try
        {
            _lcd = CorsairLcdDevice.FindDevices().FirstOrDefault() is { } device
                ? CorsairLcdDevice.Open(device)
                : null;
            if (_lcd is not null)
            {
                _log($"headless: opened pump LCD (serial {_lcd.SerialNumber})");
            }
        }
        catch (Exception ex)
        {
            _lcdRetryAt = Environment.TickCount64 + 10_000;
            warnings.Add($"pump LCD open failed: {ex.Message}");
        }
    }

    private void ApplyLcd(HeadlessConfig cfg, double? coolant, double? sourceTemp, int duty,
        int pumpDuty, List<DeviceReading> readings, List<string> warnings)
    {
        var lcd = _lcd;
        if (lcd is null || cfg.Control.LcdScreens == LcdMode.Unmanaged)
        {
            return;
        }

        try
        {
            var brightness = cfg.Control.LcdScreens == LcdMode.Off ? 0 : Math.Clamp(cfg.Control.LcdBrightness, 0, 100);
            if (_appliedLcdBrightness != brightness)
            {
                lcd.SetBrightness(brightness);
                _appliedLcdBrightness = brightness;
            }

            var accent = ((byte)128, (byte)0, (byte)255); // house purple
            switch (cfg.Control.LcdScreens)
            {
                case LcdMode.Black:
                case LcdMode.White:
                    var solid = cfg.Control.LcdScreens == LcdMode.Black
                        ? LcdFrames.Solid(480, 480, 0, 0, 0)
                        : LcdFrames.Solid(480, 480, 255, 255, 255);
                    // the panel reasserts its own liquid-temp screen when frames stop — keep the
                    // solid background alive (same keepalive as the Windows loop)
                    if (_lcdShownKey != cfg.Control.LcdScreens.ToString()
                        || Environment.TickCount64 >= _lcdSolidKeepaliveDue)
                    {
                        lcd.SendJpegFrame(solid);
                        _lcdShownKey = cfg.Control.LcdScreens.ToString();
                        _lcdSolidKeepaliveDue = Environment.TickCount64 + LcdSolidKeepaliveMs;
                    }

                    break;

                case LcdMode.Metrics:
                    if (Environment.TickCount64 < _lcdFrameDue)
                    {
                        break;
                    }

                    var (label, value, unit) = MetricValue(cfg.Control.PumpScreenMetric, coolant, sourceTemp, duty, pumpDuty, readings);
                    var key = $"{label}|{value}|{unit}";
                    if (key != _lcdShownKey)
                    {
                        var frame = LcdMetricRenderer.Render(480, 480, label, value, unit, accent);
                        lcd.SendJpegFrame(frame);
                        _lcdShownKey = key;
                        _lcdFrameDue = Environment.TickCount64 + 2_000; // ~0.5 fps content churn
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            _log($"headless: pump LCD write failed: {ex.Message} — reopening");
            try
            {
                lcd.Dispose();
            }
            catch
            {
            }

            _lcd = null;
            _appliedLcdBrightness = -1;
            _lcdRetryAt = Environment.TickCount64 + 10_000;
            warnings.Add($"pump LCD write failed: {ex.Message}");
        }
    }

    private (string Label, string Value, string Unit) MetricValue(
        LcdMetric metric, double? coolant, double? sourceTemp, int duty, int pumpDuty, List<DeviceReading> readings)
    {
        var cpu = SafeRead(() => Hwmon.CpuTemperatureC());
        var gpu = SafeRead(() => NvidiaSmi.ReadFirst(_config.NvidiaSmiPath)?.TemperatureC);
        var pumpRpm = readings.FirstOrDefault(d => d.IsPump)?.Rpm;

        return metric switch
        {
            LcdMetric.Coolant => ("COOLANT", F(coolant), "°C"),
            LcdMetric.CpuTemp => ("CPU", F(cpu), "°C"),
            LcdMetric.GpuTemp => ("GPU", F(gpu), "°C"),
            LcdMetric.FanDuty => ("FAN DUTY", duty.ToString(), "%"),
            LcdMetric.PumpDuty => ("PUMP DUTY", pumpDuty.ToString(), "%"),
            LcdMetric.PumpRpm => ("PUMP", pumpRpm?.ToString() ?? "--", "RPM"),
            LcdMetric.FanRpm => ("FAN", readings.Where(d => !d.IsPump).Select(d => d.Rpm).FirstOrDefault()?.ToString() ?? "--", "RPM"),
            LcdMetric.Date => (DateTime.Now.ToString("ddd"), DateTime.Now.ToString("HH:mm"), DateTime.Now.ToString("MMM dd")),
            _ => ("COOLANT", F(coolant), "°C"),
        };

        static string F(double? v) => v is { } x ? x.ToString("0") : "--";
    }

    // ---- teardown ----

    private void ReleaseHardware()
    {
        foreach (var hub in _hubs)
        {
            try
            {
                hub.EnterHardwareMode();
            }
            catch
            {
                // the hub also recovers on its own power cycle
            }

            try
            {
                hub.Dispose();
            }
            catch
            {
            }
        }

        _hubs.Clear();

        try
        {
            _lcd?.Dispose();
        }
        catch
        {
        }

        _lcd = null;
        _appliedLcdBrightness = -1;
        _lcdShownKey = "";

        try
        {
            _gpuBus?.Dispose();
        }
        catch
        {
        }

        _gpuBus = null;
        _gpuRgb = null;
        _appliedGpuRgb = null;
        _gpuPersistedKey = null;
        _gpuRgbScanned = false;

        _hubReceivedDuty.Clear();
        _hubDeepRepaintDue.Clear();
        _hubRgbRefreshDue.Clear();
    }
}
