using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceMaster.Core.Curves;
using DeviceMaster.Core.Lighting;

namespace DeviceMaster.Control;

public enum ControlMode
{
    /// <summary>DeviceMaster leaves the hardware alone (hub/firmware curves rule).</summary>
    Off = 0,
    Manual,
    Curve,
}

/// <summary>
/// The temperature a fan curve follows. The numbers are stable on the wire (web patches, older
/// configs): 0 was the removed coolant source, which loads as <see cref="Hottest"/> (see
/// <see cref="CurveSourceJsonConverter"/>).
/// </summary>
[JsonConverter(typeof(CurveSourceJsonConverter))]
public enum CurveSource
{
    Cpu = 1,
    Gpu = 2,

    /// <summary>
    /// The hotter of CPU and GPU (smoothed) — the fleet-wide default (#33): fans ramp when either
    /// works hard. A machine with a GPU whose reading fails goes to failsafe, never CPU-only.
    /// </summary>
    Hottest = 3,
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

/// <summary>
/// A screen metric. The numbers are stable on the wire; the removed coolant and pump metrics
/// (0, 7, 12) load as <see cref="CpuTemp"/> (see <see cref="LcdMetricJsonConverter"/>).
/// </summary>
[JsonConverter(typeof(LcdMetricJsonConverter))]
public enum LcdMetric
{
    CpuTemp = 1,
    GpuTemp = 2,
    CpuLoad = 3,
    GpuLoad = 4,
    Clock = 5,
    RamLoad = 6,
    FanDuty = 8,
    Date = 9,
    VramLoad = 10,
    FanRpm = 11,
    GpuPower = 13,
    CpuPower = 14,
    GpuClock = 15,
    VramTemp = 16,
}

/// <summary>User-facing control configuration, persisted as JSON under %LOCALAPPDATA%\DeviceMaster.</summary>
/// <remarks>
/// Settings written before pump/coolant support was removed load safely: unknown properties
/// (PumpDutyPercent, the pump/fan LCD fields) are ignored, and a "Coolant" curve source becomes
/// <see cref="CurveSource.Hottest"/> with the Hottest curve — a coolant curve (25–38 °C) would
/// pin CPU-driven fans at 100% (see <see cref="OnDeserialized"/>).
/// </remarks>
public sealed class ControlSettings : IJsonOnDeserialized
{
    public ControlMode Mode { get; set; } = ControlMode.Off;
    public int ManualDutyPercent { get; set; } = 50;

    [JsonIgnore]
    public CurveSource Source { get; set; } = CurveSource.Hottest;

    /// <summary>The JSON form of <see cref="Source"/>: spots a legacy "Coolant" (or 0) so its curve is replaced too.</summary>
    [JsonInclude]
    [JsonPropertyName(nameof(Source))]
    private JsonElement SourceJson
    {
        get => JsonSerializer.SerializeToElement(Source.ToString());
        set
        {
            _legacySource = !CurveSourceJsonConverter.TryParse(value, out var source);
            Source = source;
        }
    }

    private bool _legacySource;

    /// <summary>The active curve — always repaired to the shared two-point rules, including when a
    /// settings file written by an older build (4–5 points) is loaded.</summary>
    public List<CurvePoint> CurvePoints
    {
        get => _curvePoints;
        set => _curvePoints = CurveRules.Normalize(value) ?? CurveRules.Default.ToList();
    }

    private List<CurvePoint> _curvePoints = CurveRules.Default.ToList();

    /// <summary>
    /// Curves remembered for the sources that aren't active (key = source name); <see cref="SetSource"/> swaps them.
    /// </summary>
    public Dictionary<string, List<CurvePoint>> SourceCurves { get; set; } = [];

    /// <summary>Migrates a legacy coolant source: Hottest with its remembered (or default) curve.</summary>
    void IJsonOnDeserialized.OnDeserialized() => OnDeserialized();

    private void OnDeserialized()
    {
        if (_legacySource)
        {
            _legacySource = false;
            Source = CurveSource.Hottest;
            CurvePoints = SourceCurves.TryGetValue(nameof(CurveSource.Hottest), out var hottest) && CurveRules.Normalize(hottest) is { } repaired
                ? repaired
                : CurveRules.Default.ToList();
        }

        SourceCurves.Remove("Coolant");
    }

    /// <summary>Switches the curve source, saving the current curve and loading the new source's (or its default).</summary>
    public bool SetSource(CurveSource source)
    {
        if (source == Source)
        {
            return false;
        }

        SourceCurves[Source.ToString()] = CurvePoints.ToList();
        Source = source;
        CurvePoints = SourceCurves.TryGetValue(source.ToString(), out var saved) && CurveRules.Normalize(saved) is { } repaired
            ? repaired
            : CurveRules.Default.ToList();
        return true;
    }

    /// <summary>Validates and stores an edited curve for the active source. False = not a usable curve.</summary>
    public bool SetCurve(IEnumerable<CurvePoint> points)
    {
        if (CurveRules.Normalize(points) is not { } curve)
        {
            return false;
        }

        CurvePoints = curve;
        return true;
    }

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

    /// <summary>Static-color brightness 0–100; scales the RGB value sent to every LED (100 = the chosen color as-is).</summary>
    public int RgbBrightness { get; set; } = 100;

    /// <summary>Static colour or a rainbow cycling along every LED chain (Blackout/Off still paints black).</summary>
    public RgbEffect RgbEffect { get; set; } = RgbEffect.Static;

    /// <summary>Rainbow speed 1 (slow) … 5 (fast); see <see cref="RainbowEffect.CycleSeconds"/>.</summary>
    public int RgbSpeed { get; set; } = RainbowEffect.DefaultSpeed;

    /// <summary>True when the LEDs should show the rainbow right now (enabled, not blacked out).</summary>
    [JsonIgnore]
    public bool RainbowActive => RgbEnabled && !RgbOff && RgbEffect == RgbEffect.Rainbow;

    // ---- Turzx 8.8" serial screen ----

    /// <summary>Turzx 8.8" screen: leave alone, backlight off, a solid background, or live metrics.</summary>
    public LcdMode TurzxScreen { get; set; } = LcdMode.Unmanaged;

    /// <summary>Metric shown on the Turzx screen while <see cref="TurzxScreen"/> is Metrics.</summary>
    [JsonConverter(typeof(LcdMetricJsonConverter))]
    public LcdMetric TurzxMetric { get; set; } = LcdMetric.CpuTemp;

    /// <summary>Turzx backlight brightness 0–100.</summary>
    public int TurzxBrightness { get; set; } = 100;

    /// <summary>Mount orientation: 0 = landscape, 180 = landscape flipped the other way up.</summary>
    public int TurzxRotation { get; set; }

    /// <summary>Movie mode: one switch that blacks out every LED and screen; restores on toggle-off.</summary>
    public bool BlackoutActive { get; set; }
    public bool BlackoutPrevRgbEnabled { get; set; } = true;
    public bool BlackoutPrevRgbOff { get; set; }
    public LcdMode BlackoutPrevTurzx { get; set; } = LcdMode.Unmanaged;

    /// <summary>
    /// Movie mode on/off: on remembers the current lighting/screen state and blacks out every LED
    /// and screen (LEDs are actively painted black, not just released); off restores it.
    /// Returns false when already in that state. Shared by the desktop toggle and the web API.
    /// </summary>
    public bool SetBlackout(bool on)
    {
        if (on == BlackoutActive)
        {
            return false;
        }

        if (on)
        {
            BlackoutPrevRgbEnabled = RgbEnabled;
            BlackoutPrevRgbOff = RgbOff;
            BlackoutPrevTurzx = TurzxScreen;
            RgbEnabled = true; // paint black, don't just stop controlling
            RgbOff = true;
            TurzxScreen = LcdMode.Off;
        }
        else
        {
            RgbEnabled = BlackoutPrevRgbEnabled;
            RgbOff = BlackoutPrevRgbOff;
            TurzxScreen = BlackoutPrevTurzx;
        }

        BlackoutActive = on;
        return true;
    }

    /// <summary>Fleet hub address for the Servers page (#33), e.g. https://devicemaster.&lt;tailnet&gt;.ts.net. Machine-only.</summary>
    public string HubUrl { get; set; } = "";

    /// <summary>Every fan device id ever seen — the expected set behind the "N/N fans" roll-up.</summary>
    public List<string> SeenFanIds { get; set; } = [];

    /// <summary>Every hardware identity ever seen — the expected set behind the device roll-up.</summary>
    public List<string> SeenDeviceIds { get; set; } = [];

    [JsonIgnore]
    public FanCurve Curve => new(CurvePoints);

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
                return FromJson(File.ReadAllText(ConfigPath));
            }
        }
        catch
        {
            // corrupt config -> defaults
        }

        return new ControlSettings();
    }

    /// <summary>Reads settings JSON as <see cref="Load"/> does (throws on malformed JSON).</summary>
    public static ControlSettings FromJson(string json) =>
        JsonSerializer.Deserialize<ControlSettings>(json, JsonOptions) ?? new ControlSettings();

    /// <summary>The settings as <see cref="Save"/> writes them.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, ToJson());
    }
}
