using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceMaster.Core.Curves;
using DeviceMaster.Core.Lighting;

namespace DeviceMaster.Control;

/// <summary>
/// The part of <see cref="ControlSettings"/> that a fleet shares (#33): cooling policy, lighting
/// and the Turzx screen. Everything else stays on the machine: Turzx mount rotation, startup
/// behaviour, the seen-device memory. A profile from an older hub may still carry pump and
/// pump/fan LCD fields: they are ignored, and a "Coolant" source reads as Hottest.
/// <para>
/// Lighting is the <i>intended</i> lighting; <see cref="Blackout"/> is a separate switch layered
/// on top. Applying the profile goes through <see cref="ControlSettings.SetBlackout"/>, so a
/// machine that leaves blackout restores the fleet's lighting, not whatever it had before.
/// </para>
/// <para>
/// A profile in the removed Fixed mode (#116: "Mode": "Manual" with ManualDutyPercent, from an
/// older hub or agent, or the hub's saved state) loads as Curve with a flat curve at that duty.
/// </para>
/// </summary>
public sealed record FleetProfile : IJsonOnDeserialized
{
    [JsonIgnore]
    public ControlMode Mode { get; init; } = ControlMode.Curve;

    /// <summary>The JSON form of <see cref="Mode"/>: spots the removed Fixed mode so its duty becomes a flat curve.</summary>
    [JsonInclude]
    [JsonPropertyName(nameof(Mode))]
    private JsonElement ModeJson
    {
        get => JsonSerializer.SerializeToElement(Mode.ToString());
        init => Mode = ControlModeJsonConverter.Read(value, out _legacyFixed);
    }

    private bool _legacyFixed;

    /// <summary>The removed Fixed mode's duty, read from older profiles only (never written).</summary>
    [JsonInclude]
    [JsonPropertyName("ManualDutyPercent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private int? LegacyFixedDuty
    {
        get => null;
        init => _legacyFixedDuty = value;
    }

    private int? _legacyFixedDuty;

    [JsonConverter(typeof(CurveSourceJsonConverter))]
    public CurveSource Source { get; init; } = CurveSource.Hottest;

    public IReadOnlyList<CurvePoint> CurvePoints
    {
        get => _curvePoints;
        init => _curvePoints = value;
    }

    private IReadOnlyList<CurvePoint> _curvePoints = CurveRules.Default;

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (_legacyFixed)
        {
            _curvePoints = CurveRules.Flat(_legacyFixedDuty ?? ControlSettings.LegacyFixedDefaultDuty, _curvePoints);
        }

        _legacyFixed = false;
        _legacyFixedDuty = null;
    }

    public bool RgbEnabled { get; init; } = true;
    public bool RgbOff { get; init; }
    public int RgbR { get; init; } = 86;
    public int RgbG { get; init; } = 130;
    public int RgbB { get; init; } = 255;
    public int RgbBrightness { get; init; } = 100;
    public RgbEffect RgbEffect { get; init; } = RgbEffect.Static;
    public int RgbSpeed { get; init; } = RainbowEffect.DefaultSpeed;

    public LcdMode TurzxScreen { get; init; } = LcdMode.Unmanaged;

    [JsonConverter(typeof(LcdMetricJsonConverter))]
    public LcdMetric TurzxMetric { get; init; } = LcdMetric.CpuTemp;
    public int TurzxBrightness { get; init; } = 100;

    /// <summary>Movie mode: every LED black and the screen off, on top of the intended state above.</summary>
    public bool Blackout { get; init; }

    /// <summary>The fleet-shared part of a machine's settings (its intended lighting/screens if it is blacked out).</summary>
    public static FleetProfile From(ControlSettings s) => new()
    {
        Mode = s.Mode,
        Source = s.Source,
        CurvePoints = s.CurvePoints.ToList(),
        RgbEnabled = s.BlackoutActive ? s.BlackoutPrevRgbEnabled : s.RgbEnabled,
        RgbOff = s.BlackoutActive ? s.BlackoutPrevRgbOff : s.RgbOff,
        RgbR = s.RgbR,
        RgbG = s.RgbG,
        RgbB = s.RgbB,
        RgbBrightness = s.RgbBrightness,
        RgbEffect = s.RgbEffect,
        RgbSpeed = s.RgbSpeed,
        TurzxScreen = s.BlackoutActive ? s.BlackoutPrevTurzx : s.TurzxScreen,
        TurzxMetric = s.TurzxMetric,
        TurzxBrightness = s.TurzxBrightness,
        Blackout = s.BlackoutActive,
    };

    /// <summary>
    /// Writes this profile into a machine's settings, clamped by the same rules as the local
    /// editors (duty and colour ranges, curve repair), leaving machine-only fields alone.
    /// Returns true when anything changed.
    /// </summary>
    public bool ApplyTo(ControlSettings s)
    {
        var before = From(s);

        // leave blackout first so the intended state lands in the live fields, then re-enter
        s.SetBlackout(false);

        s.Mode = Enum.IsDefined(Mode) ? Mode : ControlMode.Curve;
        if (Enum.IsDefined(Source))
        {
            s.SetSource(Source);
        }

        s.SetCurve(CurvePoints); // rejected (kept) when not a usable curve

        s.RgbEnabled = RgbEnabled;
        s.RgbOff = RgbOff;
        s.RgbR = Math.Clamp(RgbR, 0, 255);
        s.RgbG = Math.Clamp(RgbG, 0, 255);
        s.RgbB = Math.Clamp(RgbB, 0, 255);
        s.RgbBrightness = Math.Clamp(RgbBrightness, 0, 100);
        s.RgbEffect = Enum.IsDefined(RgbEffect) ? RgbEffect : RgbEffect.Static;
        s.RgbSpeed = Math.Clamp(RgbSpeed, RainbowEffect.MinSpeed, RainbowEffect.MaxSpeed);

        s.TurzxScreen = Enum.IsDefined(TurzxScreen) ? TurzxScreen : LcdMode.Unmanaged;
        s.TurzxMetric = Enum.IsDefined(TurzxMetric) ? TurzxMetric : LcdMetric.CpuTemp;
        s.TurzxBrightness = Math.Clamp(TurzxBrightness, 0, 100);

        s.SetBlackout(Blackout);
        return From(s) != before;
    }

    /// <summary>The profile with a machine's overrides laid on top (null fields inherit).</summary>
    public FleetProfile With(FleetOverride? o) => o is null ? this : this with
    {
        Mode = o.Mode ?? Mode,
        Source = o.Source ?? Source,
        CurvePoints = o.CurvePoints ?? CurvePoints,
        RgbEnabled = o.RgbEnabled ?? RgbEnabled,
        RgbOff = o.RgbOff ?? RgbOff,
        RgbR = o.RgbR ?? RgbR,
        RgbG = o.RgbG ?? RgbG,
        RgbB = o.RgbB ?? RgbB,
        RgbBrightness = o.RgbBrightness ?? RgbBrightness,
        RgbEffect = o.RgbEffect ?? RgbEffect,
        RgbSpeed = o.RgbSpeed ?? RgbSpeed,

        TurzxScreen = o.TurzxScreen ?? TurzxScreen,
        TurzxMetric = o.TurzxMetric ?? TurzxMetric,
        TurzxBrightness = o.TurzxBrightness ?? TurzxBrightness,
        Blackout = o.Blackout ?? Blackout,
    };

    // records compare collections by reference: compare everything, the curve included, by value
    public bool Equals(FleetProfile? other) => other is not null && Flat() == other.Flat();

    public override int GetHashCode() => Flat().GetHashCode();

    private string Flat() =>
        $"{Mode}|{Source}|{string.Join(";", CurvePoints)}|{RgbEnabled}|{RgbOff}|{RgbR},{RgbG},{RgbB}|"
        + $"{RgbBrightness}|{RgbEffect}|{RgbSpeed}|{TurzxScreen}|{TurzxMetric}|{TurzxBrightness}|{Blackout}";
}

/// <summary>
/// A machine's exceptions to the fleet profile: every field is optional, null = inherit. An override
/// saved in the removed Fixed mode (#116) loads as Curve with a flat curve at its fixed duty.
/// </summary>
public sealed record FleetOverride : IJsonOnDeserialized
{
    [JsonIgnore]
    public ControlMode? Mode { get; init; }

    /// <summary>The JSON form of <see cref="Mode"/>: spots the removed Fixed mode so its duty becomes a flat curve.</summary>
    [JsonInclude]
    [JsonPropertyName(nameof(Mode))]
    private JsonElement? ModeJson
    {
        get => Mode is { } mode ? JsonSerializer.SerializeToElement(mode.ToString()) : null;
        init => Mode = value is { ValueKind: not JsonValueKind.Null } element ? ControlModeJsonConverter.Read(element, out _legacyFixed) : null;
    }

    private bool _legacyFixed;

    /// <summary>The removed Fixed mode's duty, read from older state only (never written).</summary>
    [JsonInclude]
    [JsonPropertyName("ManualDutyPercent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private int? LegacyFixedDuty
    {
        get => null;
        init => _legacyFixedDuty = value;
    }

    private int? _legacyFixedDuty;

    [JsonConverter(typeof(CurveSourceJsonConverter))]
    public CurveSource? Source { get; init; }

    public IReadOnlyList<CurvePoint>? CurvePoints
    {
        get => _curvePoints;
        init => _curvePoints = value;
    }

    private IReadOnlyList<CurvePoint>? _curvePoints;

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (_legacyFixed)
        {
            _curvePoints = CurveRules.Flat(_legacyFixedDuty ?? ControlSettings.LegacyFixedDefaultDuty, _curvePoints);
        }

        _legacyFixed = false;
        _legacyFixedDuty = null;
    }
    public bool? RgbEnabled { get; init; }
    public bool? RgbOff { get; init; }
    public int? RgbR { get; init; }
    public int? RgbG { get; init; }
    public int? RgbB { get; init; }
    public int? RgbBrightness { get; init; }
    public RgbEffect? RgbEffect { get; init; }
    public int? RgbSpeed { get; init; }
    public LcdMode? TurzxScreen { get; init; }

    [JsonConverter(typeof(LcdMetricJsonConverter))]
    public LcdMetric? TurzxMetric { get; init; }
    public int? TurzxBrightness { get; init; }
    public bool? Blackout { get; init; }
}
