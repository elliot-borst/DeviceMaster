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
    Metrics,
}

public enum LcdMetric
{
    Coolant = 0,
    CpuTemp,
    GpuTemp,
    CpuLoad,
    GpuLoad,
    Clock,
    RamLoad,
    PumpRpm,
    FanDuty,
    Date,
    VramLoad,
}

/// <summary>Per-screen display configuration (metric, rotation, font color).</summary>
public sealed class LcdScreenConfig
{
    /// <summary>"pump-lcd" or the fan LCD node's serial.</summary>
    public string Id { get; set; } = "";

    /// <summary>User-chosen group name ("Front", "Side floor", …); empty = ungrouped.</summary>
    public string Group { get; set; } = "";

    public LcdMetric Metric { get; set; } = LcdMetric.CpuTemp;

    /// <summary>0, 90, 180 or 270 — applied when rendering the frame.</summary>
    public int RotationDegrees { get; set; }

    /// <summary>Fixed font color; all three null = plain white.</summary>
    public int? FontR { get; set; }
    public int? FontG { get; set; }
    public int? FontB { get; set; }

    /// <summary>Color the value by temperature/load thresholds (green/amber/red) instead.</summary>
    public bool ColorByValue { get; set; }
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

    /// <summary>Auto-started launches stay in the tray; off = show the window on every launch.</summary>
    public bool StartHidden { get; set; } = true;

    public int RgbR { get; set; } = 86;
    public int RgbG { get; set; } = 130;
    public int RgbB { get; set; } = 255;

    /// <summary>Pump LCD + per-fan LCD screens: leave alone, backlight off, a solid background, or metrics.</summary>
    public LcdMode LcdScreens { get; set; } = LcdMode.Unmanaged;

    /// <summary>Metric shown on the pump screen while <see cref="LcdScreens"/> is Metrics (default for new configs).</summary>
    public LcdMetric PumpScreenMetric { get; set; } = LcdMetric.Coolant;

    /// <summary>Metric shown on every fan screen while <see cref="LcdScreens"/> is Metrics (default for new configs).</summary>
    public LcdMetric FanScreenMetric { get; set; } = LcdMetric.CpuTemp;

    /// <summary>Per-screen overrides; screens without an entry fall back to the defaults above.</summary>
    public List<LcdScreenConfig> LcdScreenConfigs { get; set; } = [];

    /// <summary>Movie mode: one switch that blacks out every LED and screen; restores on toggle-off.</summary>
    public bool BlackoutActive { get; set; }
    public bool BlackoutPrevRgbEnabled { get; set; } = true;
    public bool BlackoutPrevRgbOff { get; set; }
    public LcdMode BlackoutPrevLcd { get; set; } = LcdMode.Metrics;

    /// <summary>Finds (or creates, unsaved) the config for a screen id.</summary>
    public LcdScreenConfig ScreenConfig(string id, bool isPump)
    {
        var config = LcdScreenConfigs.FirstOrDefault(c => c.Id == id);
        if (config is null)
        {
            config = new LcdScreenConfig { Id = id, Metric = isPump ? PumpScreenMetric : FanScreenMetric };
            LcdScreenConfigs.Add(config);
        }

        return config;
    }

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
