using DeviceMaster.Core.Curves;
using DeviceMaster.Core.Lighting;
using DeviceMaster.Core.Safety;

namespace DeviceMaster.Control;

/// <summary>
/// The part of <see cref="ControlSettings"/> that a fleet shares (#33): cooling policy, lighting
/// and screen modes. Everything else stays on the machine: which screens exist and how each one
/// is set up, Turzx mount rotation, startup behaviour, the seen-device memory.
/// <para>
/// Lighting is the <i>intended</i> lighting; <see cref="Blackout"/> is a separate switch layered
/// on top. Applying the profile goes through <see cref="ControlSettings.SetBlackout"/>, so a
/// machine that leaves blackout restores the fleet's lighting, not whatever it had before.
/// </para>
/// </summary>
public sealed record FleetProfile
{
    public ControlMode Mode { get; init; } = ControlMode.Curve;
    public int ManualDutyPercent { get; init; } = 50;
    public int PumpDutyPercent { get; init; } = 100;
    public CurveSource Source { get; init; } = CurveSource.Hottest;
    public IReadOnlyList<CurvePoint> CurvePoints { get; init; } = CurveRules.DefaultFor(coolant: false);

    public bool RgbEnabled { get; init; } = true;
    public bool RgbOff { get; init; }
    public int RgbR { get; init; } = 86;
    public int RgbG { get; init; } = 130;
    public int RgbB { get; init; } = 255;
    public int RgbBrightness { get; init; } = 100;
    public RgbEffect RgbEffect { get; init; } = RgbEffect.Static;
    public int RgbSpeed { get; init; } = RainbowEffect.DefaultSpeed;

    public LcdMode LcdScreens { get; init; } = LcdMode.Unmanaged;
    public int LcdBrightness { get; init; } = 100;
    public LcdMetric PumpScreenMetric { get; init; } = LcdMetric.Coolant;
    public LcdMetric FanScreenMetric { get; init; } = LcdMetric.CpuTemp;
    public LcdMode TurzxScreen { get; init; } = LcdMode.Unmanaged;
    public LcdMetric TurzxMetric { get; init; } = LcdMetric.CpuTemp;
    public int TurzxBrightness { get; init; } = 100;

    /// <summary>Movie mode: every LED black and every screen off, on top of the intended state above.</summary>
    public bool Blackout { get; init; }

    /// <summary>The fleet-shared part of a machine's settings (its intended lighting/screens if it is blacked out).</summary>
    public static FleetProfile From(ControlSettings s) => new()
    {
        Mode = s.Mode,
        ManualDutyPercent = s.ManualDutyPercent,
        PumpDutyPercent = s.PumpDutyPercent,
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
        LcdScreens = s.BlackoutActive ? s.BlackoutPrevLcd : s.LcdScreens,
        LcdBrightness = s.LcdBrightness,
        PumpScreenMetric = s.PumpScreenMetric,
        FanScreenMetric = s.FanScreenMetric,
        TurzxScreen = s.BlackoutActive ? s.BlackoutPrevTurzx : s.TurzxScreen,
        TurzxMetric = s.TurzxMetric,
        TurzxBrightness = s.TurzxBrightness,
        Blackout = s.BlackoutActive,
    };

    /// <summary>
    /// Writes this profile into a machine's settings, clamped by the same rules as the local
    /// editors (pump floor, duty and colour ranges, curve repair), leaving machine-only fields alone.
    /// Returns true when anything changed.
    /// </summary>
    public bool ApplyTo(ControlSettings s)
    {
        var before = From(s);

        // leave blackout first so the intended state lands in the live fields, then re-enter
        s.SetBlackout(false);

        s.Mode = Enum.IsDefined(Mode) ? Mode : ControlMode.Curve;
        s.ManualDutyPercent = Math.Clamp(ManualDutyPercent, 0, 100); // the write layer applies the fan floor
        s.PumpDutyPercent = Math.Clamp(PumpDutyPercent, SafetyLimits.PumpMinimumDutyPercent, 100);
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

        s.LcdScreens = Enum.IsDefined(LcdScreens) ? LcdScreens : LcdMode.Unmanaged;
        s.LcdBrightness = Math.Clamp(LcdBrightness, 0, 100);
        s.PumpScreenMetric = Enum.IsDefined(PumpScreenMetric) ? PumpScreenMetric : LcdMetric.Coolant;
        s.FanScreenMetric = Enum.IsDefined(FanScreenMetric) ? FanScreenMetric : LcdMetric.CpuTemp;
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
        ManualDutyPercent = o.ManualDutyPercent ?? ManualDutyPercent,
        PumpDutyPercent = o.PumpDutyPercent ?? PumpDutyPercent,
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
        LcdScreens = o.LcdScreens ?? LcdScreens,
        LcdBrightness = o.LcdBrightness ?? LcdBrightness,
        PumpScreenMetric = o.PumpScreenMetric ?? PumpScreenMetric,
        FanScreenMetric = o.FanScreenMetric ?? FanScreenMetric,
        TurzxScreen = o.TurzxScreen ?? TurzxScreen,
        TurzxMetric = o.TurzxMetric ?? TurzxMetric,
        TurzxBrightness = o.TurzxBrightness ?? TurzxBrightness,
        Blackout = o.Blackout ?? Blackout,
    };

    // records compare collections by reference: compare everything, the curve included, by value
    public bool Equals(FleetProfile? other) => other is not null && Flat() == other.Flat();

    public override int GetHashCode() => Flat().GetHashCode();

    private string Flat() =>
        $"{Mode}|{ManualDutyPercent}|{PumpDutyPercent}|{Source}|{string.Join(";", CurvePoints)}|{RgbEnabled}|{RgbOff}|{RgbR},{RgbG},{RgbB}|"
        + $"{RgbBrightness}|{RgbEffect}|{RgbSpeed}|{LcdScreens}|{LcdBrightness}|{PumpScreenMetric}|{FanScreenMetric}|"
        + $"{TurzxScreen}|{TurzxMetric}|{TurzxBrightness}|{Blackout}";
}

/// <summary>A machine's exceptions to the fleet profile: every field is optional, null = inherit.</summary>
public sealed record FleetOverride
{
    public ControlMode? Mode { get; init; }
    public int? ManualDutyPercent { get; init; }
    public int? PumpDutyPercent { get; init; }
    public CurveSource? Source { get; init; }
    public IReadOnlyList<CurvePoint>? CurvePoints { get; init; }
    public bool? RgbEnabled { get; init; }
    public bool? RgbOff { get; init; }
    public int? RgbR { get; init; }
    public int? RgbG { get; init; }
    public int? RgbB { get; init; }
    public int? RgbBrightness { get; init; }
    public RgbEffect? RgbEffect { get; init; }
    public int? RgbSpeed { get; init; }
    public LcdMode? LcdScreens { get; init; }
    public int? LcdBrightness { get; init; }
    public LcdMetric? PumpScreenMetric { get; init; }
    public LcdMetric? FanScreenMetric { get; init; }
    public LcdMode? TurzxScreen { get; init; }
    public LcdMetric? TurzxMetric { get; init; }
    public int? TurzxBrightness { get; init; }
    public bool? Blackout { get; init; }
}
