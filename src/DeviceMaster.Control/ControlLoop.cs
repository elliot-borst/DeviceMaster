using DeviceMaster.Core.Curves;
using DeviceMaster.Core.Safety;
using DeviceMaster.Core.Sensors;
using DeviceMaster.Devices.AsusAura;
using DeviceMaster.Devices.CorsairLink;
using DeviceMaster.Devices.EneRgb;
using DeviceMaster.Devices.LianLi;
using DeviceMaster.Devices.Nvidia;
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

    public int TargetDutyPercent { get; init; }
    public bool FailsafeActive { get; init; }
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
        }
    }

    public void Stop()
    {
        Thread? thread;
        Thread? keepalive;
        lock (_gate)
        {
            if (_thread is null)
            {
                return;
            }

            _cts!.Cancel();
            thread = _thread;
            keepalive = _keepaliveThread;
            _thread = null;
            _keepaliveThread = null;
        }

        thread.Join(TimeSpan.FromSeconds(10));
        keepalive?.Join(TimeSpan.FromSeconds(5));
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
            TargetDutyPercent = duty,
            FailsafeActive = failsafe,
            Devices = readings,
            Warnings = warnings,
        };
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

            if (refreshDue)
            {
                _slv3RgbRefreshDue = Environment.TickCount64 + 60_000;
            }
            else if (sentAny)
            {
                // the RF uplink is fire-and-forget — a group that missed chunks shows its
                // rainbow fallback. One early confirmation re-send bounds that to seconds
                // (never a continuous re-send loop: that flooded the RF network in v17)
                _slv3RgbRefreshDue = Math.Min(_slv3RgbRefreshDue, Environment.TickCount64 + 3_000);
            }
        }
    }

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
                foreach (var device in devices.Where(d => d.IsBoundTo(_slv3.MasterMac)))
                {
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
