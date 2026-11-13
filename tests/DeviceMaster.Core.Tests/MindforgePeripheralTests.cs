using DeviceMaster.Devices.Turzx;
using DeviceMaster.Lcd.Skia;
using DeviceMaster.Sensors.Linux;

namespace DeviceMaster.Core.Tests;

public class HwmonFanControlTests : IDisposable
{
    private readonly string _sys = Path.Combine(Path.GetTempPath(), $"dm-sys-{Guid.NewGuid():N}");

    private string Chip(string name, params (int Index, int Rpm, int Enable)[] headers)
    {
        var dir = Path.Combine(_sys, "class", "hwmon", "hwmon9");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "name"), name + "\n");
        foreach (var (i, rpm, enable) in headers)
        {
            File.WriteAllText(Path.Combine(dir, $"pwm{i}"), "164\n");
            File.WriteAllText(Path.Combine(dir, $"pwm{i}_enable"), $"{enable}\n");
            File.WriteAllText(Path.Combine(dir, $"fan{i}_input"), $"{rpm}\n");
        }

        return dir;
    }

    [Fact]
    public void ControlsOnlySpinningHeadersAndNeverBelowTheFloor()
    {
        var dir = Chip("nct6799", (1, 0, 5), (2, 656, 5));
        using var fans = HwmonFanControl.Open(_sys)!;
        Assert.Equal([2], fans.Controlled);

        fans.Apply(10);
        Assert.Equal("1", File.ReadAllText(Path.Combine(dir, "pwm2_enable")));
        Assert.Equal("77", File.ReadAllText(Path.Combine(dir, "pwm2")));   // 30 % floor = 77/255
        Assert.Equal("5\n", File.ReadAllText(Path.Combine(dir, "pwm1_enable"))); // empty header untouched
    }

    [Fact]
    public void ReleaseRestoresTheBiosMode()
    {
        var dir = Chip("nct6799", (2, 656, 5));
        var fans = HwmonFanControl.Open(_sys)!;
        fans.Apply(100);
        Assert.Equal("255", File.ReadAllText(Path.Combine(dir, "pwm2")));
        fans.Release();
        Assert.Equal("5", File.ReadAllText(Path.Combine(dir, "pwm2_enable")));
    }

    private static void Lock(string file) => File.SetAttributes(file, FileAttributes.ReadOnly); // writes now fail, like EIO

    [Fact]
    public void AHeaderThatRejectsItsDutyIsHandedBackAndDropped()
    {
        var dir = Chip("nct6687", (1, 1800, 2), (3, 1600, 2));
        var log = new List<string>();
        var fans = HwmonFanControl.Open(_sys, log.Add)!;
        Lock(Path.Combine(dir, "pwm3"));

        for (var i = 0; i < HwmonFanControl.FailuresBeforeDrop; i++)
        {
            Assert.Single(fans.Apply(60, refresh: true), w => w.StartsWith("pwm3:"));
        }

        Assert.Equal([1], fans.Controlled);
        Assert.Contains(3, fans.Dropped.Keys);
        Assert.Equal("2", File.ReadAllText(Path.Combine(dir, "pwm3_enable"))); // BIOS curve again
        Assert.Contains(log, l => l.Contains("pwm3 does not take a duty"));
        Assert.Equal("1", File.ReadAllText(Path.Combine(dir, "pwm1_enable"))); // the good header carries on
    }

    [Fact]
    public void AFailedHandbackIsReportedAndLeftAtFullSpeed()
    {
        var dir = Chip("nct6687", (1, 1800, 2));
        var fans = HwmonFanControl.Open(_sys)!;
        fans.Apply(40);
        Lock(Path.Combine(dir, "pwm1_enable"));

        var problems = fans.Release();

        Assert.Single(problems, p => p.Contains("pwm1 could not be handed back") && p.Contains("full speed"));
        Assert.Equal("255", File.ReadAllText(Path.Combine(dir, "pwm1"))); // never stuck at the last curve duty
    }

    [Fact]
    public void NoSuperIoChipMeansNoController()
    {
        Chip("k10temp");
        Assert.Null(HwmonFanControl.Open(_sys));
    }

    public void Dispose()
    {
        if (Directory.Exists(_sys))
        {
            foreach (var file in Directory.EnumerateFiles(_sys, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_sys, recursive: true);
        }
    }
}

public class TurzxLinuxTests : IDisposable
{
    private readonly string _sys = Path.Combine(Path.GetTempPath(), $"dm-tty-{Guid.NewGuid():N}");

    private void Port(string name, string vid, string pid)
    {
        var device = Path.Combine(_sys, "class", "tty", name, "device");
        Directory.CreateDirectory(device);
        File.WriteAllText(Path.Combine(device, "idVendor"), vid + "\n");
        File.WriteAllText(Path.Combine(device, "idProduct"), pid + "\n");
    }

    [Fact]
    public void FindsTheDataPortOnlyBesideTheTurzxControlPort()
    {
        Port("ttyACM0", "1a86", "ca88");
        Port("ttyACM1", "0525", "a4a7");
        Assert.Equal("/dev/ttyACM1", TurzxScreen.FindLinuxDataPort(_sys));
    }

    [Fact]
    public void AGadgetSerialPortAloneIsNotAScreen()
    {
        Port("ttyACM0", "0525", "a4a7");
        Assert.Null(TurzxScreen.FindLinuxDataPort(_sys));
    }

    public void Dispose()
    {
        if (Directory.Exists(_sys))
        {
            Directory.Delete(_sys, recursive: true);
        }
    }
}

public class TurzxDashboardTests
{
    [Fact]
    public void NativeFrameIsThePanelsPortraitBgra()
    {
        var frame = TurzxDashboard.RenderNative(new TurzxDashboardData { GpuName = "x" }, flip: false);
        Assert.Equal(480 * 1920 * 4, frame.Length);
        Assert.Equal(480 * 1920 * 4, TurzxDashboard.RenderNative(new TurzxDashboardData(), flip: true).Length);
    }

    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", "R7 9800X3D")]
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", "R9 7950X")]
    public void CpuNamesAreShortened(string name, string expected) => Assert.Equal(expected, TurzxDashboard.ShortCpu(name));

    [Fact]
    public void DeviceNamesTakeTheirMakersColour()
    {
        Assert.Equal(SkiaSharp.SKColor.Parse("#76b900"), TurzxDashboard.VendorColor("NVIDIA GeForce RTX 4090"));
        Assert.Equal(SkiaSharp.SKColor.Parse("#f2353b"), TurzxDashboard.VendorColor("AMD Ryzen 7 9800X3D 8-Core Processor"));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090, 44, 0, 14.85, 23900, 24564, 0", 0.0)]
    [InlineData("NVIDIA GeForce RTX 4090, 67, 94, 312.10, 23900, 24564, 41", 41.0)]
    [InlineData("NVIDIA GeForce RTX 4090, 44, 0, 14.85, 23900, 24564, [N/A]", null)]
    [InlineData("NVIDIA GeForce RTX 4090, 44, 0, 14.85, 23900, 24564", null)]
    public void GpuFanSpeedIsParsed(string row, double? expected) => Assert.Equal(expected, DeviceMaster.Sensors.Linux.NvidiaSmi.ParseLine(row)!.FanPercent);
}
