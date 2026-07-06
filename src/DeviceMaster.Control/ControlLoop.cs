using DeviceMaster.Core.Curves;
using DeviceMaster.Core.Safety;
using DeviceMaster.Core.Sensors;
using DeviceMaster.Devices.CorsairLink;
using DeviceMaster.Devices.LianLi;
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
    private const int CorsairRefreshTicks = 10; // rewrite unchanged duties every N ticks anyway

    private readonly object _gate = new();
    private readonly Action<string>? _log;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private volatile ControlSettings _settings;
    private volatile ControlStatus _status = new();

    private readonly List<LinkHub> _hubs = [];
    private Slv3Controller? _slv3;
    private LhmSensorSource? _lhm;
    private int _lastWrittenCorsairDuty = -1;
    private int _ticksSinceCorsairWrite;

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

    public void Apply(ControlSettings settings)
    {
        _settings = settings;
        _lastWrittenCorsairDuty = -1; // force a rewrite on the next tick
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

            _cts!.Cancel();
            thread = _thread;
            _thread = null;
        }

        thread.Join(TimeSpan.FromSeconds(10));
        ReleaseHardware();
        _status = new ControlStatus();
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
            if (token.WaitHandle.WaitOne(wait))
            {
                break;
            }
        }
    }

    private void Tick()
    {
        var settings = _settings;
        var warnings = new List<string>();

        EnsureDevices(warnings);

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
                    _log?.Invoke($"control: opened Link hub {hub.SerialNumber[..8]}… ({hub.Channels.Count} devices)");
                }
                catch (Exception ex)
                {
                    warnings.Add($"Link hub open failed: {ex.Message}");
                }
            }
        }

        if (_slv3 is null)
        {
            try
            {
                _slv3 = Slv3Controller.Open();
                _slv3.PollDevices();
                _appliedSlv3Rgb.Clear(); // reapply color after a reconnect
                _log?.Invoke($"control: opened SL V3 dongles (master {_slv3.MasterMacText})");
            }
            catch (Exception ex)
            {
                warnings.Add($"SL V3 open failed: {ex.Message}");
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

        _slv3?.Dispose(); // fans revert to firmware defaults once keepalive stops
        _slv3 = null;

        _lhm?.Dispose();
        _lhm = null;
        _lastWrittenCorsairDuty = -1;
    }

    // ---- RGB (static color, both families) ----

    private readonly Dictionary<string, string> _appliedHubRgb = [];
    private readonly Dictionary<string, long> _hubRgbRetryAt = [];
    private readonly Dictionary<string, string> _appliedSlv3Rgb = [];
    private long _slv3RgbRefreshDue;

    private void ApplyRgb(ControlSettings settings, List<string> warnings)
    {
        var key = $"{settings.RgbR},{settings.RgbG},{settings.RgbB}";
        var (r, g, b) = ((byte)Math.Clamp(settings.RgbR, 0, 255),
                        (byte)Math.Clamp(settings.RgbG, 0, 255),
                        (byte)Math.Clamp(settings.RgbB, 0, 255));

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
            }
        }

        if (_slv3 is null)
        {
            return;
        }

        // wireless effects live in fan firmware; refresh periodically in case a group resets
        var refreshDue = Environment.TickCount64 >= _slv3RgbRefreshDue;
        foreach (var device in _slv3.Devices.Where(d => d.IsBoundTo(_slv3.MasterMac)))
        {
            if (!refreshDue && _appliedSlv3Rgb.GetValueOrDefault(device.MacText) == key)
            {
                continue;
            }

            try
            {
                var ticks = Environment.TickCount64;
                Span<byte> effectIndex = [(byte)(ticks >> 24), (byte)(ticks >> 16), (byte)(ticks >> 8), (byte)ticks];
                _slv3.ApplyStaticColor(device, r, g, b, effectIndex);
                _appliedSlv3Rgb[device.MacText] = key;
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

    private void ApplyCorsair(int duty, int pumpDuty, List<DeviceReading> readings, List<string> warnings)
    {
        var pulseActive = AnyPulsePending();
        var writeKey = duty * 1000 + pumpDuty;
        var mustWrite = writeKey != _lastWrittenCorsairDuty
            || ++_ticksSinceCorsairWrite >= CorsairRefreshTicks
            || pulseActive
            || _pulseWasActive; // one extra write to restore duties after a pulse ends
        _pulseWasActive = pulseActive;

        foreach (var hub in _hubs.ToList())
        {
            try
            {
                if (!hub.InSoftwareMode)
                {
                    hub.EnterSoftwareMode();
                }

                if (mustWrite)
                {
                    var requests = hub.Channels.Where(c => !c.IsPump).ToDictionary(
                        c => c.Channel,
                        c => TryGetPulse(hub.SerialNumber, c.Channel) ? 100 : duty);
                    hub.WriteFixedDuties(requests, pumpDuty);
                }

                var speeds = hub.ReadSpeeds().Where(s => s.Rpm is not null).ToDictionary(s => s.Channel);
                foreach (var channel in hub.Channels)
                {
                    if (channel.Info?.Flags.HasFlag(LinkDeviceFlags.ControlsSpeed) != true)
                    {
                        continue;
                    }

                    readings.Add(new DeviceReading(
                        "Corsair",
                        $"{channel.Name} (ch{channel.Channel})",
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

    private void ApplySlv3(int duty, List<DeviceReading> readings, List<string> warnings)
    {
        if (_slv3 is null)
        {
            return;
        }

        try
        {
            _slv3.SendKeepalive(duty);
            var devices = _slv3.PollDevices();
            foreach (var device in devices.Where(d => d.IsBoundTo(_slv3.MasterMac)))
            {
                var rpms = device.FanRpms.Take(Math.Max((int)device.FanCount, 1))
                    .Where(r => r > 0).Select(r => (int)r).ToList();
                readings.Add(new DeviceReading(
                    "Lian Li",
                    $"SL V3 group {device.MacText[..4]} ({device.FanCount} fans)",
                    rpms.Count > 0 ? (int)rpms.Average() : null,
                    duty,
                    IsPump: false,
                    device.MacText));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"SL V3 failed: {ex.Message} — reopening next tick");
            _slv3.Dispose();
            _slv3 = null;
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

            _slv3?.SendKeepalive(SafetyLimits.FailsafeDutyPercent);
        }
        catch
        {
            // best effort — SL V3 reverts on its own, Link hubs fall back to hardware mode on reconnect
        }
    }
}
