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

/// <summary>User-facing control configuration, persisted as JSON under %LOCALAPPDATA%\DeviceMaster.</summary>
public sealed class ControlSettings
{
    public ControlMode Mode { get; set; } = ControlMode.Off;
    public int ManualDutyPercent { get; set; } = 50;
    public CurveSource Source { get; set; } = CurveSource.Coolant;
    public List<CurvePoint> CurvePoints { get; set; } = FanCurve.DefaultCoolant.Points.ToList();

    [JsonIgnore]
    public FanCurve Curve => new(CurvePoints.Count > 0 ? CurvePoints : FanCurve.DefaultCoolant.Points);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ConfigPath => Path.Combine(
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
