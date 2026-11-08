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
         "totals": {"requests": 42, "output_tokens": 9000, "prompt_tokens": 120000, "reused": 100000, "prompt_ms": 8000,
                    "drafts_offered": 400, "drafts_accepted": 300},
         "hardware": {"gpu_util": 93, "gpu_mem_used": 25077088256, "gpu_mem_total": 25757220864, "gpu_temp": 66, "gpu_power": 301.5, "gpu_power_limit": 500.0},
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
        Assert.Equal(2500, s.PromptTokPerSec); // (120000 - 100000 reused) tokens in 8 s
        Assert.Equal(75, s.DraftHitPercent);
        Assert.Equal(100.0 * 100000 / 120000, s.CacheHitPercent!.Value, 6);
        Assert.Equal(262144, s.ContextMax);
        Assert.Equal(500, s.GpuPowerLimitW);
    }

    [Fact]
    public void ContextIsTheLiveConversationOrTheNewestRequest()
    {
        var busy = StrataStatus.Parse("""{"live": {"prompt_total": 190000, "generated": 512}}""", DateTime.UtcNow);
        Assert.Equal(190512, busy.ContextUsed);
        var idle = StrataStatus.Parse("""
            {"live": {"prompt_total": null},
             "requests": [{"prompt_total": 56925, "output_tokens": 245}, {"prompt_total": 195993, "output_tokens": 662}]}
            """, DateTime.UtcNow);
        Assert.Equal(57170, idle.ContextUsed);
        Assert.Null(StrataStatus.Parse("{}", DateTime.UtcNow).ContextUsed);
    }

    [Fact]
    public void ToleratesMissingSections()
    {
        var s = StrataStatus.Parse("{}", DateTime.UtcNow);
        Assert.Equal("unknown", s.State);
        Assert.Empty(s.Slots);
        Assert.Null(s.GpuTempC);
        Assert.Null(s.PromptTokPerSec);
        Assert.Null(s.DraftHitPercent);
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

    [Fact]
    public void IdleIsCapitalised() => Assert.Equal("Idle", TurzxDashboard.StateWord("idle"));

    [Fact]
    public void TokenCountsAreCompact()
    {
        Assert.Equal("950", TurzxDashboard.Tokens(950));
        Assert.Equal("12.4K", TurzxDashboard.Tokens(12_400));
        Assert.Equal("262K", TurzxDashboard.Tokens(262_144));
        Assert.Equal("1.24M", TurzxDashboard.Tokens(1_243_118));
        Assert.Equal("30.1K", TurzxDashboard.Tokens(30_110));
    }

    [Theory]
    [InlineData("generating", "Write")]
    [InlineData("prefill", "Read")]
    [InlineData("idle", "Idle")]
    public void SlotWordsAreShort(string state, string expected) => Assert.Equal(expected, TurzxDashboard.SlotWord(state));

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
