using System.Diagnostics;
using DeviceMaster.Devices.LianLi;

namespace DeviceMaster.Core.Tests;

public class PreciseDelayTests
{
    [Fact]
    public void TwoMillisecondWaitsAreNotRoundedUpToTheSystemTick()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the high-resolution timer is a Windows API; elsewhere Thread.Sleep is already fine
        }

        using var delay = new PreciseDelay();
        Assert.True(delay.IsHighResolution);

        const int waits = 50;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < waits; i++)
        {
            delay.Wait(2);
        }

        // Thread.Sleep(2) averages ~15.6 ms at the default resolution; generous bound for loaded machines
        var average = clock.Elapsed.TotalMilliseconds / waits;
        Assert.InRange(average, 1.5, 8.0);
    }
}
