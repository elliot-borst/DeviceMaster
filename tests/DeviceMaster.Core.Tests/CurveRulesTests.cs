using DeviceMaster.App.Headless;
using DeviceMaster.Control;
using DeviceMaster.Core.Curves;

namespace DeviceMaster.Core.Tests;

public class CurveRulesTests
{
    [Fact]
    public void Normalize_SortsKeepsDutyRisingAndEndsAtFullSpeed()
    {
        var curve = CurveRules.Normalize([new(40, 60), new(30, 50), new(35, 20)], coolant: true)!;
        Assert.Equal([30.0, 35.0, 40.0], curve.Select(p => p.TemperatureC));
        Assert.Equal([50, 50, 100], curve.Select(p => p.DutyPercent)); // 20 lifted to 50; hottest forced to 100
    }

    [Fact]
    public void Normalize_ClampsToTheSourceRangeAndSeparatesPoints()
    {
        var curve = CurveRules.Normalize([new(5, 30), new(5.2, 40), new(300, 80)], coolant: true)!;
        Assert.Equal([20.0, 21.0, 70.0], curve.Select(p => p.TemperatureC));
        Assert.Null(CurveRules.Normalize([new(30, 50)], coolant: true));                  // one point is not a curve
        Assert.Null(CurveRules.Normalize(Enumerable.Range(0, 9).Select(i => new CurvePoint(30 + i, 50)), coolant: false));
    }

    [Fact]
    public void Defaults_PassTheirOwnRules()
    {
        foreach (var coolant in new[] { true, false })
        {
            var defaults = CurveRules.DefaultFor(coolant);
            Assert.Equal(defaults, CurveRules.Normalize(defaults, coolant));
        }
    }

    [Fact]
    public void SetSource_KeepsACurvePerSource()
    {
        var settings = new ControlSettings { Source = CurveSource.Coolant };
        settings.SetCurve([new(25, 35), new(40, 100)]);
        settings.SetSource(CurveSource.Cpu);
        Assert.Equal(CurveRules.DefaultFor(coolant: false), settings.CurvePoints);   // first visit: CPU default
        settings.SetSource(CurveSource.Coolant);
        Assert.Equal([new CurvePoint(25, 35), new CurvePoint(40, 100)], settings.CurvePoints); // edits came back
    }

    [Fact]
    public void Patch_AcceptsAndRepairsCurvePoints()
    {
        var cfg = new HeadlessConfig();
        cfg.Control.Source = CurveSource.Coolant;
        var applied = ConfigPatcher.ApplyPatch(cfg, """{"curvePoints":[[30,40],[45,30],[60,90]]}""", out var invalid);
        Assert.False(invalid);
        Assert.Equal(["curvePoints=30:40;45:40;60:100"], applied);

        Assert.Empty(ConfigPatcher.ApplyPatch(cfg, """{"curvePoints":[[30,"x"]]}""", out _)); // malformed: ignored
    }
}
