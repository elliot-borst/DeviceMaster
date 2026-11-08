using DeviceMaster.Control;
using DeviceMaster.Devices.AsusAura;
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
            ReleaseMotherboardFans();
            return;
        }

        if (!_mbFansProbed)
        {
            _mbFansProbed = true;
            try
            {
                _mbFans = HwmonFanControl.Open();
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
                _mbFans.Release(); // BIOS curve back in charge
            }
            else
            {
                _mbFans.Apply(duty, refresh: ++_mbTicks % MotherboardRefreshTicks == 0);
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

    private void ReleaseMotherboardFans()
    {
        try
        {
            _mbFans?.Release();
        }
        catch
        {
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
                    _log($"headless: opened Aura controller '{_aura.FirmwareName}' ({_aura.Zones.Count} zones)");
                }
            }
            catch (Exception ex)
            {
                _log($"headless: Aura controller unavailable: {ex.Message}");
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
            _log($"RGB applied on Aura ({_aura.Zones.Count} zones): {key}");
        }
        catch (Exception ex)
        {
            warnings.Add($"Aura RGB failed: {ex.Message}");
            _aura.Dispose();
            _aura = null; // reopen after the retry window
        }
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

            _turzxWake.WaitOne(TurzxFrameMs);
        }

        screen?.Dispose(); // the panel keeps showing the last frame
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
    }
}
