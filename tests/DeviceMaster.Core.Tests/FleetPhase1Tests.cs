using DeviceMaster.App.Headless;
using DeviceMaster.Control;
using DeviceMaster.Core.Curves;
using DeviceMaster.Core.Lighting;
using DeviceMaster.Core.Safety;

namespace DeviceMaster.Core.Tests;

public class HottestTemperatureTests
{
    [Theory]
    [InlineData(50.0, 70.0, true, 70.0)]   // GPU hotter
    [InlineData(80.0, 40.0, true, 80.0)]   // CPU hotter
    [InlineData(55.0, null, false, 55.0)]  // no GPU on this machine: CPU alone
    public void UsesTheHotterReading(double? cpu, double? gpu, bool gpuExpected, double expected) =>
        Assert.Equal(expected, HottestTemperature.Combine(cpu, gpu, gpuExpected));

    [Fact]
    public void CpuFailureIsFailsafe() => Assert.Null(HottestTemperature.Combine(null, 60, gpuExpected: true));

    [Fact]
    public void ImplausibleCpuIsFailsafe() => Assert.Null(HottestTemperature.Combine(0, 60, gpuExpected: true));

    [Fact]
    public void GpuFailureOnAMachineWithAGpuIsFailsafeNotCpuOnly() =>
        Assert.Null(HottestTemperature.Combine(45, null, gpuExpected: true));
}

public class TemperatureSmootherTests
{
    [Fact]
    public void FirstReadingPassesStraightThrough() => Assert.Equal(50, new TemperatureSmoother().Next(50, 0));

    [Fact]
    public void ABriefSpikeIsDamped()
    {
        var s = new TemperatureSmoother();
        s.Next(50, 0);
        var afterOneSecond = s.Next(80, 1_000)!.Value;
        Assert.InRange(afterOneSecond, 55, 65); // ~28 % of the way with a 3 s rise constant
    }

    [Fact]
    public void ASustainedLoadIsFollowedWithinSeconds()
    {
        var s = new TemperatureSmoother();
        s.Next(50, 0);
        double? v = null;
        for (var t = 1; t <= 10; t++)
        {
            v = s.Next(80, t * 1_000);
        }

        Assert.InRange(v!.Value, 78, 80);
    }

    [Fact]
    public void FallsSlowerThanItRises()
    {
        var rise = new TemperatureSmoother();
        rise.Next(50, 0);
        var up = rise.Next(70, 2_000)!.Value - 50;

        var fall = new TemperatureSmoother();
        fall.Next(70, 0);
        var down = 70 - fall.Next(50, 2_000)!.Value;

        Assert.True(up > down * 2);
    }

    [Fact]
    public void AnUnusableReadingIsNeverSmoothedAway()
    {
        var s = new TemperatureSmoother();
        s.Next(60, 0);
        Assert.Null(s.Next(null, 1_000));
        Assert.Equal(40, s.Next(40, 2_000)); // starts fresh, no memory of the old value
    }
}

public class FleetProfileTests
{
    [Fact]
    public void RoundTripsThroughSettings()
    {
        var profile = new FleetProfile
        {
            Mode = ControlMode.Curve,
            Source = CurveSource.Hottest,
            CurvePoints = [new(35, 25), new(80, 90)],
            RgbEffect = RgbEffect.Rainbow,
            RgbSpeed = 4,
            RgbR = 10,
            TurzxScreen = LcdMode.Metrics,
        };

        var settings = new ControlSettings();
        Assert.True(profile.ApplyTo(settings));
        Assert.Equal(profile, FleetProfile.From(settings));
        Assert.False(profile.ApplyTo(settings)); // nothing left to change
    }

    [Fact]
    public void LeavesMachineOnlySettingsAlone()
    {
        var settings = new ControlSettings { TurzxRotation = 180, StartWithWindows = false, SeenFanIds = ["a"] };
        settings.ScreenConfig("pump-lcd", isPump: true).RotationDegrees = 90;

        new FleetProfile { TurzxScreen = LcdMode.Metrics }.ApplyTo(settings);

        Assert.Equal(180, settings.TurzxRotation);
        Assert.False(settings.StartWithWindows);
        Assert.Equal(["a"], settings.SeenFanIds);
        Assert.Equal(90, settings.ScreenConfig("pump-lcd", isPump: true).RotationDegrees);
    }

    [Fact]
    public void PumpDutyNeverBelowTheFloor()
    {
        var settings = new ControlSettings();
        new FleetProfile { PumpDutyPercent = 10 }.ApplyTo(settings);
        Assert.Equal(SafetyLimits.PumpMinimumDutyPercent, settings.PumpDutyPercent);
    }

    [Fact]
    public void ABrokenCurveIsRejected()
    {
        var settings = new ControlSettings();
        var before = settings.CurvePoints.ToList();
        new FleetProfile { Source = settings.Source, CurvePoints = [new(50, 50)] }.ApplyTo(settings);
        Assert.Equal(before, settings.CurvePoints);
    }

    [Fact]
    public void BlackoutKeepsTheIntendedLightingUnderneath()
    {
        var settings = new ControlSettings();
        new FleetProfile { RgbEnabled = true, RgbOff = false, LcdScreens = LcdMode.Metrics, Blackout = true }.ApplyTo(settings);

        Assert.True(settings.BlackoutActive);
        Assert.True(settings.RgbOff); // painting black now
        Assert.Equal(LcdMode.Off, settings.LcdScreens);

        var seen = FleetProfile.From(settings); // reports the intended state, not the blackout
        Assert.False(seen.RgbOff);
        Assert.Equal(LcdMode.Metrics, seen.LcdScreens);
        Assert.True(seen.Blackout);

        new FleetProfile { RgbEnabled = true, RgbOff = false, LcdScreens = LcdMode.Metrics, Blackout = false }.ApplyTo(settings);
        Assert.False(settings.RgbOff);
        Assert.Equal(LcdMode.Metrics, settings.LcdScreens);
    }

    [Fact]
    public void OverridesReplaceOnlyTheirFields()
    {
        var fleet = new FleetProfile { RgbR = 255, RgbG = 0, RgbB = 0, RgbEffect = RgbEffect.Rainbow, Source = CurveSource.Hottest };
        var mine = fleet.With(new FleetOverride { RgbEffect = RgbEffect.Static });

        Assert.Equal(RgbEffect.Static, mine.RgbEffect);
        Assert.Equal(255, mine.RgbR);
        Assert.Equal(CurveSource.Hottest, mine.Source);
        Assert.Equal(fleet, fleet.With(null));
    }

    [Fact]
    public void TheHottestSourceGetsTheDieTemperatureCurve()
    {
        var settings = new ControlSettings(); // coolant by default
        settings.SetSource(CurveSource.Hottest);
        Assert.Equal(CurveRules.DefaultFor(coolant: false), settings.CurvePoints);
    }

    [Fact]
    public void TheWebApiAcceptsTheHottestSource()
    {
        var cfg = new HeadlessConfig();
        var applied = ConfigPatcher.ApplyPatch(cfg, """{"source":3}""", out var invalid);
        Assert.False(invalid);
        Assert.NotEmpty(applied);
        Assert.Equal(CurveSource.Hottest, cfg.Control.Source);
    }
}
