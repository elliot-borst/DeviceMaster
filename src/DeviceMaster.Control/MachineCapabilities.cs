namespace DeviceMaster.Control;

/// <summary>
/// What a machine can actually be controlled through, as its loop sees it right now (#33). The
/// fleet UI shows only the controls that apply to a machine, and a fleet setting is simply skipped
/// by machines without the hardware (no pump ⇒ pump duty is ignored, and so on).
/// </summary>
public sealed record MachineCapabilities
{
    /// <summary>"windows" or "linux".</summary>
    public string Platform { get; init; } = OperatingSystem.IsWindows() ? "windows" : "linux";

    public string MachineName { get; init; } = Environment.MachineName;

    // ---- cooling ----
    public bool Pump { get; init; }
    public bool CorsairHubs { get; init; }
    public bool LianLiFans { get; init; }
    public bool MotherboardFans { get; init; }

    // ---- lighting ----
    public bool MotherboardRgb { get; init; }
    public bool RamRgb { get; init; }
    public bool GpuRgb { get; init; }

    /// <summary>LED chains behind the Corsair hubs / Lian Li receivers (fans, pump).</summary>
    public bool FanRgb { get; init; }

    // ---- screens ----
    public bool PumpLcd { get; init; }
    public bool FanLcds { get; init; }
    public bool Turzx { get; init; }

    // ---- sensors (what the curve sources can use) ----
    public bool CpuTemperature { get; init; }
    public bool GpuTemperature { get; init; }
    public bool CoolantTemperature { get; init; }

    /// <summary>Any controllable lighting at all.</summary>
    public bool Lighting => MotherboardRgb || RamRgb || GpuRgb || FanRgb;

    /// <summary>Any controllable fan at all.</summary>
    public bool Fans => CorsairHubs || LianLiFans || MotherboardFans;
}
