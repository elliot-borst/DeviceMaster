using DeviceMaster.Sensors.Linux;

namespace DeviceMaster.Core.Tests;

public class CpuStatsTests
{
    [Fact]
    public void ProcStat_BusyExcludesIdleAndIowait()
    {
        var (busy, total) = CpuStats.ParseProcStat("cpu  100 10 50 800 40 0 0 0 0 0")!.Value;
        Assert.Equal(1000UL, total);
        Assert.Equal(160UL, busy);
        Assert.Null(CpuStats.ParseProcStat("intr 1 2 3"));
    }

    [Fact]
    public void RaplPower_HandlesCounterWrap()
    {
        Assert.Equal(50.0, CpuStats.PowerWatts(1_000_000, 101_000_000, 1_000_000_000, 2.0));
        Assert.Equal(10.0, CpuStats.PowerWatts(995_000_000, 5_000_000, 1_000_000_000, 1.0)); // wrapped
        Assert.Null(CpuStats.PowerWatts(0, 1, 10, 0));
    }

    [Fact]
    public void MemInfo_UsedIsTotalMinusAvailable()
    {
        var (used, total) = CpuStats.ParseMemInfo("MemTotal:       16777216 kB\nMemFree:  100 kB\nMemAvailable:    4194304 kB\n");
        Assert.Equal(16.0, total);
        Assert.Equal(12.0, used);
    }
}
