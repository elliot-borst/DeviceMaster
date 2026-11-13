using DeviceMaster.Devices.Turzx;
using DeviceMaster.Lcd.Skia;
using DeviceMaster.Sensors.Linux;

namespace DeviceMaster.Core.Tests;

public class HwmonFanControlTests : IDisposable
{
    private readonly string _sys = Path.Combine(Path.GetTempPath(), $"dm-sys-{Guid.NewGuid():N}");
    private long _now = 1_000_000;

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

    /// <summary>Opens with a fake clock: sleeps advance it instantly.</summary>
    private HwmonFanControl Open(List<string>? log = null) =>
        HwmonFanControl.Open(_sys, log is null ? null : log.Add, () => _now, ms => _now += ms)!;

    private static string Read(string dir, string file) => File.ReadAllText(Path.Combine(dir, file)).Trim();

    private static void Set(string dir, string file, int value) => File.WriteAllText(Path.Combine(dir, file), $"{value}\n");

    private static void Lock(string file) => File.SetAttributes(file, FileAttributes.ReadOnly); // writes now fail, like EIO

    [Fact]
    public void ControlsOnlySpinningHeadersAndNeverBelowTheFloor()
    {
        var dir = Chip("nct6799", (1, 0, 5), (2, 656, 5));
        var fans = Open();
        Assert.Equal([2], fans.Controlled);

        fans.Apply(10);
        Assert.Equal("1", Read(dir, "pwm2_enable"));
        Assert.Equal("77", Read(dir, "pwm2"));   // 30 % floor = 77/255
        Assert.Equal("5", Read(dir, "pwm1_enable")); // empty header untouched
    }

    [Fact]
    public void ReleaseRestoresTheBiosMode()
    {
        var dir = Chip("nct6799", (2, 656, 5));
        var fans = Open();
        fans.Apply(100);
        Assert.Equal("255", Read(dir, "pwm2"));
        Assert.Empty(fans.Release());
        Assert.Equal("5", Read(dir, "pwm2_enable"));
    }

    [Fact]
    public void AHeaderStillRampingIsNotJudgedEarly()
    {
        var dir = Chip("nct6687", (1, 1800, 2));
        var fans = Open();
        fans.Apply(57);                 // writes 146
        Set(dir, "pwm1", 158);          // the controller is still ramping

        _now += 1_000;
        Assert.Empty(fans.Apply(57));   // too early for a verdict

        Set(dir, "pwm1", 146);          // landed
        _now += HwmonFanControl.VerifyAfterMs;
        Assert.Empty(fans.Apply(57));
        Assert.Equal([1], fans.Controlled);
    }

    [Fact]
    public void AHeaderThatIgnoresItsDutyIsDroppedAndHandedBackAfterSettling()
    {
        var dir = Chip("nct6687", (1, 1800, 2), (3, 1600, 2));
        var log = new List<string>();
        var fans = Open(log);

        // pwm3 accepts the write but the duty never moves (reads back the BIOS value)
        for (var i = 0; i < HwmonFanControl.FailuresBeforeDrop; i++)
        {
            fans.Apply(50);
            Set(dir, "pwm3", 165);
            Set(dir, "pwm1", int.Parse(Read(dir, "pwm1")));
            _now += HwmonFanControl.VerifyAfterMs;
        }

        fans.Apply(50); // third verdict: dropped; its last duty write is already older than the settle time
        Assert.Equal([1], fans.Controlled);
        Assert.Contains(3, fans.Dropped.Keys);
        Assert.Equal("2", Read(dir, "pwm3_enable")); // handback written
        _now += HwmonFanControl.VerifyAfterMs;
        fans.Apply(50);                               // and confirmed
        Assert.Contains(log, l => l.Contains("pwm3 is back on the BIOS curve"));
        Assert.Empty(fans.Stuck);
        Assert.Equal("1", Read(dir, "pwm1_enable")); // the good header carries on
    }

    [Fact]
    public void AHandbackWaitsForTheLastDutyWriteToSettle()
    {
        var dir = Chip("nct6687", (2, 2000, 2));
        var fans = Open();
        fans.Apply(50);
        Lock(Path.Combine(dir, "pwm2"));
        fans.Apply(60); // EIO 1
        fans.Apply(70); // EIO 2
        fans.Apply(80); // EIO 3: dropped right after the successful write at the same instant
        Assert.Contains(2, fans.Dropped.Keys);
        Assert.Equal("1", Read(dir, "pwm2_enable")); // not yet: the 50 % write may still be landing

        _now += HwmonFanControl.SettleMs;
        fans.Apply(80);
        Assert.Equal("2", Read(dir, "pwm2_enable"));
    }

    [Fact]
    public void AHandbackTheControllerUndoesIsRetriedThenFullSpeed()
    {
        var dir = Chip("nct6687", (2, 2000, 2));
        var fans = Open();
        Lock(Path.Combine(dir, "pwm2")); // EIO on every duty write
        for (var i = 0; i < HwmonFanControl.FailuresBeforeDrop; i++)
        {
            fans.Apply(50);
        }

        Assert.Contains(2, fans.Dropped.Keys);
        File.SetAttributes(Path.Combine(dir, "pwm2"), FileAttributes.Normal);
        for (var round = 0; round < 20 && fans.Stuck.Count == 0; round++)
        {
            _now += Math.Max(HwmonFanControl.SettleMs, HwmonFanControl.VerifyAfterMs);
            fans.Apply(50);
            Set(dir, "pwm2_enable", 1); // the pending manual config lands after our handback, every time
        }

        Assert.Contains(2, fans.Stuck.Keys);
        Assert.Equal("255", Read(dir, "pwm2")); // full speed, never a frozen duty
    }

    [Fact]
    public void ReleaseAlsoCoversDroppedHeaders()
    {
        var dir = Chip("nct6687", (1, 1800, 2), (2, 2000, 2));
        var fans = Open();
        Lock(Path.Combine(dir, "pwm2"));
        for (var i = 0; i < HwmonFanControl.FailuresBeforeDrop; i++)
        {
            fans.Apply(50);
        }

        Assert.Equal([1], fans.Controlled);
        File.SetAttributes(Path.Combine(dir, "pwm2"), FileAttributes.Normal);
        Set(dir, "pwm2_enable", 1); // its handback never happened (or was undone)

        Assert.Empty(fans.Release());
        Assert.Equal("2", Read(dir, "pwm1_enable"));
        Assert.Equal("2", Read(dir, "pwm2_enable"));
    }

    [Fact]
    public void AFailedReleaseIsReportedAndLeftAtFullSpeed()
    {
        var dir = Chip("nct6687", (1, 1800, 2));
        var fans = Open();
        fans.Apply(40);
        Lock(Path.Combine(dir, "pwm1_enable"));

        var problems = fans.Release();

        Assert.Single(problems, p => p.Contains("pwm1 could not be handed back") && p.Contains("full speed"));
        Assert.Equal("255", Read(dir, "pwm1")); // never stuck at the last curve duty
    }

    [Fact]
    public void HandBackIsGradualAndControlCanResume()
    {
        var dir = Chip("nct6799", (2, 656, 5));
        var fans = Open();
        fans.Apply(60);

        fans.HandBack();                               // mode Off: nothing blocks
        Assert.Equal("1", Read(dir, "pwm2_enable"));   // still settling
        _now += HwmonFanControl.SettleMs;
        fans.HandBack();
        Assert.Equal("5", Read(dir, "pwm2_enable"));

        fans.Apply(70);                                // back to Curve
        Assert.Equal("1", Read(dir, "pwm2_enable"));
        Assert.Equal([2], fans.Controlled);
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
