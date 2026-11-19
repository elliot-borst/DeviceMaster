using System.Text.Json;
using DeviceMaster.Control;
using DeviceMaster.Core.Curves;
using DeviceMaster.Sensors.Linux;

namespace DeviceMaster.Core.Tests;

/// <summary>#98: CPU/GPU load, integrated-GPU temperature, and the fleet summary fields behind the Servers page.</summary>
public sealed class ServersPageDataTests : IDisposable
{
    private readonly string _sys = Path.Combine(Path.GetTempPath(), "dm-sys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_sys, recursive: true);
        }
        catch
        {
        }
    }

    // ---- /proc/stat load delta ----

    [Fact]
    public void CpuLoad_IsBusyDeltaOverTotalDelta()
    {
        var a = CpuStats.ParseProcStat("cpu  100 0 100 800 0 0 0 0")!.Value;   // busy 200 / total 1000
        var b = CpuStats.ParseProcStat("cpu  250 0 150 1000 100 0 0 0")!.Value; // busy 400 / total 1500
        Assert.Equal(40.0, CpuStats.LoadPercent(a, b)!.Value, 6);              // 200 busy of 500
    }

    [Fact]
    public void CpuLoad_NullWhenNoTimePassedOrCountersWentBackwards()
    {
        Assert.Null(CpuStats.LoadPercent((100, 1000), (100, 1000)));
        Assert.Null(CpuStats.LoadPercent((100, 1000), (50, 900)));
        Assert.Null(CpuStats.LoadPercent((100, 1000), (90, 2000)));
        Assert.Equal(100.0, CpuStats.LoadPercent((0, 0), (10, 10)));
    }

    [Fact]
    public void CpuStats_SamplesLoadFromAFakeProc()
    {
        Directory.CreateDirectory(_sys);
        var stats = new CpuStats(procRoot: _sys, sysRoot: _sys);
        File.WriteAllText(Path.Combine(_sys, "stat"), "cpu  100 0 100 800 0 0 0 0\ncpu0 1 1 1 1\n");
        Assert.Null(stats.Sample().LoadPercent); // first sample has nothing to compare with
        File.WriteAllText(Path.Combine(_sys, "stat"), "cpu  250 0 150 1000 100 0 0 0\n");
        Assert.Equal(40.0, stats.Sample().LoadPercent!.Value, 6);
    }

    // ---- integrated-GPU hwmon selection ----

    private void Hwmon(int index, string name, params (int Channel, string? Label, int MilliC)[] temps)
    {
        var dir = Path.Combine(_sys, "class", "hwmon", $"hwmon{index}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "name"), name + "\n");
        foreach (var (channel, label, milli) in temps)
        {
            File.WriteAllText(Path.Combine(dir, $"temp{channel}_input"), milli + "\n");
            if (label is not null)
            {
                File.WriteAllText(Path.Combine(dir, $"temp{channel}_label"), label + "\n");
            }
        }
    }

    [Fact]
    public void IntegratedGpu_PicksAmdgpuEdgeFromFakeSysfs()
    {
        Hwmon(0, "k10temp", (1, "Tctl", 61_000));
        Hwmon(1, "nvme", (1, "Composite", 40_000));
        Hwmon(2, "amdgpu", (1, "edge", 48_000), (2, "junction", 55_000));

        var temps = DeviceMaster.Sensors.Linux.Hwmon.ReadTemperatures(_sys);
        Assert.Equal(48.0, IntegratedGpu.TemperatureC(temps));
        Assert.Equal(61.0, DeviceMaster.Sensors.Linux.Hwmon.CpuTemperatureC(temps)); // the CPU pick is unchanged
    }

    [Fact]
    public void IntegratedGpu_IntelXeOrI915WhenNoAmdgpu()
    {
        Hwmon(0, "coretemp", (1, "Package id 0", 55_000));
        Hwmon(1, "i915", (1, null, 50_000));
        Hwmon(2, "xe", (2, "pkg", 47_000), (3, "vram", 52_000));

        var temps = DeviceMaster.Sensors.Linux.Hwmon.ReadTemperatures(_sys);
        Assert.Equal(47.0, IntegratedGpu.TemperatureC(temps)); // xe before i915, "pkg" preferred
    }

    [Fact]
    public void IntegratedGpu_NullWithoutAGpuAndSkipsImplausibleValues()
    {
        Hwmon(0, "k10temp", (1, "Tctl", 61_000));
        Assert.Null(IntegratedGpu.TemperatureC(DeviceMaster.Sensors.Linux.Hwmon.ReadTemperatures(_sys)));

        Hwmon(1, "amdgpu", (1, "edge", 0));
        Assert.Null(IntegratedGpu.TemperatureC(DeviceMaster.Sensors.Linux.Hwmon.ReadTemperatures(_sys)));
        Assert.Null(IntegratedGpu.TemperatureC(DeviceMaster.Sensors.Linux.Hwmon.ReadTemperatures(Path.Combine(_sys, "missing"))));
    }

    [Fact]
    public void IntegratedGpu_SeveralAmdgpuDevicesReportTheHottest()
    {
        Hwmon(0, "amdgpu", (1, "edge", 45_000));
        Hwmon(1, "amdgpu", (1, "edge", 71_000), (2, "junction", 80_000));
        Assert.Equal(71.0, IntegratedGpu.TemperatureC(DeviceMaster.Sensors.Linux.Hwmon.ReadTemperatures(_sys)));
    }

    [Fact]
    public void IntegratedGpu_BusyPercentFromAmdgpuCardsOnly()
    {
        Assert.Null(IntegratedGpu.BusyPercent(_sys));
        var card0 = Path.Combine(_sys, "class", "drm", "card0", "device");
        var card1 = Path.Combine(_sys, "class", "drm", "card1", "device");
        var connector = Path.Combine(_sys, "class", "drm", "card1-DP-1", "device");
        Directory.CreateDirectory(card0); // an Intel card: no busy file
        Directory.CreateDirectory(card1);
        Directory.CreateDirectory(connector);
        Assert.Null(IntegratedGpu.BusyPercent(_sys));

        File.WriteAllText(Path.Combine(card1, "gpu_busy_percent"), "37\n");
        File.WriteAllText(Path.Combine(connector, "gpu_busy_percent"), "99\n"); // not a card directory
        Assert.Equal(37.0, IntegratedGpu.BusyPercent(_sys));

        Assert.Null(IntegratedGpu.ParseBusyPercent("garbage"));
        Assert.Null(IntegratedGpu.ParseBusyPercent("140"));
        Assert.Equal(0.0, IntegratedGpu.ParseBusyPercent("0"));
    }

    // ---- the curve: an integrated GPU never newly triggers the failsafe ----

    [Fact]
    public void Hottest_IntegratedGpuIsOptional()
    {
        Assert.Equal(70, HottestTemperature.Combine(60, null, 70, discreteGpuExpected: false));  // iGPU counts when there
        Assert.Equal(60, HottestTemperature.Combine(60, null, null, discreteGpuExpected: false)); // missing → CPU alone
        Assert.Equal(60, HottestTemperature.Combine(60, null, 0, discreteGpuExpected: false));   // implausible → CPU alone
        Assert.Null(HottestTemperature.Combine(null, null, 70, discreteGpuExpected: false));      // no CPU is still failsafe
    }

    [Fact]
    public void Hottest_ALostDiscreteGpuIsStillFailsafeEvenWithAnIntegratedOne()
    {
        Assert.Null(HottestTemperature.Combine(60, null, 50, discreteGpuExpected: true));
        Assert.Equal(80, HottestTemperature.Combine(60, 80, 50, discreteGpuExpected: true)); // the discrete wins
    }

    // ---- summary fields ----

    [Fact]
    public void Summary_CarriesLoadAndMemoryAndRoundTripsThroughJson()
    {
        var snapshot = new MachineSnapshot
        {
            Name = "A",
            Online = true,
            Status = new ControlStatus
            {
                CpuLoadPercent = 12.5,
                GpuLoadPercent = 80,
                RamUsedGb = 10.2,
                RamTotalGb = 32,
                UptimeSeconds = 90_000,
                Storage = [new StorageInfo("/", 1_000, 250)],
                Docker = new DockerInfo(3, 4, ["web"]),
            },
        };

        var summary = snapshot.Summarize(DateTime.UtcNow);
        var back = JsonSerializer.Deserialize<MachineSummary>(JsonSerializer.Serialize(summary, AgentApi.Json), AgentApi.Json)!;
        Assert.Equal(12.5, back.CpuLoadPercent);
        Assert.Equal(80, back.GpuLoadPercent);
        Assert.Equal(10.2, back.RamUsedGb);
        Assert.Equal(32, back.RamTotalGb);
        Assert.Equal(90_000, back.UptimeSeconds);
        Assert.Equal(250, Assert.Single(back.Storage).UsedBytes);
        Assert.Equal(3, back.Docker!.Running);
    }

    [Fact]
    public void Summary_UnknownLoadStaysNull()
    {
        var back = JsonSerializer.Deserialize<MachineSummary>(
            JsonSerializer.Serialize(new MachineSnapshot { Name = "B", Status = new ControlStatus() }.Summarize(DateTime.UtcNow), AgentApi.Json),
            AgentApi.Json)!;
        Assert.Null(back.CpuLoadPercent);
        Assert.Null(back.GpuLoadPercent);
        Assert.Null(back.RamUsedGb);

        // an older hub's JSON without the new fields still reads
        var old = JsonSerializer.Deserialize<MachineSummary>("""{"Name":"C","Online":true}""", AgentApi.Json)!;
        Assert.Null(old.CpuLoadPercent);
    }
}
