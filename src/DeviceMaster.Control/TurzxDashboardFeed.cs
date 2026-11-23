using DeviceMaster.Lcd.Skia;

namespace DeviceMaster.Control;

/// <summary>
/// The Linux dashboard's data from the Windows app's status (#102): the Turzx 8.8" V1.x USB panel
/// shows the same MindForge-style cards as the headless agents, in the gaming layout:
/// CPU | FPS | GPU. No local LLM panel on Windows.
/// </summary>
public static class TurzxDashboardFeed
{
    public static TurzxDashboardData From(ControlStatus s, int? fps, IReadOnlyList<double> fpsHistory, string? fpsSource = null)
    {
        // the CPU card's COOLER bar: a motherboard header if one spins, else the first spinning fan
        var fan = s.Devices.FirstOrDefault(d => d.Family == "motherboard" && d.Rpm is > 0)
            ?? s.Devices.FirstOrDefault(d => d.Rpm is > 0);
        return new TurzxDashboardData
        {
            HasLlm = false,
            ShowFps = true,
            Fps = fps,
            FpsHistory = fpsHistory,
            FpsSource = fpsSource,
            GpuName = s.GpuName ?? "GPU",
            GpuTempC = s.GpuTemperatureC,
            GpuUtil = s.GpuLoadPercent,
            GpuPowerW = s.GpuPowerW,
            GpuFanPercent = s.GpuFanPercent,
            GpuFanRpm = s.GpuFanRpm,
            VramUsedGb = s.VramUsedGb,
            VramTotalGb = s.VramTotalGb,
            CpuName = s.CpuName ?? "CPU",
            CpuTempC = s.CpuTemperatureC,
            CpuLoad = s.CpuLoadPercent,
            CpuPowerW = s.CpuPowerW,
            RamUsedGb = s.RamUsedGb,
            RamTotalGb = s.RamTotalGb,
            RamInstalledGb = s.RamInstalledGb,
            FanRpm = fan?.Rpm,
            FanDuty = s.TargetDutyPercent,
            Failsafe = s.FailsafeActive,
        };
    }
}
