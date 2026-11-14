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
    public void AHeaderThatWorkedAndWasDroppedIsTakenBackByItself()
    {
        var dir = Chip("nct6687", (1, 1800, 2));
        var log = new List<string>();
        var fans = Open(log);

        // it holds its duty first
        fans.Apply(50);
        _now += HwmonFanControl.VerifyAfterMs;
        fans.Apply(50);
        Assert.Contains(1, fans.EverHeld);

        // then the driver starts ignoring it (the nct6687 "rejected new configuration" case)
        for (var i = 0; i < HwmonFanControl.FailuresBeforeDrop; i++)
        {
            fans.Apply(60);
            Set(dir, "pwm1", 165);
            _now += HwmonFanControl.VerifyAfterMs;
        }

        fans.Apply(60);
        Assert.Empty(fans.Controlled);
        Assert.Contains(1, fans.RetakeAt.Keys);
        _now += HwmonFanControl.VerifyAfterMs;
        fans.Apply(60); // handback confirmed: on the BIOS curve meanwhile
        Assert.Equal("2", Read(dir, "pwm1_enable"));

        // after the wait it is taken back, and once it holds it's fully back under control
        _now += HwmonFanControl.RetakeDelaysMs[0];
        fans.Apply(60);
        Assert.Equal([1], fans.Controlled);
        Assert.Equal("1", Read(dir, "pwm1_enable"));
        _now += HwmonFanControl.VerifyAfterMs;
        fans.Apply(60);
        Assert.Empty(fans.Dropped);
        Assert.Contains(log, l => l.Contains("pwm1 is under control again") || l.Contains("taking pwm1 back"));
    }

    [Fact]
    public void AHeaderThatNeverHeldIsOnlyRetriedAFewTimes()
    {
        var dir = Chip("nct6687", (3, 1600, 2));
        var fans = Open();

        for (var round = 0; round <= HwmonFanControl.MaxRetakesNeverHeld; round++)
        {
            for (var i = 0; i < HwmonFanControl.FailuresBeforeDrop; i++)
            {
                fans.Apply(50);
                Set(dir, "pwm3", 165);
                _now += HwmonFanControl.VerifyAfterMs;
            }

            fans.Apply(50);                               // dropped
            _now += HwmonFanControl.VerifyAfterMs;
            fans.Apply(50);                               // handed back
            _now += HwmonFanControl.RetakeDelaysMs[^1];   // past any retake wait
            fans.Apply(50);                               // retaken (if it still has tries)
        }

        Assert.DoesNotContain(3, fans.EverHeld);
        Assert.Empty(fans.RetakeAt);                      // given up: BIOS curve for good
        Assert.Empty(fans.Controlled);
        Assert.Equal("2", Read(dir, "pwm3_enable"));
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

    [Theory]
    [InlineData("nct6687", "2")]
    [InlineData("nct6799", "5")]
    public void AHeaderLeftManualByAnEarlierRunIsHandedBackToTheBiosMode(string chip, string expected)
    {
        var dir = Chip(chip, (1, 1800, 1)); // a killed run left it manual
        var fans = Open();
        fans.Apply(50);
        Assert.Empty(fans.Release());
        Assert.Equal(expected, Read(dir, "pwm1_enable")); // not "restored" to manual
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
        Assert.Equal(262144, s.ContextMax);
        Assert.Equal(500, s.GpuPowerLimitW);
    }

    [Fact]
    public void LastReplyComesFromTheNewestRequest()
    {
        var s = StrataStatus.Parse("""
            {"live": {"state": "idle"},
             "requests": [{"prompt_tokens": 193007, "reused": 190968, "output_tokens": 730, "first_engine_token_s": 1.987,
                           "drafts_offered": 577, "drafts_accepted": 308},
                          {"prompt_tokens": 100, "output_tokens": 5}]}
            """, DateTime.UtcNow);
        var reply = s.Reply!;
        Assert.False(reply.Live);
        Assert.Equal(1.987, reply.Seconds);
        Assert.Equal(98.9, reply.CacheHitPercent!.Value, 1);
        Assert.Equal(53.4, reply.GuessHitPercent!.Value, 1);
        Assert.Equal(730, reply.Written);
    }

    [Fact]
    public void LiveReplyCountsUpWhileReadingThenEstimatesTheStart()
    {
        var reading = StrataStatus.Parse("""
            {"live": {"state": "reading", "prompt_tokens": 15194, "generated": 0, "elapsed_s": 2.1}}
            """, DateTime.UtcNow).Reply!;
        Assert.True(reading.Live && reading.Reading);
        Assert.Equal(2.1, reading.Seconds);

        var writing = StrataStatus.Parse("""
            {"live": {"state": "generating", "prompt_tokens": 15194, "generated": 92, "elapsed_s": 4.2, "tok_s": 65.8},
             "conversation_cache": {"last_prompt": 15194, "last_reused": 15000}}
            """, DateTime.UtcNow).Reply!;
        Assert.False(writing.Reading);
        Assert.Equal(2.8, writing.Seconds!.Value, 1); // 4.2 s − 92 tokens at 65.8 tok/s (actual: 2.7 s)
        Assert.Equal(98.7, writing.CacheHitPercent!.Value, 1);
        Assert.Null(writing.GuessHitPercent);
        Assert.Equal(92, writing.Written);
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
        Assert.Null(s.Reply);
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
