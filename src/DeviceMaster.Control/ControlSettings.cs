using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceMaster.Core.Curves;

namespace DeviceMaster.Control;

public enum ControlMode
{
    /// <summary>DeviceMaster leaves the hardware alone (hub/firmware curves rule).</summary>
    Off = 0,
    Manual,
    Curve,
}

public enum CurveSource
{
    /// <summary>Loop coolant temperature from the iCUE Link chain — the right source for radiator fans.</summary>
    Coolant = 0,
    Cpu,
    Gpu,
}

public enum LcdMode
{
    /// <summary>DeviceMaster never touches the screens — they show whatever they show.</summary>
    Unmanaged = 0,
    Off,
    Black,
    White,
}

/// <summary>User-facing control configuration, persisted as JSON under %LOCALAPPDATA%\DeviceMaster.</summary>
public sealed class ControlSettings
{
    public ControlMode Mode { get; set; } = ControlMode.Off;
    public int ManualDutyPercent { get; set; } = 50;

    /// <summary>Pump duty, controlled separately from fans; hard-floored at 50% by the write layer.</summary>
    public int PumpDutyPercent { get; set; } = 100;

    public CurveSource Source { get; set; } = CurveSource.Coolant;
    public List<CurvePoint> CurvePoints { get; set; } = FanCurve.DefaultCoolant.Points.ToList();

    /// <summary>When enabled, DeviceMaster drives all lighting with one static color.</summary>
    public bool RgbEnabled { get; set; }

    /// <summary>
    /// Lights out: actively paints every LED black (and persists it) instead of the selected
    /// color. Distinct from <see cref="RgbEnabled"/> = false, which stops writing entirely and
    /// lets devices fall back to their own (rainbow) effects.
    /// </summary>
    public bool RgbOff { get; set; }

    /// <summary>Registers the logon scheduled task ("start with Windows"). On by default; the app manages it.</summary>
    public bool StartWithWindows { get; set; } = true;

    public int RgbR { get; set; } = 86;
    public int RgbG { get; set; } = 130;
    public int RgbB { get; set; } = 255;

    /// <summary>Pump LCD + per-fan LCD screens: leave alone, backlight off, or a solid background.</summary>
    public LcdMode LcdScreens { get; set; } = LcdMode.Unmanaged;

    /// <summary>Every fan device id ever seen — the expected set behind the "N/N fans" roll-up.</summary>
    public List<string> SeenFanIds { get; set; } = [];

    /// <summary>Every hardware identity ever seen — the expected set behind the device roll-up.</summary>
    public List<string> SeenDeviceIds { get; set; } = [];

    [JsonIgnore]
    public FanCurve Curve => new(CurvePoints.Count > 0 ? CurvePoints : FanCurve.DefaultCoolant.Points);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ConfigPath =>
        Environment.GetEnvironmentVariable("DEVICEMASTER_CONFIG") is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeviceMaster", "config.json");

    public static ControlSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                return JsonSerializer.Deserialize<ControlSettings>(File.ReadAllText(ConfigPath), JsonOptions)
                    ?? new ControlSettings();
            }
        }
        catch
        {
            // corrupt config -> defaults
        }

        return new ControlSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
