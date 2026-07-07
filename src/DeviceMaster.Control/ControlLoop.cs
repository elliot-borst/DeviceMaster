using DeviceMaster.Core.Curves;
using DeviceMaster.Core.Safety;
using DeviceMaster.Core.Sensors;
using DeviceMaster.Devices.AsusAura;
using DeviceMaster.Devices.CorsairLink;
using DeviceMaster.Devices.EneRgb;
using DeviceMaster.Devices.LianLi;
using DeviceMaster.Devices.Nvidia;
using DeviceMaster.Devices.Turzx;
using DeviceMaster.Sensors;

namespace DeviceMaster.Control;

public sealed record DeviceReading(
    string Family, string Name, int? Rpm, int AppliedDutyPercent, bool IsPump,
    string? Id = null, string? HubSerial = null, int? Channel = null);

/// <summary>Immutable snapshot of the loop's last tick, safe to read from any thread.</summary>
public sealed record ControlStatus
{
    public bool Running { get; init; }
    public ControlMode Mode { get; init; }
    public string SourceName { get; init; } = "";
    public double? SourceTemperatureC { get; init; }

    /// <summary>Loop coolant temperature, read every tick regardless of the curve source.</summary>
    public double? CoolantTemperatureC { get; init; }

    /// <summary>CPU/GPU temperatures, sampled every couple of seconds (for dashboard tiles).</summary>
    public double? CpuTemperatureC { get; init; }
    public double? GpuTemperatureC { get; init; }

    public int TargetDutyPercent { get; init; }
    public bool FailsafeActive { get; init; }

    /// <summary>Turzx 8.8" screen connection state for the UI ("Connected · COM3", "Not detected", …); null = not managed.</summary>
    public string? TurzxInfo { get; init; }

    public IReadOnlyList<DeviceReading> Devices { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// The 1 Hz control loop. Each tick: read the selected temperature source, evaluate the curve
/// (or take the manual duty), and drive every fan on both families — Corsair Link hubs
/// (write-on-change plus a periodic refresh; pumps always 100%) and Lian Li SL V3 wireless
/// (keepalive PWM every tick, as the firmware requires).
/// Failsafe: a missing or implausible source reading forces 100% everywhere.
/// Stop() restores Corsair hubs to hardware mode; SL V3 fans revert on their own.
/// </summary>
public sealed class ControlLoop : IDisposable
{
    private const int TickMs = 1000;
    private const int CorsairRefreshTicks = 10;   // rewrite unchanged duties every N ticks anyway
    private const int CorsairRescanTicks = 30;    // re-enumerate the Link chain every N ticks

    private readonly object _gate = new();
    private readonly Action<string>? _log;
    private Thread? _thread;
    private Thread? _keepaliveThread;
    private CancellationTokenSource? _cts;
    private volatile ControlSettings _settings;
    private volatile ControlStatus _status = new();

    // SL V3 access is shared between the tick thread and the keepalive thread. The gate covers
    // the _slv3 field and fast TX sends only — RX polling (slow: timeouts, recovery resets)
    // deliberately runs OUTSIDE it, because a starved keepalive reverts every group to
    // firmware defaults (rainbow + own curve) within ~1 s.
    private readonly object _slv3Gate = new();
    private volatile int _slv3KeepaliveDuty = SafetyLimits.FailsafeDutyPercent;
    private volatile bool _slv3Dead; // a thread saw the session fail — the tick reopens it
    private int _slv3OpenFailures;
    private long _slv3UsbRestartNotBefore;

    /// <summary>Failed opens/reopens tolerated before the USB device-node restart (software replug).</summary>
    private const int Slv3FailuresBeforeUsbRestart = 3;
    private const long Slv3UsbRestartCooldownMs = 10 * 60_000;

    /// <summary>A group missing from RX telemetry this long shows "no rpm" (commands continue).</summary>
    private const int Slv3StaleAfterMs = 5_000;

    private readonly List<LinkHub> _hubs = [];
    private Slv3Controller? _slv3;
    private AuraController? _aura;
    private LhmSensorSource? _lhm;
    private LhmFanController? _headers;
    private bool _headersUnavailableWarned;
    private EneRamScanner? _ramScanner;
    private IReadOnlyList<EneRgbDevice> _ramRgb = [];
    private IReadOnlyList<GpuRgb> _gpuRgb = [];
    private bool _chipRgbScanned;

    /// <summary>Detected memory modules (SPD identity), for the hardware inventory. Filled once per session.</summary>
    public IReadOnlyList<RamStick> RamInventory { get; private set; } = [];

    /// <summary>Detected NVIDIA GPUs with board partner and RGB reachability, for the hardware inventory.</summary>
    public IReadOnlyList<GpuRgb> GpuInventory => _gpuRgb;
    private int _lastWrittenCorsairDuty = -1;
    private int _ticksSinceCorsairWrite;
    private int _ticksSinceCorsairRescan;
    private int _lastWrittenHeaderDuty = -1;
    private int _ticksSinceHeaderWrite;

    // "identify this fan" pulses: (hub serial, channel) -> expiry tick
    private readonly Dictionary<(string Hub, int Channel), long> _pulses = [];

    /// <summary>Runs one fan channel at 100% for a few seconds so it can be identified by eye/ear.</summary>
    public void PulseChannel(string hubSerial, int channel, int seconds = 6)
    {
        lock (_pulses)
        {
            _pulses[(hubSerial, channel)] = Environment.TickCount64 + seconds * 1000L;
        }
    }

    private bool TryGetPulse(string hubSerial, int channel)
    {
        lock (_pulses)
        {
            if (_pulses.TryGetValue((hubSerial, channel), out var expiry))
            {
                if (Environment.TickCount64 <= expiry)
                {
                    return true;
                }

                _pulses.Remove((hubSerial, channel));
            }

            return false;
        }
    }

    private bool AnyPulsePending()
    {
        lock (_pulses)
        {
            return _pulses.Count > 0;
        }
    }

    public ControlLoop(ControlSettings settings, Action<string>? log = null)
    {
        _settings = settings;
        _log = log;
    }

    public ControlStatus Status => _status;

    // set when the user picks a new color: the woken tick paints BEFORE its duty/telemetry
    // passes, because the eye notices color lag far more than a briefly deferred duty write
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _rgbPriority;
    private string _lastRgbSignature = "";

    public void Apply(ControlSettings settings)
    {
        _settings = settings;
        _lastWrittenCorsairDuty = -1; // force a rewrite on the next tick

        var rgbSignature = $"{settings.RgbEnabled}|{settings.RgbOff}|{settings.RgbR},{settings.RgbG},{settings.RgbB}";
        if (rgbSignature != _lastRgbSignature)
        {
            _lastRgbSignature = rgbSignature;
            _rgbPriority = true;
        }

        _wake.Set(); // act on the change now, not at the next 1 Hz boundary
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
            _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "DeviceMaster control loop" };
            _thread.Start();

            // SL V3 fans revert to firmware defaults (rainbow, own curve) when RF traffic
            // pauses for ~1 s — the keepalive gets its own thread so a slow tick (SMBus
            // scans, color floods) can never starve it.
            _keepaliveThread = new Thread(() => RunKeepalive(_cts.Token)) { IsBackground = true, Name = "DeviceMaster SL V3 keepalive" };
            _keepaliveThread.Start();

            // the Turzx screen gets its own worker: a full 480×1920 serial frame takes seconds
            // to push, which must never stall the 1 Hz fan/pump tick.
            _turzxThread = new Thread(() => RunTurzx(_cts.Token)) { IsBackground = true, Name = "DeviceMaster Turzx screen" };
            _turzxThread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        Thread? keepalive;
        Thread? turzx;
        lock (_gate)
        {
            if (_thread is null)
            {
                return;
            }

            _cts!.Cancel();
            thread = _thread;
            keepalive = _keepaliveThread;
            turzx = _turzxThread;
            _thread = null;
            _keepaliveThread = null;
            _turzxThread = null;
        }

        _turzxWake.Set(); // wake the Turzx worker so it sees the cancel promptly
        thread.Join(TimeSpan.FromSeconds(10));
        keepalive?.Join(TimeSpan.FromSeconds(5));
        turzx?.Join(TimeSpan.FromSeconds(25)); // may be mid-frame (a full serial push is seconds)
        ReleaseHardware();
        _status = new ControlStatus();
    }

    private void RunKeepalive(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            lock (_slv3Gate)
            {
                if (_slv3 is not null && !_slv3Dead)
                {
                    try
                    {
                        _slv3.SendKeepalive(_slv3KeepaliveDuty);
                    }
                    catch (Exception ex)
                    {
                        // never dispose from this thread — the tick thread may be mid-poll on
                        // the same handles; it reopens once it sees the flag
                        _log?.Invoke($"SL V3 keepalive failed: {ex.Message} — scheduling a dongle reopen");
                        _slv3Dead = true; // fans fail safe on their own until the reopen
                    }
                }
            }

            if (token.WaitHandle.WaitOne(800))
            {
                break;
            }
        }
    }

    public void Dispose() => Stop();

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"control tick failed: {ex.Message}");
                TryFailsafe();
            }

            var elapsed = Environment.TickCount64 - started;
            var wait = (int)Math.Max(50, TickMs - elapsed);
            if (WaitHandle.WaitAny([token.WaitHandle, _wake], wait) == 0)
            {
                break; // cancelled; index 1 (wake) and WaitTimeout both mean "tick now"
            }
        }
    }

    private void Tick()
    {
        var settings = _settings;
        var warnings = new List<string>();

        EnsureDevices(warnings);

        if (_rgbPriority)
        {
            _rgbPriority = false;
            if (settings.RgbEnabled)
            {
                ApplyRgb(settings, warnings); // the later regular call no-ops via key matching
            }
        }

        // ---- decide the duty ----
        double? sourceTemp = null;
        var failsafe = false;
        int duty;
        if (settings.Mode == ControlMode.Manual)
        {
            duty = SafetyGuard.ClampFanDuty(settings.ManualDutyPercent);
        }
        else
        {
            sourceTemp = ReadSourceTemperature(settings.Source, warnings);
            if (!SensorValidity.IsPlausibleTemperature(sourceTemp))
            {
                duty = SafetyGuard.DutyOnSensorFailure();
                failsafe = true;
                warnings.Add($"{settings.Source} temperature unavailable — failsafe 100%");
            }
            else
            {
                duty = settings.Curve.EvaluateDuty(sourceTemp!.Value);
            }
        }

        var pumpDuty = SafetyGuard.ClampPumpDuty(settings.PumpDutyPercent);
        var readings = new List<DeviceReading>();
        ApplyCorsair(duty, pumpDuty, readings, warnings);
        ApplySlv3(duty, readings, warnings);
        ApplyHeaders(duty, readings, warnings);

        if (settings.RgbEnabled)
        {
            ApplyRgb(settings, warnings);
        }

        ApplyLcd(settings, readings, duty, warnings);
        ApplyTurzx(settings, readings, duty);
        SampleDashboardTemps();

        var coolant = settings.Mode == ControlMode.Curve && settings.Source == CurveSource.Coolant
            ? sourceTemp
            : TryReadCoolant();

        _status = new ControlStatus
        {
            Running = true,
            Mode = settings.Mode,
            SourceName = settings.Mode == ControlMode.Manual ? "manual" : settings.Source.ToString(),
            SourceTemperatureC = sourceTemp,
            CoolantTemperatureC = coolant,
            CpuTemperatureC = _dashCpuTemp,
            GpuTemperatureC = _dashGpuTemp,
            TargetDutyPercent = duty,
            FailsafeActive = failsafe,
            TurzxInfo = _turzxStatusText,
            Devices = readings,
            Warnings = warnings,
        };
    }

    // dashboard temperatures, sampled on a slow cadence so LHM isn't hit every tick
    private double? _dashCpuTemp;
    private double? _dashGpuTemp;
    private long _dashTempsDue;

    private void SampleDashboardTemps()
    {
        if (Environment.TickCount64 < _dashTempsDue)
        {
            return;
        }

        _dashTempsDue = Environment.TickCount64 + 2_000;
        try
        {
            _lhm ??= new LhmSensorSource();
            var readings = _lhm.Read();
            _dashCpuTemp = readings
                .Where(r => r.Kind == SensorKind.Temperature && r.Id.Contains("cpu", StringComparison.OrdinalIgnoreCase))
                .Select(r => (double?)r.Value).Max();
            _dashGpuTemp = readings
                .Where(r => r.Kind == SensorKind.Temperature && r.Id.Contains("gpu", StringComparison.OrdinalIgnoreCase))
                .Select(r => (double?)r.Value).Max();
        }
        catch
        {
            // tiles just show a dash
        }
    }

    // ---- device lifecycle ----

    private void EnsureDevices(List<string> warnings)
    {
        if (_hubs.Count == 0)
        {
            foreach (var device in LinkHub.FindHubDevices())
            {
                try
                {
                    var hub = LinkHub.Open(device);
                    hub.EnumerateChannels(allowEnterSoftwareMode: true);
                    if (hub.HasUnknownChannels)
                    {
                        warnings.Add($"Hub {hub.SerialNumber[..8]}… has unrecognized chain devices — skipped");
                        hub.Dispose();
                        continue;
                    }

                    _hubs.Add(hub);
                    _appliedHubRgb.Remove(hub.SerialNumber); // reapply color after a reconnect
                    _log?.Invoke($"control: opened Link hub {hub.SerialNumber[..8]}… fw {hub.FirmwareVersion}: [{hub.ChannelSignature()}]");
                    try
                    {
                        // the hub's own idea of per-channel LEDs — ground truth against the catalog
                        var (codes, leds) = hub.ReadLedDeviceInfo();
                        _log?.Invoke($"hub {hub.SerialNumber[..8]}… 0x1E raw: {Convert.ToHexString(codes.AsSpan(0, 48))}");
                        _log?.Invoke($"hub {hub.SerialNumber[..8]}… 0x1D raw: {Convert.ToHexString(leds.AsSpan(0, 48))}");

                        // the registry is persisted hub-side and goes stale when the chain is
                        // re-arranged — sync it so colors reach the channels that exist NOW
                        hub.SyncLedRegistry(_log);
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"hub {hub.SerialNumber[..8]}… LED registry check failed: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Link hub open failed: {ex.Message}");
                }
            }
        }

        lock (_slv3Gate)
        {
            if (_slv3 is not null && (_slv3Dead || _slv3.NeedsReopen))
            {
                _log?.Invoke("control: reopening the SL V3 dongles");
                _slv3.Dispose();
                _slv3 = null;
                _slv3OpenFailures++; // a wedged session counts toward the USB-restart escalation
            }

            _slv3Dead = false;

            if (_slv3 is null)
            {
                // escalation of last resort — the dongle firmware sometimes wedges so hard that
                // only a replug helps (verified live 2026-07-06); do the replug in software
                if (_slv3OpenFailures >= Slv3FailuresBeforeUsbRestart
                    && Environment.TickCount64 >= _slv3UsbRestartNotBefore)
                {
                    _slv3UsbRestartNotBefore = Environment.TickCount64 + Slv3UsbRestartCooldownMs;
                    _slv3OpenFailures = 0;
                    try
                    {
                        _log?.Invoke("SL V3 dongles unresponsive after repeated reopens — restarting their USB device nodes (software replug)");
                        if (Slv3Controller.RestartDongleDevices(_log) > 0)
                        {
                            Thread.Sleep(3000); // give Windows time to re-enumerate the nodes
                        }
                        else
                        {
                            _log?.Invoke("SL V3 recovery has nothing left to restart — check the dongles' USB cables");
                        }
                    }
                    catch (Exception rex)
                    {
                        _log?.Invoke($"SL V3 USB device restart failed: {rex.Message}");
                    }
                }

                try
                {
                    _slv3 = Slv3Controller.Open(log: _log);
                    _slv3.PollDevices();
                    _appliedSlv3Rgb.Clear(); // reapply color after a reconnect
                    _slv3OpenFailures = 0;
                    var groups = _slv3.Devices
                        .Where(d => d.IsBoundTo(_slv3.MasterMac))
                        .Select(d => $"{d.MacText[..4]}({d.FanCount})");
                    _log?.Invoke($"control: opened SL V3 dongles (master {_slv3.MasterMacText}) — groups: [{string.Join(" ", groups)}]");
                }
                catch (Exception ex)
                {
                    if (++_slv3OpenFailures == 1)
                    {
                        _log?.Invoke($"SL V3 open failed: {ex.Message}");
                    }

                    warnings.Add($"SL V3 open failed: {ex.Message}");
                }
            }
        }

        if (_aura is null && AuraController.FindDevices().FirstOrDefault() is { } auraDevice)
        {
            try
            {
                _aura = AuraController.Open(auraDevice);
                _appliedAuraRgb = null; // reapply color after a reconnect
                _log?.Invoke($"control: opened Aura controller (fw {_aura.FirmwareName}, {_aura.Zones.Count} zones)");
            }
            catch (Exception ex)
            {
                warnings.Add($"Aura open failed: {ex.Message}");
            }
        }

        // chip-level RGB (RAM sticks over SMBus, GPU over NvAPI I2C): scan once — these
        // don't hot-plug, and the SMBus scan is too expensive to repeat every tick
        if (!_chipRgbScanned)
        {
            _chipRgbScanned = true;
            try
            {
                _gpuRgb = GpuRgbLocator.Scan(_log);
            }
            catch (Exception ex)
            {
                warnings.Add($"GPU RGB scan failed: {ex.Message}");
            }

            if (LhmFanController.IsElevated)
            {
                try
                {
                    _ramScanner = new EneRamScanner(_log);
                    RamInventory = _ramScanner.ReadSticks();
                    _ramRgb = _ramScanner.FindRgbControllers();
                    _log?.Invoke($"control: {RamInventory.Count} DIMM(s), {_ramRgb.Count} ENE RAM RGB controller(s)");
                }
                catch (Exception ex)
                {
                    warnings.Add($"RAM RGB scan failed: {ex.Message}");
                }
            }
        }

        if (_headers is null)
        {
            if (!LhmFanController.IsElevated)
            {
                if (!_headersUnavailableWarned)
                {
                    _headersUnavailableWarned = true;
                    _log?.Invoke("motherboard/GPU fan control skipped — not running as administrator");
                }
            }
            else
            {
                try
                {
                    _headers = new LhmFanController();
                    _lastWrittenHeaderDuty = -1;
                    _log?.Invoke($"control: opened SuperIO/GPU fan controls ({_headers.MotherboardName})");
                }
                catch (Exception ex)
                {
                    if (!_headersUnavailableWarned)
                    {
                        _headersUnavailableWarned = true;
                        warnings.Add($"motherboard/GPU fan control unavailable: {ex.Message}");
                    }
                }
            }
        }
    }

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
                // hub also recovers on power cycle
            }

            hub.Dispose();
        }

        _hubs.Clear();

        lock (_slv3Gate)
        {
            _slv3?.Dispose(); // fans revert to firmware defaults once keepalive stops
            _slv3 = null;
        }

        _aura?.Dispose(); // committed colors persist in the controller on their own
        _aura = null;

        _ramScanner?.Dispose(); // ENE colors are saved to the controllers' flash
        _ramScanner = null;
        _ramRgb = [];
        _gpuRgb = [];
        _chipRgbScanned = false;

        _corsairLcd?.Dispose(); // panels keep showing their last frame on their own
        _corsairLcd = null;
        foreach (var node in _lcdNodes)
        {
            node.Dispose();
        }

        _lcdNodes.Clear();
        _appliedLcd.Clear();

        _headers?.Dispose(); // restores BIOS/driver-automatic control on every touched header
        _headers = null;

        _lhm?.Dispose();
        _lhm = null;
        _lastWrittenCorsairDuty = -1;
        _lastWrittenHeaderDuty = -1;
    }

    // ---- RGB (static color, both families) ----

    private readonly Dictionary<string, string> _appliedHubRgb = [];
    private readonly Dictionary<string, long> _hubRgbRetryAt = [];
    private readonly Dictionary<string, string> _appliedSlv3Rgb = [];
    private string? _appliedAuraRgb;
    private string? _appliedChipRgb;
    private long _chipRgbRetryAt;
    private long _slv3RgbRefreshDue;
    private int _slv3ConfirmSendsLeft;

    // flash persistence is slow and endurance-limited: colors apply instantly (volatile),
    // and only a color that has stayed put this long gets written to controller flash
    private const int RgbPersistSettleMs = 10_000;
    private long _rgbPersistDue;
    private string? _rgbPersistArmedFor;
    private string? _persistedRgb;

    // NOTE: an effect-index watchdog (compare telemetry's EffectIndex against the uploaded
    // one, re-send on mismatch) was tried in v17 and REMOVED in v20: at least one group's
    // firmware reports a slot counter rather than echoing the index, which put the watchdog
    // into a permanent ~8 s re-send loop that flooded the RF network and knocked whole
    // groups out of telemetry. Rainbow fallbacks heal via the 60 s refresh instead.

    private void ApplyRgb(ControlSettings settings, List<string> warnings)
    {
        // "Off" is an active color: every surface gets black (and persists it), because
        // several devices (SL V3 firmware, hub hardware mode) fall back to their own rainbow
        // effects the moment we merely stop writing
        var (r, g, b) = settings.RgbOff
            ? ((byte)0, (byte)0, (byte)0)
            : ((byte)Math.Clamp(settings.RgbR, 0, 255),
               (byte)Math.Clamp(settings.RgbG, 0, 255),
               (byte)Math.Clamp(settings.RgbB, 0, 255));
        var key = $"{r},{g},{b}";

        foreach (var hub in _hubs.ToList())
        {
            if (_appliedHubRgb.GetValueOrDefault(hub.SerialNumber) == key)
            {
                continue;
            }

            if (Environment.TickCount64 < _hubRgbRetryAt.GetValueOrDefault(hub.SerialNumber))
            {
                warnings.Add($"RGB on hub {hub.SerialNumber[..8]}… pending retry");
                continue;
            }

            try
            {
                hub.ApplyStaticColor(r, g, b);
                _appliedHubRgb[hub.SerialNumber] = key;
                _hubRgbRetryAt.Remove(hub.SerialNumber);
                _log?.Invoke($"RGB applied on hub {hub.SerialNumber[..8]}…: {hub.TotalLeds} LEDs "
                    + $"({string.Join(", ", hub.LedCounts.Select(kv => $"ch{kv.Key}={kv.Value}"))})");
            }
            catch (Exception ex)
            {
                _hubRgbRetryAt[hub.SerialNumber] = Environment.TickCount64 + 10_000;
                warnings.Add($"RGB on hub {hub.SerialNumber[..8]}… failed: {ex.Message}");

                // also into the log — color failures used to be invisible post-mortem
                _log?.Invoke($"RGB on hub {hub.SerialNumber[..8]}… failed: {ex.Message} — retrying in 10 s with a rebuilt color path");
            }
        }

        if (_aura is not null && _appliedAuraRgb != key)
        {
            try
            {
                _aura.ApplyStaticColor(r, g, b);
                _appliedAuraRgb = key;
                _log?.Invoke($"RGB applied on Aura ({_aura.Zones.Count} zones)");
            }
            catch (Exception ex)
            {
                warnings.Add($"RGB on Aura failed: {ex.Message}");
                _aura.Dispose();
                _aura = null; // reopen next tick
            }
        }

        if (_appliedChipRgb != key
            && Environment.TickCount64 >= _chipRgbRetryAt
            && (_ramRgb.Count > 0 || _gpuRgb.Any(g2 => g2.Ene is not null)))
        {
            try
            {
                foreach (var ram in _ramRgb)
                {
                    ram.ApplyStaticColor(r, g, b, persist: false);
                }

                foreach (var gpu in _gpuRgb)
                {
                    gpu.Ene?.ApplyStaticColor(r, g, b, persist: false);
                }

                _appliedChipRgb = key;
                _log?.Invoke($"RGB applied on {_ramRgb.Count} RAM controller(s) + {_gpuRgb.Count(g2 => g2.Ene is not null)} GPU(s)");
            }
            catch (Exception ex)
            {
                _chipRgbRetryAt = Environment.TickCount64 + 30_000;
                warnings.Add($"RAM/GPU RGB failed: {ex.Message} (will retry)");
            }
        }

        // once every surface carries the current color and it has settled, persist it so it
        // survives power cycles (Aura commit + ENE flash saves — one write per color, total)
        if (_persistedRgb != key && _appliedChipRgb == key && (_aura is null || _appliedAuraRgb == key))
        {
            if (_rgbPersistArmedFor != key)
            {
                _rgbPersistArmedFor = key; // a color change restarts the settle window
                _rgbPersistDue = Environment.TickCount64 + RgbPersistSettleMs;
            }
            else if (Environment.TickCount64 >= _rgbPersistDue)
            {
                try
                {
                    _aura?.Commit();
                    foreach (var ram in _ramRgb)
                    {
                        ram.Persist();
                    }

                    foreach (var gpu in _gpuRgb)
                    {
                        gpu.Ene?.Persist();
                    }

                    // SL V3 fans: persist the stored lighting effect too, so keepalive gaps
                    // (app updates, reboots) fall back to this color instead of rainbow
                    lock (_slv3Gate)
                    {
                        if (_slv3 is not null && !_slv3Dead)
                        {
                            _slv3.SaveConfig();
                        }
                    }

                    _persistedRgb = key;
                    _log?.Invoke("RGB persisted to controller flash (survives power cycles)");
                }
                catch (Exception ex)
                {
                    warnings.Add($"RGB persist failed: {ex.Message}");
                }
            }
        }

        lock (_slv3Gate)
        {
            if (_slv3 is null || _slv3Dead)
            {
                return;
            }

            // wireless effects live in fan firmware; refresh periodically in case a group resets
            var refreshDue = Environment.TickCount64 >= _slv3RgbRefreshDue;
            var sentAny = false;
            foreach (var device in _slv3.Devices.Where(d => d.IsBoundTo(_slv3.MasterMac)))
            {
                if (!refreshDue && _appliedSlv3Rgb.GetValueOrDefault(device.MacText) == key)
                {
                    continue;
                }

                try
                {
                    var ticks = Environment.TickCount64;
                    var effectIndex = new[] { (byte)(ticks >> 24), (byte)(ticks >> 16), (byte)(ticks >> 8), (byte)ticks };
                    _slv3.ApplyStaticColor(device, r, g, b, effectIndex);
                    _appliedSlv3Rgb[device.MacText] = key;
                    sentAny = true;
                }
                catch (Exception ex)
                {
                    warnings.Add($"RGB write to SL V3 {device.MacText[..4]} failed: {ex.Message}");
                }
            }

            // The RF uplink is fire-and-forget and drops roughly one group per few changes —
            // a group that missed chunks shows its rainbow fallback. Every fresh upload is
            // followed by TWO confirmation re-sends ~1.5 s apart (a fixed, finite burst —
            // never a continuous re-send loop: that flooded the RF network in v17).
            if (sentAny && !refreshDue)
            {
                _slv3ConfirmSendsLeft = 2;
                _slv3RgbRefreshDue = Math.Min(_slv3RgbRefreshDue, Environment.TickCount64 + 1_500);
            }
            else if (refreshDue)
            {
                if (_slv3ConfirmSendsLeft > 0)
                {
                    _slv3ConfirmSendsLeft--;
                    _slv3RgbRefreshDue = Environment.TickCount64 + 1_500;
                }
                else
                {
                    // 20 s steady-state refresh: one group has demonstrably weak RF reception
                    // and drops its stored effect — bound-time-to-heal beats airtime here
                    // (still nowhere near the v17 continuous-resend flood)
                    _slv3RgbRefreshDue = Environment.TickCount64 + 20_000;
                }
            }
        }
    }

    // ---- LCD screens (pump LCD + SL V3 per-fan LCDs) ----

    private CorsairLcdDevice? _corsairLcd;
    private readonly List<Slv3LcdNode> _lcdNodes = [];
    private readonly Dictionary<string, LcdMode> _appliedLcd = [];
    private long _lcdRetryAt;
    private bool _lcdOpenFailedWarned;

    /// <summary>Screens currently open (id, isPump) — for the UI's per-screen editors.</summary>
    public IReadOnlyList<(string Id, bool IsPump)> LcdScreenIds
    {
        get
        {
            var list = new List<(string, bool)>();
            if (_corsairLcd is not null)
            {
                list.Add(("pump-lcd", true));
            }

            list.AddRange(_lcdNodes.Select(n => (n.Serial, false)));
            return list;
        }
    }

    // "find this screen": flash a big index frame on one screen for a few seconds
    private readonly Dictionary<string, long> _lcdIdentifyUntil = [];

    public void IdentifyScreen(string id, int seconds = 8)
    {
        lock (_lcdIdentifyUntil)
        {
            _lcdIdentifyUntil[id] = Environment.TickCount64 + seconds * 1000L;
        }

        _lcdShownKey.Remove(id); // force an immediate re-push on the next metric round

        _lcdMetricsDue = 0;
        _wake.Set();
    }

    private bool IsIdentifying(string id)
    {
        lock (_lcdIdentifyUntil)
        {
            if (_lcdIdentifyUntil.TryGetValue(id, out var until))
            {
                if (Environment.TickCount64 <= until)
                {
                    return true;
                }

                _lcdIdentifyUntil.Remove(id);
                _lcdShownKey.Remove(id); // restore the metric right away
        
            }

            return false;
        }
    }

    private void ApplyLcd(ControlSettings settings, List<DeviceReading> readings, int duty, List<string> warnings)
    {
        var mode = settings.LcdScreens;
        if (mode == LcdMode.Unmanaged || Environment.TickCount64 < _lcdRetryAt)
        {
            return;
        }

        // open screens lazily, only once the user actually manages them
        if (_corsairLcd is null && CorsairLcdDevice.FindDevices().FirstOrDefault() is { } lcdHid)
        {
            try
            {
                _corsairLcd = CorsairLcdDevice.Open(lcdHid);
                _appliedLcd.Remove("pump-lcd");
                _log?.Invoke($"control: opened the pump LCD (serial {_corsairLcd.SerialNumber})");
            }
            catch (Exception ex)
            {
                if (!_lcdOpenFailedWarned)
                {
                    _lcdOpenFailedWarned = true;
                    _log?.Invoke($"pump LCD open failed: {ex.Message}");
                }

                warnings.Add($"pump LCD open failed: {ex.Message}");
            }
        }

        if (_lcdNodes.Count == 0)
        {
            foreach (var (path, serial) in Slv3LcdNode.FindNodes())
            {
                try
                {
                    _lcdNodes.Add(Slv3LcdNode.Open(path, serial));
                }
                catch (Exception ex)
                {
                    warnings.Add($"fan LCD {serial[..Math.Min(6, serial.Length)]} open failed: {ex.Message}");
                }
            }

            if (_lcdNodes.Count > 0)
            {
                _log?.Invoke($"control: opened {_lcdNodes.Count} fan LCD node(s)");
            }
        }

        if (_corsairLcd is { } pumpLcd && _appliedLcd.GetValueOrDefault("pump-lcd") != mode)
        {
            try
            {
                ApplyLcdMode(mode,
                    jpeg => pumpLcd.SendJpegFrame(jpeg),
                    percent => pumpLcd.SetBrightness(percent),
                    Devices.CorsairLink.Protocol.CorsairLcdPackets.ScreenWidth,
                    Devices.CorsairLink.Protocol.CorsairLcdPackets.ScreenHeight);
                _appliedLcd["pump-lcd"] = mode;
                _log?.Invoke($"pump LCD set to {mode}");
            }
            catch (Exception ex)
            {
                warnings.Add($"pump LCD write failed: {ex.Message} — reopening");
                _log?.Invoke($"pump LCD write failed: {ex.Message} — reopening");
                _corsairLcd.Dispose();
                _corsairLcd = null;
                _lcdRetryAt = Environment.TickCount64 + 10_000;
            }
        }

        var applied = 0;
        foreach (var node in _lcdNodes.ToList())
        {
            if (_appliedLcd.GetValueOrDefault(node.Serial) == mode)
            {
                continue;
            }

            try
            {
                ApplyLcdMode(mode,
                    node.SendJpegFrame,
                    node.SetBrightness,
                    Slv3LcdProtocol.ScreenWidth,
                    Slv3LcdProtocol.ScreenHeight);
                _appliedLcd[node.Serial] = mode;
                applied++;
            }
            catch (Exception ex)
            {
                warnings.Add($"fan LCD {node.Serial[..Math.Min(6, node.Serial.Length)]} write failed: {ex.Message} — reopening");
                node.Dispose();
                _lcdNodes.Remove(node);
                _lcdRetryAt = Environment.TickCount64 + 10_000;
            }
        }

        if (applied > 0)
        {
            _log?.Invoke($"{applied} fan LCD(s) set to {mode}");

            // the LCD node lives inside the fan — LCD commands reset the fan's stored RGB
            // effect AND leave its radio deaf for several seconds (observed live twice on the
            // weakest group). Re-push colors starting 3 s after the burst, with extra
            // confirmations spanning the deaf window.
            _slv3RgbRefreshDue = Math.Min(_slv3RgbRefreshDue, Environment.TickCount64 + 3_000);
            _slv3ConfirmSendsLeft = Math.Max(_slv3ConfirmSendsLeft, 3);
        }

        // the pump panel reasserts its own liquid-temp screen ~30 s after frames stop —
        // keep the solid background alive with a periodic re-send (HID only, no RF impact)
        if (_corsairLcd is { } keepalivePump && mode is LcdMode.Black or LcdMode.White
            && Environment.TickCount64 >= _pumpLcdKeepaliveDue)
        {
            _pumpLcdKeepaliveDue = Environment.TickCount64 + PumpLcdKeepaliveMs;
            try
            {
                var (r, g, b) = mode == LcdMode.Black ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255);
                keepalivePump.SendJpegFrame(LcdFrames.Solid(
                    Devices.CorsairLink.Protocol.CorsairLcdPackets.ScreenWidth,
                    Devices.CorsairLink.Protocol.CorsairLcdPackets.ScreenHeight, r, g, b));
            }
            catch (Exception ex)
            {
                warnings.Add($"pump LCD keepalive failed: {ex.Message} — reopening");
                _corsairLcd.Dispose();
                _corsairLcd = null;
                _lcdRetryAt = Environment.TickCount64 + 10_000;
            }
        }

        if (mode == LcdMode.Metrics)
        {
            StreamLcdMetrics(settings, readings, duty, warnings);
        }
    }

    private static void ApplyLcdMode(LcdMode mode, Action<byte[]> sendFrame, Action<int> setBrightness, int width, int height)
    {
        switch (mode)
        {
            case LcdMode.Off:
                setBrightness(0);
                break;
            case LcdMode.Black:
                sendFrame(LcdFrames.Solid(width, height, 0, 0, 0));
                setBrightness(100);
                break;
            case LcdMode.White:
                sendFrame(LcdFrames.Solid(width, height, 255, 255, 255));
                setBrightness(100);
                break;
            case LcdMode.Metrics:
                setBrightness(100); // frames come from the metric streamer
                break;
        }
    }

    // ---- Turzx 8.8" serial screen ----
    // The serial port is owned exclusively by the Turzx worker thread (RunTurzx). The tick
    // thread only renders the desired frame and publishes desired state under _turzxGate; it
    // never touches the port, so a multi-second full-frame push can't stall fan/pump control.
    private const int TurzxLandscapeWidth = 1920;
    private const int TurzxLandscapeHeight = 480;

    private Thread? _turzxThread;
    private readonly AutoResetEvent _turzxWake = new(false);
    private readonly object _turzxGate = new();
    private LcdMode _turzxMode = LcdMode.Unmanaged;
    private int _turzxBrightness = 100;
    private bool _turzxFlip;
    private byte[]? _turzxFrame;
    private string? _turzxFrameKey;
    private long _turzxMetricsDue;
    private volatile string? _turzxStatusText;

    // worker-thread-only state
    private TurzxScreen? _turzx;
    private int _turzxAppliedBrightness = -1;
    private string? _turzxShownKey;
    private long _turzxRetryAt;

    /// <summary>Tick-thread half: render the desired Turzx frame (fast, cached) and publish desired state.</summary>
    private void ApplyTurzx(ControlSettings settings, List<DeviceReading> readings, int duty)
    {
        byte[]? frame = null;
        string? key = null;
        if (settings.TurzxScreen == LcdMode.Metrics && Environment.TickCount64 >= _turzxMetricsDue)
        {
            _turzxMetricsDue = Environment.TickCount64 + 2_000;
            IReadOnlyList<Core.Sensors.SensorReading>? lhm = null;
            if (ReadLcdMetric(settings.TurzxMetric, readings, duty, ref lhm) is { } m)
            {
                key = $"{settings.TurzxMetric}|{m.Value}|{m.Unit}|{m.Accent}";
                frame = LcdMetricRenderer.Render(TurzxLandscapeWidth, TurzxLandscapeHeight, m.Label, m.Value, m.Unit, m.Accent);
            }
        }

        lock (_turzxGate)
        {
            _turzxMode = settings.TurzxScreen;
            _turzxBrightness = Math.Clamp(settings.TurzxBrightness, 0, 100);
            _turzxFlip = settings.TurzxRotation == 180;
            if (frame is not null)
            {
                _turzxFrame = frame;
                _turzxFrameKey = key;
            }
        }

        _turzxWake.Set();
    }

    /// <summary>Worker-thread half: owns the serial port, applies brightness/mode, pushes frames on change.</summary>
    private void RunTurzx(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _turzxWake.WaitOne(1000);
            if (token.IsCancellationRequested)
            {
                break;
            }

            LcdMode mode;
            int brightness;
            bool flip;
            byte[]? frame;
            string? key;
            lock (_turzxGate)
            {
                mode = _turzxMode;
                brightness = _turzxBrightness;
                flip = _turzxFlip;
                frame = _turzxFrame;
                key = _turzxFrameKey;
            }

            try
            {
                if (mode == LcdMode.Unmanaged)
                {
                    if (_turzx is not null)
                    {
                        _turzx.Dispose(); // panel keeps showing its last frame on its own
                        _turzx = null;
                        _turzxShownKey = null;
                        _turzxAppliedBrightness = -1;
                        _turzxStatusText = null;
                    }

                    continue;
                }

                if (Environment.TickCount64 < _turzxRetryAt)
                {
                    continue;
                }

                if (_turzx is null)
                {
                    var target = TurzxScreen.Find().FirstOrDefault();
                    if (target.ComPort is null)
                    {
                        _turzxStatusText = "Not detected";
                        _turzxRetryAt = Environment.TickCount64 + 5_000;
                        continue;
                    }

                    _turzx = TurzxScreen.Open(target.ComPort, target.Serial);
                    _turzxShownKey = null;
                    _turzxAppliedBrightness = -1;
                    _log?.Invoke($"control: opened the Turzx screen ({_turzx.ComPort})");
                }

                _turzx.FlipLandscape = flip;

                var wantBrightness = mode == LcdMode.Off ? 0 : brightness;
                if (_turzxAppliedBrightness != wantBrightness)
                {
                    _turzx.SetBrightness(wantBrightness);
                    _turzxAppliedBrightness = wantBrightness;
                }

                switch (mode)
                {
                    case LcdMode.Off:
                        break; // backlight already driven to 0 above
                    case LcdMode.Black:
                        if (_turzxShownKey != "black")
                        {
                            _turzx.SendJpegFrame(LcdFrames.Solid(TurzxLandscapeWidth, TurzxLandscapeHeight, 0, 0, 0));
                            _turzxShownKey = "black";
                        }

                        break;
                    case LcdMode.White:
                        if (_turzxShownKey != "white")
                        {
                            _turzx.SendJpegFrame(LcdFrames.Solid(TurzxLandscapeWidth, TurzxLandscapeHeight, 255, 255, 255));
                            _turzxShownKey = "white";
                        }

                        break;
                    case LcdMode.Metrics:
                        if (frame is not null && _turzxShownKey != key)
                        {
                            _turzx.SendJpegFrame(frame);
                            _turzxShownKey = key;
                        }

                        break;
                }

                _turzxStatusText = $"Connected · {_turzx.ComPort}"
                    + (_turzx.RomVersion is { } rom ? $" · ROM {rom}" : "");
            }
            catch (TurzxUnsupportedException ex)
            {
                // the panel is present but not a Turing-protocol device — stop hammering it
                _turzxStatusText = $"Found on {_turzx?.ComPort ?? "the port"} but not responding to the Turing protocol "
                    + "— likely a newer 8.8\" revision. Screen control is unavailable on this panel.";
                _log?.Invoke($"Turzx: {ex.Message}");
                _turzx?.Dispose();
                _turzx = null;
                _turzxShownKey = null;
                _turzxAppliedBrightness = -1;
                _turzxRetryAt = Environment.TickCount64 + 300_000; // 5 min: no protocol yet, don't re-stall the port
            }
            catch (Exception ex)
            {
                _turzxStatusText = $"Error: {ex.Message}";
                _log?.Invoke($"Turzx screen write failed: {ex.Message} — reopening");
                _turzx?.Dispose();
                _turzx = null;
                _turzxShownKey = null;
                _turzxAppliedBrightness = -1;
                _turzxRetryAt = Environment.TickCount64 + 10_000;
            }
        }

        try { _turzx?.Dispose(); } catch { /* shutting down */ }
        _turzx = null;
    }

    // The pump panel's firmware reasserts its own liquid-temp screen ~30 s after frames stop
    // (observed live), so it needs a periodic frame keepalive. The SL V3 fan panels hold
    // their frame indefinitely — and their radios dislike LCD traffic — so they stay
    // change-only.
    private const int PumpLcdKeepaliveMs = 10_000;
    private long _pumpLcdKeepaliveDue;
    private long _lcdMetricsDue;
    private readonly Dictionary<string, string> _lcdShownKey = [];
    private long _fanLcdBatchDue;

    private void StreamLcdMetrics(ControlSettings settings, List<DeviceReading> readings, int duty, List<string> warnings)
    {
        if (Environment.TickCount64 < _lcdMetricsDue)
        {
            return;
        }

        _lcdMetricsDue = Environment.TickCount64 + 2_000;
        IReadOnlyList<Core.Sensors.SensorReading>? lhmReadings = null;

        if (_corsairLcd is { } pump)
        {
            var config = settings.ScreenConfig("pump-lcd", isPump: true);
            var frame = ComposeScreenFrame("pump-lcd", ordinal: 0, config,
                Devices.CorsairLink.Protocol.CorsairLcdPackets.ScreenWidth,
                Devices.CorsairLink.Protocol.CorsairLcdPackets.ScreenHeight,
                readings, duty, ref lhmReadings, out var key);
            var keepaliveDue = Environment.TickCount64 >= _pumpLcdKeepaliveDue;
            if (frame is not null && (_lcdShownKey.GetValueOrDefault("pump-lcd") != key || keepaliveDue))
            {
                try
                {
                    pump.SendJpegFrame(frame);
                    _lcdShownKey["pump-lcd"] = key!;
                    _pumpLcdKeepaliveDue = Environment.TickCount64 + PumpLcdKeepaliveMs;
                }
                catch (Exception ex)
                {
                    warnings.Add($"pump LCD write failed: {ex.Message} — reopening");
                    _corsairLcd.Dispose();
                    _corsairLcd = null;
                    _lcdRetryAt = Environment.TickCount64 + 10_000;
                }
            }
        }

        // Fan screens update as ONE batch on a 15 s cadence, not staggered per fan: staggered
        // pushes kept some fan radio deaf at any given moment, so colors dropped to rainbow
        // "randomly". A batch leaves quiet air between bursts, and every burst is followed by
        // color confirms. Identify pushes (and screens with no frame yet) skip the wait.
        var batchDue = Environment.TickCount64 >= _fanLcdBatchDue;
        var pushedAny = false;
        var ordinal = 0;
        foreach (var node in _lcdNodes.ToList())
        {
            ordinal++;
            var urgent = IsIdentifying(node.Serial) || !_lcdShownKey.ContainsKey(node.Serial);
            if (!batchDue && !urgent)
            {
                continue;
            }

            var config = settings.ScreenConfig(node.Serial, isPump: false);
            var frame = ComposeScreenFrame(node.Serial, ordinal, config,
                Slv3LcdProtocol.ScreenWidth, Slv3LcdProtocol.ScreenHeight,
                readings, duty, ref lhmReadings, out var key);
            if (frame is null || _lcdShownKey.GetValueOrDefault(node.Serial) == key)
            {
                continue;
            }

            try
            {
                node.SendJpegFrame(frame);
                _lcdShownKey[node.Serial] = key!;
                pushedAny = true;
            }
            catch (Exception ex)
            {
                warnings.Add($"fan LCD {node.Serial[..Math.Min(6, node.Serial.Length)]} write failed: {ex.Message} — reopening");
                node.Dispose();
                _lcdNodes.Remove(node);
                _lcdRetryAt = Environment.TickCount64 + 10_000;
            }
        }

        if (batchDue)
        {
            _fanLcdBatchDue = Environment.TickCount64 + 15_000;
        }

        if (pushedAny)
        {
            // fan radios go briefly deaf around LCD traffic — follow every burst with confirms
            _slv3RgbRefreshDue = Math.Min(_slv3RgbRefreshDue, Environment.TickCount64 + 3_000);
            _slv3ConfirmSendsLeft = Math.Max(_slv3ConfirmSendsLeft, 2);
        }
    }

    /// <summary>One screen's frame (metric or identify flash) and its content key.</summary>
    private byte[]? ComposeScreenFrame(
        string id, int ordinal, LcdScreenConfig config, int width, int height,
        List<DeviceReading> readings, int duty,
        ref IReadOnlyList<Core.Sensors.SensorReading>? lhmReadings, out string? key)
    {
        if (IsIdentifying(id))
        {
            // unmissable: black number on a solid orange screen
            var name = ordinal == 0 ? "P" : ordinal.ToString();
            key = $"identify|{id}";
            return LcdMetricRenderer.Render(width, height,
                ordinal == 0 ? "PUMP SCREEN" : "FAN SCREEN",
                name, id.Length > 6 ? id[..6] : id,
                accent: (0, 0, 0), config.RotationDegrees, background: (255, 150, 0));
        }

        if (ReadLcdMetric(config.Metric, readings, duty, ref lhmReadings) is not { } m)
        {
            key = null;
            return null;
        }

        var accent = config.ColorByValue
            ? m.Accent // green/amber/red by thresholds
            : config is { FontR: { } fr, FontG: { } fg, FontB: { } fb }
                ? ((byte)Math.Clamp(fr, 0, 255), (byte)Math.Clamp(fg, 0, 255), (byte)Math.Clamp(fb, 0, 255))
                : ((byte)235, (byte)235, (byte)245); // default: plain white
        key = $"{config.Metric}|{m.Value}|{m.Unit}|{accent}|{config.RotationDegrees}";
        return LcdMetricRenderer.Render(width, height, m.Label, m.Value, m.Unit, accent, config.RotationDegrees);
    }

    private (string Label, string Value, string Unit, (byte R, byte G, byte B) Accent)? ReadLcdMetric(
        LcdMetric metric, List<DeviceReading> readings, int duty,
        ref IReadOnlyList<Core.Sensors.SensorReading>? lhmReadings)
    {
        switch (metric)
        {
            case LcdMetric.Clock:
                var now = DateTime.Now;
                return ("TIME", now.ToString("HH:mm"), now.ToString("ddd d MMM"), (235, 235, 245));

            case LcdMetric.Date:
                var today = DateTime.Now;
                return (today.ToString("dddd").ToUpperInvariant(), today.Day.ToString(), today.ToString("MMMM yyyy"), (235, 235, 245));

            case LcdMetric.Coolant:
                return TryReadCoolant() is { } coolant
                    ? ("COOLANT", $"{coolant:F0}", "°C", TemperatureAccent(coolant, warm: 33, hot: 37))
                    : null;

            case LcdMetric.PumpRpm:
                return readings.FirstOrDefault(r => r.IsPump) is { Rpm: { } rpm }
                    ? ("PUMP", rpm.ToString(), "rpm", (122, 167, 255))
                    : null;

            case LcdMetric.PumpDuty:
                return readings.FirstOrDefault(r => r.IsPump) is { } pumpReading
                    ? ("PUMP", pumpReading.AppliedDutyPercent.ToString(), "%", (122, 167, 255))
                    : null;

            case LcdMetric.FanDuty:
                return ("FANS", duty.ToString(), "%", (122, 167, 255));

            case LcdMetric.FanRpm:
                var rpms = readings.Where(r => !r.IsPump && r.Rpm is > 0).Select(r => r.Rpm!.Value).ToList();
                return rpms.Count > 0
                    ? ("FANS", ((int)rpms.Average()).ToString(), "rpm", (122, 167, 255))
                    : null;

            case LcdMetric.CpuTemp:
                return LhmLcdMetric(ref lhmReadings, "cpu", SensorKind.Temperature, "CPU", "°C");
            case LcdMetric.GpuTemp:
                return LhmLcdMetric(ref lhmReadings, "gpu", SensorKind.Temperature, "GPU", "°C");
            case LcdMetric.CpuLoad:
                return LhmLcdMetric(ref lhmReadings, "cpu", SensorKind.Load, "CPU", "%", preferName: "total");
            case LcdMetric.GpuLoad:
                return LhmLcdMetric(ref lhmReadings, "gpu", SensorKind.Load, "GPU", "%", preferName: "core");
            case LcdMetric.RamLoad:
                return LhmLcdMetric(ref lhmReadings, "ram", SensorKind.Load, "RAM", "%");
            case LcdMetric.VramLoad:
                return LhmLcdMetric(ref lhmReadings, "gpu", SensorKind.Load, "VRAM", "%",
                    preferName: "memory", excludeName: "controller");
            default:
                return null;
        }
    }

    private (string, string, string, (byte, byte, byte))? LhmLcdMetric(
        ref IReadOnlyList<Core.Sensors.SensorReading>? lhmReadings,
        string hardware, SensorKind kind, string label, string unit,
        string? preferName = null, string? excludeName = null)
    {
        try
        {
            _lhm ??= new LhmSensorSource();
            lhmReadings ??= _lhm.Read();
            var candidates = lhmReadings
                .Where(r => r.Kind == kind && r.Id.Contains(hardware, StringComparison.OrdinalIgnoreCase))
                .Where(r => excludeName is null || !r.Name.Contains(excludeName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // prefer the canonical sensor ("CPU Total", "GPU Core", "GPU Memory") over the
            // max of every load sensor the hardware happens to expose
            var preferred = preferName is null
                ? []
                : candidates.Where(r => r.Name.Contains(preferName, StringComparison.OrdinalIgnoreCase)).ToList();
            var value = (preferred.Count > 0 ? preferred : candidates)
                .Select(r => (double?)r.Value)
                .Max();
            if (value is not { } v)
            {
                return null;
            }

            var accent = kind == SensorKind.Temperature
                ? TemperatureAccent(v, warm: 60, hot: 80)
                : ((byte)122, (byte)167, (byte)255);
            return (label, $"{v:F0}", unit, accent);
        }
        catch
        {
            return null;
        }
    }

    private static (byte, byte, byte) TemperatureAccent(double value, double warm, double hot) =>
        value >= hot ? ((byte)248, (byte)113, (byte)113)
        : value >= warm ? ((byte)251, (byte)191, (byte)36)
        : ((byte)74, (byte)222, (byte)128);

    private double? TryReadCoolant()
    {
        foreach (var hub in _hubs)
        {
            try
            {
                var temp = hub.ReadTemperatures().FirstOrDefault(t => t.TemperatureCelsius is not null);
                if (temp?.TemperatureCelsius is { } coolant)
                {
                    return coolant;
                }
            }
            catch
            {
                // informational only — the source path reports its own errors
            }
        }

        return null;
    }

    // ---- temperature sources ----

    private double? ReadSourceTemperature(CurveSource source, List<string> warnings)
    {
        if (source == CurveSource.Coolant)
        {
            foreach (var hub in _hubs)
            {
                try
                {
                    var temp = hub.ReadTemperatures().FirstOrDefault(t => t.TemperatureCelsius is not null);
                    if (temp?.TemperatureCelsius is { } coolant)
                    {
                        return coolant;
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"coolant read failed: {ex.Message}");
                }
            }

            return null;
        }

        try
        {
            _lhm ??= new LhmSensorSource();
            var readings = _lhm.Read();
            var wanted = source == CurveSource.Cpu ? "cpu" : "gpu";
            return readings
                .Where(r => r.Kind == SensorKind.Temperature && r.Id.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                .Select(r => (double?)r.Value)
                .Max();
        }
        catch (Exception ex)
        {
            warnings.Add($"{source} read failed: {ex.Message}");
            return null;
        }
    }

    // ---- appliers ----

    private bool _pulseWasActive;

    // per-hub fingerprint of the channels that currently report a tach, for reseat detection
    private readonly Dictionary<string, string> _hubTachSignatures = [];

    private void ApplyCorsair(int duty, int pumpDuty, List<DeviceReading> readings, List<string> warnings)
    {
        var pulseActive = AnyPulsePending();
        var writeKey = duty * 1000 + pumpDuty;
        var mustWrite = writeKey != _lastWrittenCorsairDuty
            || ++_ticksSinceCorsairWrite >= CorsairRefreshTicks
            || pulseActive
            || _pulseWasActive; // one extra write to restore duties after a pulse ends
        _pulseWasActive = pulseActive;

        var rescanDue = ++_ticksSinceCorsairRescan >= CorsairRescanTicks;
        if (rescanDue)
        {
            _ticksSinceCorsairRescan = 0;
        }

        foreach (var hub in _hubs.ToList())
        {
            try
            {
                if (!hub.InSoftwareMode)
                {
                    hub.EnterSoftwareMode();
                }

                if (rescanDue)
                {
                    // fans renegotiate the chain when stacks are reseated or power-blip —
                    // a stale map sends colors and reads RPMs on the wrong channels
                    var before = hub.ChannelSignature();
                    hub.EnumerateChannels(allowEnterSoftwareMode: false);
                    if (before != hub.ChannelSignature())
                    {
                        _appliedHubRgb.Remove(hub.SerialNumber);
                        _lastWrittenCorsairDuty = -1;
                        _log?.Invoke($"Link chain changed on hub {hub.SerialNumber[..8]}… — re-applying duties and colors");
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
                        _log?.Invoke($"hub {hub.SerialNumber[..8]}… LED registry sync skipped: {ex.Message}");
                    }
                }

                if (mustWrite)
                {
                    var requests = hub.Channels.Where(c => !c.IsPump).ToDictionary(
                        c => c.Channel,
                        c => TryGetPulse(hub.SerialNumber, c.Channel) ? 100 : duty);
                    hub.WriteFixedDuties(requests, pumpDuty);
                }

                var speeds = hub.ReadSpeeds().Where(s => s.Rpm is not null).ToDictionary(s => s.Channel);

                // degraded-link tracking: a fan that enumerates but reports no tach also ignores
                // LED data on this hardware (verified with `link ledtest`). When a channel's tach
                // comes alive mid-session — the owner reseated a junction — pulse LED power so the
                // hub re-registers the fan, and re-send colors immediately.
                var fanChannels = hub.Channels
                    .Where(c => !c.IsPump && c.Info?.Flags.HasFlag(LinkDeviceFlags.ControlsSpeed) == true)
                    .ToList();
                var degraded = fanChannels.Where(c => !speeds.ContainsKey(c.Channel))
                    .Select(c => c.Channel).OrderBy(c => c).ToList();
                var responsive = fanChannels.Where(c => speeds.ContainsKey(c.Channel))
                    .Select(c => c.Channel).OrderBy(c => c).ToList();
                var tachSignature = string.Join(",", responsive);
                if (_hubTachSignatures.TryGetValue(hub.SerialNumber, out var previousTach)
                    && previousTach != tachSignature)
                {
                    // pulse only on GAINED channels (a reseat): pulsing visibly blinks the whole
                    // chain, so a transient tach dropout must not trigger it
                    HashSet<int> previousSet = previousTach.Length == 0
                        ? []
                        : previousTach.Split(',').Select(int.Parse).ToHashSet();
                    var gained = responsive.Where(c => !previousSet.Contains(c)).ToList();
                    _log?.Invoke($"hub {hub.SerialNumber[..8]}… responsive fan channels changed: "
                        + $"[{previousTach}] → [{tachSignature}]");
                    if (gained.Count > 0)
                    {
                        _log?.Invoke($"hub {hub.SerialNumber[..8]}… ch{string.Join(", ch", gained)} came alive "
                            + "— pulsing LED power and re-applying colors");
                        try
                        {
                            hub.PulseLedPower();
                        }
                        catch (LinkHubException ex)
                        {
                            _log?.Invoke($"hub {hub.SerialNumber[..8]}… LED power pulse failed: {ex.Message}");
                        }

                        _appliedHubRgb.Remove(hub.SerialNumber);
                    }
                }

                _hubTachSignatures[hub.SerialNumber] = tachSignature;
                if (degraded.Count > 0)
                {
                    warnings.Add($"{degraded.Count} Corsair fan(s) on hub {hub.SerialNumber[..4]}… have degraded links "
                        + $"(no tach, no RGB): ch{string.Join(", ch", degraded)} — reseat those magnetic junctions "
                        + "or swap the stack onto the other Link port");
                }

                foreach (var channel in hub.Channels)
                {
                    if (channel.Info?.Flags.HasFlag(LinkDeviceFlags.ControlsSpeed) != true)
                    {
                        continue;
                    }

                    readings.Add(new DeviceReading(
                        "Corsair",
                        $"{channel.Name} (ch{channel.Channel} · hub {hub.SerialNumber[..4]})",
                        speeds.TryGetValue(channel.Channel, out var s) ? s.Rpm : null,
                        channel.IsPump ? pumpDuty : TryGetPulse(hub.SerialNumber, channel.Channel) ? 100 : duty,
                        channel.IsPump,
                        channel.Id,
                        hub.SerialNumber,
                        channel.Channel));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Link hub {hub.SerialNumber[..8]}… failed: {ex.Message} — reopening next tick");
                hub.Dispose();
                _hubs.Remove(hub);
            }
        }

        if (mustWrite)
        {
            _lastWrittenCorsairDuty = writeKey;
            _ticksSinceCorsairWrite = 0;
        }
    }

    private readonly Dictionary<string, bool> _slv3TelemetryFresh = [];
    private readonly Dictionary<string, bool> _slv3BoundState = [];
    private readonly Dictionary<string, int> _slv3UnpairedPolls = [];
    private readonly Dictionary<string, long> _slv3RebindNotBefore = [];
    private readonly HashSet<string> _slv3EverBound = [];

    private void ApplySlv3(int duty, List<DeviceReading> readings, List<string> warnings)
    {
        _slv3KeepaliveDuty = duty; // the keepalive thread sends the PWM at its own 1 Hz cadence

        // Everything below holds _slv3Gate: the dongle pair is ONE RF system and its firmware
        // crashes when TX traffic (keepalive) and RX polling overlap — the v21.0 release
        // interleaved them and the TX dongle died with error 31 every ~50 s, taking the whole
        // USB branch (including the Link hubs) down with it. Poll timeouts are short and
        // bounded instead, so the keepalive is delayed at most ~400 ms in steady state.
        lock (_slv3Gate)
        {
            if (_slv3 is null || _slv3Dead)
            {
                return;
            }

            try
            {
                var devices = _slv3.PollDevices();
                foreach (var device in devices)
                {
                    // pairing loss is otherwise SILENT: an unpaired group ignores our PWM and
                    // colors and just runs its firmware rainbow — make it loud instead
                    var bound = device.IsBoundTo(_slv3.MasterMac);
                    if (_slv3BoundState.TryGetValue(device.MacText, out var wasBound) && wasBound != bound)
                    {
                        _log?.Invoke(bound
                            ? $"SL V3 group {device.MacText[..4]} re-paired to this dongle"
                            : $"SL V3 group {device.MacText[..4]} LOST its pairing to this dongle — its fans run "
                              + "their own defaults (rainbow) until re-paired (power-cycle the fans)");
                    }

                    _slv3BoundState[device.MacText] = bound;
                    if (!bound)
                    {
                        warnings.Add($"SL V3 group {device.MacText[..4]} is not paired to this dongle — fans on firmware defaults");

                        // auto-repair: after 5 consecutive unpaired polls (filters telemetry
                        // glitches), re-pair — but never steal a group that telemetry says
                        // belongs to a DIFFERENT master unless we saw it bound to us before
                        var strikes = _slv3UnpairedPolls.GetValueOrDefault(device.MacText) + 1;
                        _slv3UnpairedPolls[device.MacText] = strikes;
                        var masterless = device.MasterMac.All(b => b == 0);
                        var oursBefore = _slv3EverBound.Contains(device.MacText);
                        if (strikes >= 5 && (masterless || oursBefore)
                            && Environment.TickCount64 >= _slv3RebindNotBefore.GetValueOrDefault(device.MacText))
                        {
                            _slv3RebindNotBefore[device.MacText] = Environment.TickCount64 + 120_000;
                            try
                            {
                                if (_slv3.BindDevice(device, _log))
                                {
                                    _appliedSlv3Rgb.Remove(device.MacText); // colors re-send next tick
                                    _slv3UnpairedPolls.Remove(device.MacText);
                                }
                            }
                            catch (Exception bex)
                            {
                                _log?.Invoke($"SL V3 group {device.MacText[..4]} re-pair attempt failed: {bex.Message}");
                            }
                        }

                        continue;
                    }

                    _slv3UnpairedPolls.Remove(device.MacText);
                    _slv3EverBound.Add(device.MacText);

                    var fresh = _slv3.LastSeenAgeMs(device) <= Slv3StaleAfterMs;
                    if (_slv3TelemetryFresh.TryGetValue(device.MacText, out var was) && was != fresh)
                    {
                        _log?.Invoke(fresh
                            ? $"SL V3 group {device.MacText[..4]} telemetry recovered"
                            : $"SL V3 group {device.MacText[..4]} telemetry lost — speeds and colors keep being sent");
                    }

                    _slv3TelemetryFresh[device.MacText] = fresh;

                    var rpms = device.FanRpms.Take(Math.Max((int)device.FanCount, 1))
                        .Where(r => r > 0).Select(r => (int)r).ToList();
                    readings.Add(new DeviceReading(
                        "Lian Li",
                        $"SL V3 group {device.MacText[..4]} ({device.FanCount} fans)",
                        fresh && rpms.Count > 0 ? (int)rpms.Average() : null,
                        duty,
                        IsPump: false,
                        device.MacText));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"SL V3 failed: {ex.Message} — reopening next tick");
                _slv3Dead = true; // the tick's EnsureDevices reopens under the gate
            }
        }
    }

    /// <summary>
    /// Motherboard headers + GPU coolers via LHM (elevated only). Same global duty as the
    /// fan families, floored at 30% in the device layer because SuperIO has no hardware
    /// fallback. Write-on-change plus a periodic refresh, like the Corsair path.
    /// </summary>
    private void ApplyHeaders(int duty, List<DeviceReading> readings, List<string> warnings)
    {
        if (_headers is null)
        {
            return;
        }

        try
        {
            var mustWrite = duty != _lastWrittenHeaderDuty || ++_ticksSinceHeaderWrite >= CorsairRefreshTicks;
            if (mustWrite)
            {
                _headers.SetAllDuties(duty);
                _lastWrittenHeaderDuty = duty;
                _ticksSinceHeaderWrite = 0;
            }

            var applied = SafetyGuard.ClampHeaderDuty(duty);
            foreach (var fan in _headers.Enumerate())
            {
                readings.Add(new DeviceReading(fan.Family, fan.Name, fan.Rpm, applied, IsPump: false, fan.Id));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"header/GPU fan control failed: {ex.Message} — reopening next tick");
            try
            {
                _headers.Dispose();
            }
            catch
            {
                // BIOS control returns on reboot regardless
            }

            _headers = null;
        }
    }

    private void TryFailsafe()
    {
        try
        {
            foreach (var hub in _hubs)
            {
                var requests = hub.Channels.Where(c => !c.IsPump)
                    .ToDictionary(c => c.Channel, _ => SafetyLimits.FailsafeDutyPercent);
                hub.WriteFixedDuties(requests);
            }

            _slv3KeepaliveDuty = SafetyLimits.FailsafeDutyPercent; // keepalive thread carries it
            _headers?.SetAllDuties(SafetyLimits.FailsafeDutyPercent);
        }
        catch
        {
            // best effort — SL V3 reverts on its own, Link hubs fall back to hardware mode on reconnect
        }
    }
}
