namespace DeviceMaster.Sensors;

/// <summary>
/// A one-shot snapshot of the headline CPU/GPU/RAM figures for the Turzx dashboard. Any field
/// is null when the sensor isn't present or readable (the renderer shows a placeholder). Names
/// are the raw LibreHardwareMonitor hardware names (e.g. "AMD Ryzen 7 9800X3D") — shortening is
/// the renderer's job.
/// </summary>
public sealed record SystemStats(
    string CpuName,
    double? CpuLoadPercent,
    double? CpuTempC,
    double? CpuPowerW,
    double? RamUsedGb,
    string GpuName,
    double? GpuLoadPercent,
    double? GpuTempC,
    double? GpuPowerW,
    double? VramUsedGb);
