using DeviceMaster.Core.Discovery;
using Xunit;

namespace DeviceMaster.Core.Tests;

public class UsbHubHealTests
{
    private const string Hub = @"USB\VID_05E3&PID_0610\7&63b41e3&0&2";

    [Fact]
    public void AFailureIsConfirmedBeforeHealing()
    {
        var policy = new UsbHubHealPolicy();
        Assert.Empty(policy.Next([Hub], 0));
        Assert.Empty(policy.Next([Hub], 5_000));
        var heal = Assert.Single(policy.Next([Hub], UsbHubHealPolicy.ConfirmMs));
        Assert.True(heal.Heal);
        Assert.Equal(1, heal.Attempt);
    }

    [Fact]
    public void AFlickerThatClearsIsNeverHealed()
    {
        var policy = new UsbHubHealPolicy();
        Assert.Empty(policy.Next([Hub], 0));
        Assert.Empty(policy.Next([], 5_000));
        Assert.Empty(policy.Next([], UsbHubHealPolicy.ClearMs + 5_000)); // forgotten
        Assert.Empty(policy.Next([Hub], UsbHubHealPolicy.ClearMs + 10_000)); // a new failure starts over
    }

    [Fact]
    public void RepeatedFailuresBackOffThenGiveUpOnce()
    {
        var policy = new UsbHubHealPolicy();
        policy.Next([Hub], 0);
        var now = UsbHubHealPolicy.ConfirmMs;
        var attempts = new List<int>();
        var gaveUp = 0;
        for (var t = now; t < now + 3_600_000; t += 5_000)
        {
            foreach (var d in policy.Next([Hub], t))
            {
                if (d.Heal)
                {
                    attempts.Add(d.Attempt);
                }

                gaveUp += d.GiveUp ? 1 : 0;
            }
        }

        Assert.Equal([1, 2, 3, 4], attempts);
        Assert.Equal(1, gaveUp);
    }

    [Fact]
    public void AHubThatVanishesDuringItsCycleKeepsItsAttemptCount()
    {
        var policy = new UsbHubHealPolicy();
        policy.Next([Hub], 0);
        Assert.Single(policy.Next([Hub], 10_000));
        Assert.Empty(policy.Next([], 15_000)); // off the bus while cycling
        Assert.Empty(policy.Next([Hub], 25_000)); // failed again: still inside the 30 s backoff
        Assert.Equal(2, Assert.Single(policy.Next([Hub], 40_000)).Attempt);
    }

    [Fact]
    public void ConnectionInfoIsDecodedFromThePackedStruct()
    {
        var buffer = new byte[64];
        buffer[12] = 0xD9; buffer[13] = 0x0F; // idVendor 0FD9
        buffer[14] = 0x80; buffer[15] = 0x00; // idProduct 0080
        buffer[31] = 2; // DeviceFailedEnumeration
        var (status, vid, pid) = UsbHubPorts.ParseConnectionInfo(buffer);
        Assert.Equal(UsbPortStatus.DeviceFailedEnumeration, status);
        Assert.Equal(0x0FD9, vid);
        Assert.Equal(0x0080, pid);
        Assert.True(new UsbHubPort(Hub, 1, status, vid, pid).Failed);
    }
}
