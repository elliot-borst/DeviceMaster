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
            Directory.Delete(_sys, recursive: true);
        }
    }
}

public class StrataStatusTests
{
    private const string Metrics = """
        {"engine": {"model": "qwen3.8-flash-next-unsloth-ud-iq3_xxs", "max_context": 262144},
         "live": {"state": "generating", "phase": "thinking", "running": 1, "outside_slots": 1, "queued": 1, "tok_s": 108.5, "waiting": 0,
                  "slots": [{"slot": 0, "state": "generating", "held_tokens": 1200}, {"slot": 1, "state": "idle", "held_tokens": 0}]},
         "totals": {"requests": 42, "output_tokens": 9000},
         "hardware": {"gpu_util": 93, "gpu_mem_used": 25077088256, "gpu_mem_total": 25757220864, "gpu_temp": 66, "gpu_power": 301.5},
         "hardware_static": {"gpu_name": "NVIDIA GeForce RTX 4090"},
         "history": {"tok_s": [0.0, 50.5, 108.5]}}
        """;

    [Fact]
    public void ParsesStateSlotsAndGpu()
    {
        var s = StrataStatus.Parse(Metrics, DateTime.UtcNow);
        Assert.Equal("qwen3.8-flash-next-unsloth-ud-iq3_xxs", s.Model);
        Assert.Equal("generating", s.State);
        Assert.Equal(108.5, s.TokPerSec);
        Assert.Equal(2, s.Slots.Count);
        Assert.Equal(new LlmSlot(0, "generating", 1200), s.Slots[0]);
        Assert.Equal(1, s.Queued);
        Assert.Equal(42, s.Requests);
        Assert.Equal(66, s.GpuTempC);
        Assert.Equal(23915.375, s.GpuMemUsedMb!.Value, 3); // 25077088256 bytes / 2^20
        Assert.Equal([0.0, 50.5, 108.5], s.TokHistory);
        Assert.Equal(1, s.Running);
        Assert.Equal(1, s.OutsideSlots);
        Assert.Equal("thinking", s.Phase);
    }

    [Fact]
    public void ToleratesMissingSections()
    {
        var s = StrataStatus.Parse("{}", DateTime.UtcNow);
        Assert.Equal("unknown", s.State);
        Assert.Empty(s.Slots);
        Assert.Null(s.GpuTempC);
    }

    [Fact]
    public void ReadsTheKeyFromAnEnvFileWithoutLoggingIt()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "FAMILY=unsloth\nAPI_KEY=\"sk-test-123\"\n");
            Assert.Equal("sk-test-123", StrataStatus.ReadKeyFile(path));
            Assert.Null(StrataStatus.ReadKeyFile(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
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
        var frame = TurzxDashboard.RenderNative(new TurzxDashboardData { HasLlm = true, LlmModel = "x" }, flip: false);
        Assert.Equal(480 * 1920 * 4, frame.Length);
        Assert.Equal(480 * 1920 * 4, TurzxDashboard.RenderNative(new TurzxDashboardData(), flip: true).Length);
    }

    [Theory]
    [InlineData("qwen3.8-flash-next-unsloth-ud-iq3_xxs", "qwen3.8 flash next · UD-IQ3_XXS")]
    [InlineData("unsloth/Qwen3.8-Flash-Next-UD-IQ3_XXS", "Qwen3.8 Flash Next · UD-IQ3_XXS")]
    [InlineData("qwen3.8-27b", "qwen3.8 27b")]
    public void ModelNamesAreShortened(string model, string expected) => Assert.Equal(expected, TurzxDashboard.ShortModel(model));

    [Theory]
    [InlineData(true, 1, 82.4, 0, "82 tok/s")]
    [InlineData(true, 2, 120.0, 12_400, "~60 tok/s · 12.4K ctx")]
    [InlineData(false, 2, 120.0, 0, "")]
    [InlineData(true, 1, 0.0, 3_000, "3K ctx")]
    public void SlotSpeedIsExactAloneAndAnEstimateWhenShared(bool active, int busy, double total, int held, string expected) =>
        Assert.Equal(expected, TurzxDashboard.SlotDetail(active, busy, total, held));

    [Fact]
    public void IdleIsCapitalised() => Assert.Equal("Idle", TurzxDashboard.StateWord("idle"));

    [Fact]
    public void TokenCountsAreCompact()
    {
        Assert.Equal("950", TurzxDashboard.Tokens(950));
        Assert.Equal("12.4K", TurzxDashboard.Tokens(12_400));
        Assert.Equal("262K", TurzxDashboard.Tokens(262_144));
    }
}
