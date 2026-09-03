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
        var applied = ConfigPatcher.ApplyPatch(cfg, """{"fanDuty":150}""", out var invalid);
        Assert.False(invalid);
        Assert.Equal(100, cfg.Control.ManualDutyPercent);

        applied = ConfigPatcher.ApplyPatch(cfg, """{"fanDuty":-20}""", out _);
        Assert.Equal(0, cfg.Control.ManualDutyPercent);
    }

    [Fact]
    public void PumpDutyIsHardFlooredAtSafetyMinimum()
    {
        var cfg = NewConfig();
        var applied = ConfigPatcher.ApplyPatch(cfg, """{"pumpDuty":10}""", out var invalid);
        Assert.False(invalid);
        // SafetyLimits.PumpMinimumDutyPercent = 50 — the patch must never write below it
        Assert.Equal(50, cfg.Control.PumpDutyPercent);
        Assert.Contains("pumpDuty=50", applied);
    }

    [Fact]
    public void RgbChannelsClampedTo0_255()
    {
        var cfg = NewConfig();
        ConfigPatcher.ApplyPatch(cfg, """{"rgbR":300,"rgbG":-1,"rgbB":128}""", out _);
        Assert.Equal(255, cfg.Control.RgbR);
        Assert.Equal(0, cfg.Control.RgbG);
        Assert.Equal(128, cfg.Control.RgbB);
    }

    [Fact]
    public void ModeAndSourceAndLcdEnumsAreValidated()
    {
        var cfg = NewConfig();
        var applied = ConfigPatcher.ApplyPatch(cfg, """{"mode":9,"source":2,"lcdScreens":4,"pumpScreenMetric":7}""", out var invalid);
        Assert.False(invalid);
        Assert.Equal(ControlMode.Curve, cfg.Control.Mode);         // mode 9 rejected, stays headless default
        Assert.Equal(CurveSource.Gpu, cfg.Control.Source);
        Assert.Equal(LcdMode.Metrics, cfg.Control.LcdScreens);
        Assert.Equal(LcdMetric.PumpRpm, cfg.Control.PumpScreenMetric);
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var cfg = NewConfig();
        var applied = ConfigPatcher.ApplyPatch(cfg, """{"bogus":1,"I2cDevice":"x","nvidiaSmiPath":"/bin/false"}""", out var invalid);
        Assert.False(invalid);
        Assert.Empty(applied);
        Assert.Null(cfg.I2cDevice);
    }

    [Fact]
    public void InvalidJsonIsFlagged()
    {
        var cfg = NewConfig();
        var applied = ConfigPatcher.ApplyPatch(cfg, "{not json", out var invalid);
        Assert.True(invalid);
        Assert.Empty(applied);
    }

    [Fact]
    public void NoChangesMeansEmptyApplied()
    {
        var cfg = NewConfig();
        var applied = ConfigPatcher.ApplyPatch(cfg, """{"fanDuty":50,"unknown":3}""", out var invalid);
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
        cfg.Control.PumpDutyPercent = 80;
        var json = cfg.Save();

        var reloaded = HeadlessConfig.Deserialize(json);
        Assert.NotNull(reloaded);
        Assert.Equal("/dev/i2c-10", reloaded.I2cDevice);
        Assert.Equal(80, reloaded.Control.PumpDutyPercent);
    }
}
