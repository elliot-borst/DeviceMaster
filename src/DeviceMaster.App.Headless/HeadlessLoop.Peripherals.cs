using DeviceMaster.Control;
using DeviceMaster.Devices.AsusAura;
using DeviceMaster.Devices.Turzx;
using DeviceMaster.Lcd.Skia;
using DeviceMaster.Sensors.Linux;

namespace DeviceMaster.App.Headless;

/// <summary>
/// The non-Corsair hardware of an air-cooled AI box: motherboard fan headers (Super I/O hwmon),
/// the ASUS Aura LED controller, the Turzx 8.8" dashboard and the local LLM status it shows.
/// Every piece is optional and absent hardware is simply skipped.
/// </summary>
public sealed partial class HeadlessLoop
{
    private const int MotherboardRefreshTicks = 30;
    private const int TurzxFrameMs = 3_000;          // a full 480×1920 serial push takes ~2.3 s
    private const int TurzxRetryMs = 15_000;
    private const int StrataPollMs = 2_000;

    // motherboard fan headers
    private HwmonFanControl? _mbFans;
    private bool _mbFansProbed;
    private int _mbTicks;

    // ASUS Aura
    private AuraController? _aura;
    private string? _appliedAura;
    private long _auraRetryAt;

    // local LLM status (polled off the tick)
    private StrataStatus? _strata;
    private volatile LlmStatus? _llm;
    private Thread? _strataThread;

    // Turzx dashboard (own thread: a frame push takes seconds)
    private Thread? _turzxThread;
    private readonly AutoResetEvent _turzxWake = new(false);
    private volatile TurzxDashboardData? _dashboard;

    // GPU fan speed: the LLM server's GPU figures have none, so nvidia-smi is asked alongside it
    private double? _gpuFanPercent;
    private int? _gpuFanRpm;          // NVML (nvidia-smi has percent only)
    private NvmlFans? _nvmlFans;
    private long _nvmlRetryAt;

    private void StartPeripheralWorkers(CancellationToken token)
    {
        _turzxThread = new Thread(() => RunTurzx(token)) { IsBackground = true, Name = "DeviceMaster Turzx dashboard" };
        _turzxThread.Start();
        _strataThread = new Thread(() => RunStrata(token)) { IsBackground = true, Name = "DeviceMaster LLM status" };
        _strataThread.Start();
    }

    private void StopPeripheralWorkers()
    {
        _turzxWake.Set();
        _turzxThread?.Join(TimeSpan.FromSeconds(8));
        _strataThread?.Join(TimeSpan.FromSeconds(4));
        _turzxThread = null;
        _strataThread = null;
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

    // ---------- local LLM status ----------

    private void RunStrata(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var cfg = _config;
            if (cfg.StrataUrl is { Length: > 0 } url)
            {
                try
                {
                    _strata ??= new StrataStatus(url, StrataStatus.ReadKeyFile(cfg.StrataKeyFile));
                    _llm = _strata.ReadAsync(token).GetAwaiter().GetResult() ?? _llm;
                    _gpuFanPercent = File.Exists(cfg.NvidiaSmiPath) ? NvidiaSmi.ReadFirst(cfg.NvidiaSmiPath)?.FanPercent : null;
                    _gpuFanRpm = ReadGpuFanRpm();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // server down or restarting: the dashboard shows it offline once the reading ages out
                }
            }

            token.WaitHandle.WaitOne(StrataPollMs);
        }

        _strata?.Dispose();
        _strata = null;
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

    /// <summary>A recent LLM reading (≤ 10 s old), or null.</summary>
    private LlmStatus? FreshLlm() =>
        _llm is { } llm && DateTime.UtcNow - llm.ReadAtUtc < TimeSpan.FromSeconds(10) ? llm : null;

    /// <summary>GPU figures from the LLM server's own sampling, when it's running (no nvidia-smi needed in the container).</summary>
    private GpuReading? GpuFromLlm() =>
        FreshLlm() is { GpuTempC: { } temp } llm
            ? new GpuReading(llm.GpuName ?? "GPU", temp, llm.GpuUtil, llm.GpuPowerW, llm.GpuMemUsedMb, llm.GpuMemTotalMb)
            : null;

    // ---------- Turzx 8.8" dashboard ----------

    /// <summary>Builds the dashboard snapshot from this tick's status (rendered and pushed by the Turzx thread).</summary>
    private void UpdateDashboard(HeadlessConfig cfg, ControlStatus status)
    {
        var llm = FreshLlm();
        var fan = status.Devices.FirstOrDefault(d => d.Family == "motherboard" && d.Rpm is > 0)
            ?? status.Devices.FirstOrDefault(d => !d.IsPump && !d.IsScreen && d.Rpm is > 0);
        _dashboard = new TurzxDashboardData
        {
            HasLlm = cfg.StrataUrl is { Length: > 0 },
            LlmReachable = llm is not null,
            LlmModel = llm?.Model ?? "",
            LlmState = llm?.State ?? "offline",
            LlmPhase = llm?.Phase,
            LlmRunning = llm?.Running ?? 0,
            LlmTokPerSec = llm?.TokPerSec,
            LlmTokHistory = llm?.TokHistory ?? [],
            LlmSlots = llm?.Slots.Select(s => new DashboardSlot(s.State, s.HeldTokens)).ToList() ?? [],
            LlmQueued = llm?.Queued ?? 0,
            LlmOutputTokens = llm?.OutputTokens ?? 0,
            LlmPromptTokPerSec = llm?.PromptTokPerSec,
            LlmDraftHitPercent = llm?.DraftHitPercent,
            LlmCacheHitPercent = llm?.CacheHitPercent,
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
