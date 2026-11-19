using DeviceMaster.App.Headless;
using DeviceMaster.Control;
using Xunit;

namespace DeviceMaster.Core.Tests;

public class ConfigPatcherTests
{
    private static HeadlessConfig NewConfig() => new();

    [Fact]
    public void SetsFanDutyClampedToRange()
    {
        var cfg = NewConfig();
        var applied = ConfigPatcher.ApplyPatch(cfg.Control, """{"fanDuty":150}""", out var invalid);
        Assert.False(invalid);
        Assert.Equal(100, cfg.Control.ManualDutyPercent);

        applied = ConfigPatcher.ApplyPatch(cfg.Control, """{"fanDuty":-20}""", out _);
        Assert.Equal(0, cfg.Control.ManualDutyPercent);
    }

    [Fact]
    public void RemovedPumpAndLcdFieldsAreIgnored()
    {
        // an older web page may still send these: pump and pump/fan LCD control is gone
        var cfg = NewConfig();
        var applied = ConfigPatcher.ApplyPatch(cfg.Control,
            """{"pumpDuty":10,"lcdScreens":4,"lcdBrightness":20,"pumpScreenMetric":7}""", out var invalid);
        Assert.False(invalid);
        Assert.Empty(applied);
    }

    [Fact]
    public void RgbChannelsClampedTo0_255()
    {
        var cfg = NewConfig();
        ConfigPatcher.ApplyPatch(cfg.Control, """{"rgbR":300,"rgbG":-1,"rgbB":128}""", out _);
        Assert.Equal(255, cfg.Control.RgbR);
        Assert.Equal(0, cfg.Control.RgbG);
        Assert.Equal(128, cfg.Control.RgbB);
    }

    [Fact]
    public void ModeAndSourceEnumsAreValidated()
    {
        var cfg = NewConfig();
        var applied = ConfigPatcher.ApplyPatch(cfg.Control, """{"mode":9,"source":2}""", out var invalid);
        Assert.False(invalid);
        Assert.Equal(ControlMode.Curve, cfg.Control.Mode);         // mode 9 rejected, stays headless default
        Assert.Equal(CurveSource.Gpu, cfg.Control.Source);

        // 0 was the removed coolant source: rejected, the source stays as it was
        Assert.Empty(ConfigPatcher.ApplyPatch(cfg.Control, """{"source":0}""", out _));
        Assert.Equal(CurveSource.Gpu, cfg.Control.Source);
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var cfg = NewConfig();
        var applied = ConfigPatcher.ApplyPatch(cfg.Control, """{"bogus":1,"I2cDevice":"x","nvidiaSmiPath":"/bin/false"}""", out var invalid);
        Assert.False(invalid);
        Assert.Empty(applied);
        Assert.Null(cfg.I2cDevice);
    }

    [Fact]
    public void InvalidJsonIsFlagged()
    {
        var cfg = NewConfig();
        var applied = ConfigPatcher.ApplyPatch(cfg.Control, "{not json", out var invalid);
        Assert.True(invalid);
        Assert.Empty(applied);
    }

    [Fact]
    public void NoChangesMeansEmptyApplied()
    {
        var cfg = NewConfig();
        var applied = ConfigPatcher.ApplyPatch(cfg.Control, """{"fanDuty":50,"unknown":3}""", out var invalid);
        Assert.False(invalid);
        // 50 == default ManualDutyPercent — still reported as applied (explicit user intent),
        // the web server only skips persistence when the applied list is empty
        Assert.Single(applied);
    }

    [Fact]
    public void RoundTripPreservesNonControlFields()
    {
        var cfg = NewConfig();
        cfg.I2cDevice = "/dev/i2c-10";
        cfg.GpuSensorFile = "/var/run/gpu-sensors.jsonl";
        cfg.Control.ManualDutyPercent = 80;
        cfg.Control.Source = CurveSource.Cpu;
        var json = cfg.Save();

        var reloaded = HeadlessConfig.Deserialize(json);
        Assert.NotNull(reloaded);
        Assert.Equal("/dev/i2c-10", reloaded.I2cDevice);
        Assert.Equal(80, reloaded.Control.ManualDutyPercent);
        Assert.Equal(CurveSource.Cpu, reloaded.Control.Source);
    }

    [Fact]
    public void BlackoutRemembersAndRestoresLightingAndScreens()
    {
        var cfg = NewConfig();
        cfg.Control.RgbEnabled = true;
        cfg.Control.RgbOff = false;
        cfg.Control.TurzxScreen = LcdMode.Metrics;

        var applied = ConfigPatcher.ApplyPatch(cfg.Control, """{"blackout":true}""", out _);
        Assert.Equal(["blackout=True"], applied);
        Assert.True(cfg.Control.RgbOff);
        Assert.Equal(LcdMode.Off, cfg.Control.TurzxScreen);

        Assert.Empty(ConfigPatcher.ApplyPatch(cfg.Control, """{"blackout":true}""", out _)); // already on: no change

        ConfigPatcher.ApplyPatch(cfg.Control, """{"blackout":false}""", out _);
        Assert.False(cfg.Control.BlackoutActive);
        Assert.False(cfg.Control.RgbOff);
        Assert.Equal(LcdMode.Metrics, cfg.Control.TurzxScreen);
    }

    [Fact]
    public void RgbOffTurnsLightsOutOnAndOff()
    {
        var cfg = NewConfig();
        cfg.Control.RgbOff = true;

        var applied = ConfigPatcher.ApplyPatch(cfg.Control, """{"rgbOff":false,"rgbR":150,"rgbG":0,"rgbB":255,"rgbBrightness":100}""", out var invalid);
        Assert.False(invalid);
        Assert.Contains("rgbOff=False", applied);
        Assert.False(cfg.Control.RgbOff);

        ConfigPatcher.ApplyPatch(cfg.Control, """{"rgbOff":true}""", out _);
        Assert.True(cfg.Control.RgbOff);
        Assert.Empty(ConfigPatcher.ApplyPatch(cfg.Control, """{"rgbOff":"no"}""", out _)); // not a bool: ignored
    }

    [Fact]
    public void RgbOffDuringBlackoutSetsWhatBlackoutRestores()
    {
        var cfg = NewConfig();
        cfg.Control.RgbOff = true;
        ConfigPatcher.ApplyPatch(cfg.Control, """{"blackout":true}""", out _);

        ConfigPatcher.ApplyPatch(cfg.Control, """{"rgbOff":false}""", out _);
        Assert.True(cfg.Control.RgbOff); // still blacked out

        ConfigPatcher.ApplyPatch(cfg.Control, """{"blackout":false}""", out _);
        Assert.False(cfg.Control.RgbOff); // lights come back on
    }

    [Fact]
    public void RainbowEffectAndSpeed()
    {
        var cfg = NewConfig();
        cfg.Control.RgbOff = true;
        var applied = ConfigPatcher.ApplyPatch(cfg.Control, """{"rgbEffect":1,"rgbSpeed":9}""", out _);
        Assert.Equal(["rgbEffect=1", "rgbSpeed=5"], applied); // speed clamped to 1–5
        Assert.True(cfg.Control.RainbowActive);                // picking rainbow clears lights-out
        Assert.Empty(ConfigPatcher.ApplyPatch(cfg.Control, """{"rgbEffect":7}""", out _)); // unknown effect ignored
    }
}
