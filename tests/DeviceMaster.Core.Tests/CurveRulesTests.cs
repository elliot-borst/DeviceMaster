using DeviceMaster.App.Headless;
using DeviceMaster.Control;
using DeviceMaster.Core.Curves;

namespace DeviceMaster.Core.Tests;

public class CurveRulesTests
{
    [Fact]
    public void Normalize_TwoPointsSortedWithDutyNeverFalling()
    {
        var curve = CurveRules.Normalize([new(60, 40), new(30, 70)])!;
        Assert.Equal([new CurvePoint(30, 70), new CurvePoint(60, 70)], curve); // second duty lifted to the first
    }

    [Fact]
    public void Normalize_OlderCurvesKeepFirstAndLastPoint()
    {
        var curve = CurveRules.Normalize([new(35, 40), new(45, 55), new(55, 70), new(65, 85), new(70, 100)])!;
        Assert.Equal([new CurvePoint(35, 40), new CurvePoint(70, 100)], curve);
    }

    [Fact]
    public void Normalize_ClampsToTheSharedAxisAndSeparatesPoints()
    {
        Assert.Equal([new CurvePoint(20, 30), new CurvePoint(21, 40)], CurveRules.Normalize([new(5, 30), new(5.2, 40)]));
        Assert.Equal([new CurvePoint(99, 30), new CurvePoint(100, 80)], CurveRules.Normalize([new(300, 30), new(400, 80)]));
        Assert.Null(CurveRules.Normalize([new(30, 50)])); // one point is not a curve
    }

    [Fact]
    public void Defaults_PassTheirOwnRules()
    {
        foreach (var coolant in new[] { true, false })
        {
            var defaults = CurveRules.DefaultFor(coolant);
            Assert.Equal(defaults, CurveRules.Normalize(defaults));
        }
    }

    [Fact]
    public void Settings_RepairAnOlderCurveOnLoad()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<ControlSettings>(
            """{"CurvePoints":[{"TemperatureC":35,"DutyPercent":40},{"TemperatureC":55,"DutyPercent":70},{"TemperatureC":70,"DutyPercent":100}]}""")!;
        Assert.Equal([new CurvePoint(35, 40), new CurvePoint(70, 100)], settings.CurvePoints);
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
        Assert.Equal(["curvePoints=30:40;60:90"], applied); // middle point dropped: first and last kept

        Assert.Empty(ConfigPatcher.ApplyPatch(cfg, """{"curvePoints":[[30,"x"]]}""", out _)); // malformed: ignored
    }
}
