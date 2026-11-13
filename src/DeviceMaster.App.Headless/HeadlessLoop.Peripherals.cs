using DeviceMaster.Control;
using DeviceMaster.Devices.AsusAura;
using DeviceMaster.Devices.MsiMysticLight;
using DeviceMaster.Devices.Turzx;
using DeviceMaster.Lcd.Skia;
using DeviceMaster.Sensors.Linux;

namespace DeviceMaster.App.Headless;

/// <summary>
/// The non-Corsair hardware of an air-cooled box: motherboard fan headers (Super I/O hwmon),
/// the ASUS Aura LED controller, and the Turzx 8.8" dashboard.
/// Every piece is optional and absent hardware is simply skipped.
/// </summary>
public sealed partial class HeadlessLoop
{
    private const int MotherboardRefreshTicks = 30;
    private const int TurzxFrameMs = 3_000;          // a full 480×1920 serial push takes ~2.3 s
    private const int TurzxRetryMs = 15_000;
    private const int GpuFanPollMs = 2_000;

    // motherboard fan headers
    private HwmonFanControl? _mbFans;
    private bool _mbFansProbed;
    private int _mbTicks;

    // ASUS Aura
    private AuraController? _aura;
    private string? _appliedAura;
    private long _auraRetryAt;
    private string _auraSummary = "Aura: not opened yet";

    // Turzx dashboard (own thread: a frame push takes seconds)
    private Thread? _turzxThread;
    private readonly AutoResetEvent _turzxWake = new(false);
    private volatile TurzxDashboardData? _dashboard;

    // GPU fan RPM (polled off the tick; nvidia-smi has percent only)
    private Thread? _gpuFanThread;
    private int? _gpuFanRpm;          // NVML
    private NvmlFans? _nvmlFans;
    private long _nvmlRetryAt;

    private void StartPeripheralWorkers(CancellationToken token)
    {
        _turzxThread = new Thread(() => RunTurzx(token)) { IsBackground = true, Name = "DeviceMaster Turzx dashboard" };
        _turzxThread.Start();
        _gpuFanThread = new Thread(() => RunGpuFan(token)) { IsBackground = true, Name = "DeviceMaster GPU fan" };
        _gpuFanThread.Start();
    }

    private void StopPeripheralWorkers()
    {
        _turzxWake.Set();
        _turzxThread?.Join(TimeSpan.FromSeconds(8));
        _gpuFanThread?.Join(TimeSpan.FromSeconds(4));
        _turzxThread = null;
        _gpuFanThread = null;
    }

    // ---------- motherboard fan headers ----------

    /// <summary>Same duty as every other fan (failsafe included); hands headers back to the BIOS in mode Off.</summary>
    private void ApplyMotherboardFans(HeadlessConfig cfg, int duty, bool off, List<DeviceReading> readings, List<string> warnings)
    {
        if (!cfg.MotherboardFans)
        {
            HandBackMotherboardFans(warnings); // non-blocking: over the next few ticks
            return;
        }

        if (!_mbFansProbed)
        {
            _mbFansProbed = true;
            try
            {
                _mbFans = HwmonFanControl.Open(log: _log);
                if (_mbFans is not null)
                {
                    _log($"headless: motherboard fans on {_mbFans.Chip}: controlling header(s) "
                        + $"[{string.Join(", ", _mbFans.Controlled.Select(i => $"pwm{i}"))}] (spinning at start-up)");
                }
            }
            catch (Exception ex)
            {
                _log($"headless: motherboard fan controller unavailable: {ex.Message}");
            }
        }

        if (_mbFans is null)
        {
            return;
        }

        try
        {
            if (off)
            {
                warnings.AddRange(_mbFans.HandBack().Select(p => $"motherboard fans: {p}")); // BIOS curve back in charge, non-blocking
            }
            else
            {
                warnings.AddRange(_mbFans.Apply(duty, refresh: ++_mbTicks % MotherboardRefreshTicks == 0)
                    .Select(w => $"motherboard fan write failed: {w}"));
            }

            foreach (var (index, reason) in _mbFans.Dropped)
            {
                warnings.Add($"motherboard fan pwm{index} handed back to the BIOS: it does not take a duty ({reason})");
            }

            foreach (var header in _mbFans.Headers().Where(h => _mbFans.Controlled.Contains(h.Index)))
            {
                readings.Add(new DeviceReading("motherboard", header.Label, header.Rpm,
                    off ? 0 : Math.Max(duty, Core.Safety.SafetyLimits.HeaderMinimumDutyPercent), IsPump: false, Id: $"{_mbFans.Chip}:pwm{header.Index}"));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"motherboard fan write failed: {ex.Message}");
        }
    }

    private void HandBackMotherboardFans(List<string> warnings)
    {
        try
        {
            if (_mbFans is not null)
            {
                warnings.AddRange(_mbFans.HandBack().Select(p => $"motherboard fans: {p}"));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"motherboard fan handback failed: {ex.Message}");
        }
    }

    /// <summary>Shutdown: blocking, verified handback of every header (#35, #37).</summary>
    private void ReleaseMotherboardFans()
    {
        try
        {
            // Release verifies every restore and logs any header it could not hand back (#35)
            _mbFans?.Release();
        }
        catch (Exception ex)
        {
            _log($"headless: motherboard fan release failed: {ex.Message}");
        }
    }

    // ---------- MSI Mystic Light (761 protocol) ----------

    private MysticLightController? _msi;
    private string? _appliedMsi;
    private long _msiRetryAt;
    private long _msiRefreshDue;
    private string _msiSummary = "Mystic Light: not opened yet";
    private const int MsiRefreshMs = 30_000;   // static re-sent so a board-side reset heals itself
    private const int MsiRainbowLeds = 24;     // one wheel per 24 LEDs, repeated along longer strips

    private void ApplyMysticLight(HeadlessConfig cfg, List<string> warnings)
    {
        if (!cfg.AuraEnabled || !cfg.Control.RgbEnabled) // the one "motherboard RGB" switch
        {
            return;
        }

        if (_msi is null)
        {
            if (Environment.TickCount64 < _msiRetryAt)
            {
                return;
            }

            _msiRetryAt = Environment.TickCount64 + 60_000;
            try
            {
                if (MysticLightController.FindDevices().FirstOrDefault() is { } device)
                {
                    _msi = MysticLightController.Open(device);
                    _appliedMsi = null;
                    _msiSummary = _msi.Describe();
                    _log($"headless: opened {_msiSummary}");
                }
                else
                {
                    _msiSummary = "Mystic Light: no 761-protocol controller found (HID 0DB0:0076)";
                }
            }
            catch (Exception ex)
            {
                var summary = $"Mystic Light: controller unavailable: {ex.Message}";
                if (summary != _msiSummary)
                {
                    _log($"headless: {summary}");
                }

                _msiSummary = summary;
            }

            if (_msi is null)
            {
                return;
            }
        }

        var (r, g, b) = RgbTarget(cfg);
        var rainbow = cfg.Control.RainbowActive && !cfg.Control.RgbOff;
        var key = rainbow ? $"rainbow:{cfg.Control.RgbSpeed}:{cfg.Control.RgbBrightness}" : $"{r},{g},{b}";
        if (!rainbow && key == _appliedMsi && Environment.TickCount64 < _msiRefreshDue)
        {
            return;
        }

        try
        {
            if (rainbow)
            {
                // no built-in effect on this protocol: one wheel step per tick, painted from here
                var phase = Environment.TickCount64 / 1000.0 / DeviceMaster.Core.Lighting.RainbowEffect.CycleSeconds(cfg.Control.RgbSpeed) % 1.0;
                _msi.ApplyFrame(DeviceMaster.Core.Lighting.RainbowEffect.Frame(MsiRainbowLeds, phase, cfg.Control.RgbBrightness));
            }
            else
            {
                _msi.ApplyStaticColor(r, g, b);
            }

            if (key != _appliedMsi)
            {
                _log($"RGB applied on {_msi.Describe()}: {key}");
            }

            _appliedMsi = key;
            _msiRefreshDue = Environment.TickCount64 + MsiRefreshMs;
            _msiSummary = _msi.Describe();
        }
        catch (Exception ex)
        {
            _msiSummary = $"Mystic Light: apply failed: {ex.Message}";
            warnings.Add($"Mystic Light RGB failed: {ex.Message}");
            _log($"headless: {_msiSummary} — reopening");
            _msi.Dispose();
            _msi = null;
            _msiRetryAt = Environment.TickCount64 + 10_000;
        }
    }

    // ---------- ASUS Aura ----------

    private void ApplyAura(HeadlessConfig cfg, List<string> warnings)
    {
        if (!cfg.AuraEnabled || !cfg.Control.RgbEnabled)
        {
            return;
        }

        if (_aura is null)
        {
            if (Environment.TickCount64 < _auraRetryAt)
            {
                return;
            }

            _auraRetryAt = Environment.TickCount64 + 60_000;
            try
            {
                if (AuraController.FindDevices().FirstOrDefault() is { } device)
                {
                    _aura = AuraController.Open(device);
                    _appliedAura = null;
                    _auraSummary = _aura.Describe();
                    _log($"headless: opened {_auraSummary}");
                }
                else
                {
                    _auraSummary = "Aura: no mainboard LED controller found (HID 0B05, 65-byte reports)";
                }
            }
            catch (Exception ex)
            {
                _auraSummary = $"Aura: controller unavailable: {ex.Message}";
                _log($"headless: {_auraSummary}");
            }

            if (_aura is null)
            {
                return;
            }
        }

        var (r, g, b) = RgbTarget(cfg);
        var rainbow = cfg.Control.RainbowActive;
        var key = rainbow ? "rainbow" : $"{r},{g},{b}";
        if (key == _appliedAura)
        {
            return;
        }

        try
        {
            if (rainbow)
            {
                _aura.ApplyRainbow();
            }
            else
            {
                _aura.ApplyStaticColor(r, g, b);
            }

            _appliedAura = key;
            _auraSummary = _aura.Describe();
            _log($"RGB applied on Aura ({_aura.Zones.Count} zones): {key}");
            if (_aura.LastZoneErrors.Count > 0)
            {
                _log($"headless: Aura zone errors: {string.Join("; ", _aura.LastZoneErrors)}");
            }
        }
        catch (Exception ex)
        {
            _auraSummary = $"Aura: apply failed: {ex.Message} — {_aura.Describe()}";
            warnings.Add($"Aura RGB failed: {ex.Message}");
            _aura.Dispose();
            _aura = null; // reopen after the retry window
        }
    }

    private List<string> RgbDiagnostics()
    {
        var lines = new List<string>();
        lines.Add(_auraSummary);
        lines.Add(_msiSummary);
        lines.Add(_ramRgbSummary);
        if (_aura?.LastZoneErrors.Count > 0)
        {
            lines.Add($"Aura zone errors: {string.Join("; ", _aura.LastZoneErrors)}");
        }

        return lines;
    }

    // ---------- GPU fan RPM ----------

    private void RunGpuFan(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _gpuFanRpm = ReadGpuFanRpm();
            }
            catch
            {
                // no NVIDIA driver or the read failed: the dashboard falls back to percent
            }

            token.WaitHandle.WaitOne(GpuFanPollMs);
        }

        _nvmlFans?.Dispose();
        _nvmlFans = null;
    }

    private int? ReadGpuFanRpm()
    {
        if (_nvmlFans is null && Environment.TickCount64 >= _nvmlRetryAt)
        {
            _nvmlRetryAt = Environment.TickCount64 + 60_000;
            _nvmlFans = NvmlFans.TryOpen();
        }

        return _nvmlFans?.ReadRpm();
    }

    // ---------- Turzx 8.8" dashboard ----------

    /// <summary>Builds the dashboard snapshot from this tick's status (rendered and pushed by the Turzx thread).</summary>
    private void UpdateDashboard(ControlStatus status)
    {
        var fan = status.Devices.FirstOrDefault(d => d.Family == "motherboard" && d.Rpm is > 0)
            ?? status.Devices.FirstOrDefault(d => !d.IsPump && !d.IsScreen && d.Rpm is > 0);
        _dashboard = new TurzxDashboardData
        {
            GpuName = status.GpuName ?? "GPU",
            GpuTempC = status.GpuTemperatureC,
            GpuUtil = status.GpuLoadPercent,
            GpuPowerW = status.GpuPowerW,
            GpuFanPercent = status.GpuFanPercent,
            GpuFanRpm = status.GpuFanRpm,
            VramUsedGb = status.VramUsedGb,
            VramTotalGb = status.VramTotalGb,
            CpuName = status.CpuName ?? "CPU",
            CpuTempC = status.CpuTemperatureC,
            CpuLoad = status.CpuLoadPercent,
            CpuPowerW = status.CpuPowerW,
            RamUsedGb = status.RamUsedGb,
            RamTotalGb = status.RamTotalGb,
            FanRpm = fan?.Rpm,
            FanDuty = status.TargetDutyPercent,
            Failsafe = status.FailsafeActive,
        };
    }

    // whether a Turzx screen is attached (open, or found by a once-a-minute probe while unmanaged)
    private volatile bool _turzxOpen;
    private bool _turzxSeen;
    private long _turzxProbeDue;

    private bool TurzxPresent()
    {
        if (_turzxOpen)
        {
            _turzxSeen = true;
        }
        else if (Environment.TickCount64 >= _turzxProbeDue)
        {
            _turzxProbeDue = Environment.TickCount64 + 60_000;
            try
            {
                _turzxSeen = TurzxScreen.FindLinuxDataPort() is not null;
            }
            catch
            {
                // leave the last answer
            }
        }

        return _turzxSeen;
    }

    private void RunTurzx(CancellationToken token)
    {
        TurzxScreen? screen = null;
        int? brightness = null;
        long retryAt = 0;
        while (!token.IsCancellationRequested)
        {
            var cfg = _config;
            var mode = cfg.Control.TurzxScreen;
            try
            {
                if (mode == LcdMode.Unmanaged)
                {
                    screen?.Dispose();
                    screen = null;
                    brightness = null;
                }
                else if (Environment.TickCount64 >= retryAt && _dashboard is { } data)
                {
                    if (screen is null)
                    {
                        if (TurzxScreen.FindLinuxDataPort() is not { } port)
                        {
                            retryAt = Environment.TickCount64 + TurzxRetryMs;
                            throw new InvalidOperationException("no Turzx screen found");
                        }

                        screen = TurzxScreen.Open(port);
                        brightness = null;
                        _log($"headless: opened Turzx 8.8\" screen on {port}");
                    }

                    screen.FlipLandscape = cfg.Control.TurzxRotation == 180;
                    var wanted = mode == LcdMode.Off ? 0 : Math.Clamp(cfg.Control.TurzxBrightness, 0, 100);
                    if (brightness != wanted)
                    {
                        screen.SetBrightness(wanted);
                        brightness = wanted;
                    }

                    if (mode != LcdMode.Off)
                    {
                        screen.SendNativeFrame(TurzxDashboard.RenderNative(data, screen.FlipLandscape));
                    }
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                if (screen is not null)
                {
                    _log($"headless: Turzx screen error ({ex.Message}) — reopening in {TurzxRetryMs / 1000} s");
                    screen.Dispose();
                    screen = null;
                    retryAt = Environment.TickCount64 + TurzxRetryMs;
                }
            }

            _turzxOpen = screen is not null;
            _turzxWake.WaitOne(TurzxFrameMs);
        }

        screen?.Dispose(); // the panel keeps showing the last frame
        _turzxOpen = false;
    }

    private void ReleasePeripherals()
    {
        ReleaseMotherboardFans();
        _mbFans = null;
        _mbFansProbed = false;
        try
        {
            _aura?.Dispose();
        }
        catch
        {
        }

        _aura = null;
        try
        {
            _msi?.Dispose(); // direct colours stay until the board resets; nothing is saved to its flash
        }
        catch
        {
        }

        _msi = null;
        ReleaseRamRgb();
    }
}
