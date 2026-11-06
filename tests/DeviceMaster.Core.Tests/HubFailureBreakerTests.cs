using DeviceMaster.Control;
using Xunit;

namespace DeviceMaster.Core.Tests;

/// <summary>
/// Locks the #211 flap-recovery contract: a hub that fails 3 sessions in quick succession trips
/// (the reopen-every-6-s loop that orphaned the pump hub on 2026-10-30 must not run forever),
/// the trip backs the path off, spread-out failures never trip, and paths are independent.
/// </summary>
public class HubFailureBreakerTests
{
    [Fact]
    public void TripsAtThirdFailureWithinWindow()
    {
        var b = new HubFailureBreaker(threshold: 3, windowMs: 600_000, backoffMs: 90_000);
        Assert.False(b.Note("p", 0));
        Assert.False(b.Note("p", 6_000)); // the incident cadence: every ~6 s
        Assert.True(b.Note("p", 12_000));
        Assert.True(b.IsBackedOff("p", 12_000));
        Assert.False(b.IsBackedOff("p", 12_000 + 90_000)); // backoff expired
    }

    [Fact]
    public void FailuresSpreadWiderThanWindowNeverTrip()
    {
        var b = new HubFailureBreaker(threshold: 3, windowMs: 600_000, backoffMs: 90_000);
        Assert.False(b.Note("p", 0));
        Assert.False(b.Note("p", 599_000));
        Assert.False(b.Note("p", 1_199_000)); // first has aged out
        Assert.False(b.IsBackedOff("p", 1_199_000));
    }

    [Fact]
    public void PathsAreIndependent()
    {
        var b = new HubFailureBreaker(threshold: 2, windowMs: 600_000, backoffMs: 90_000);
        b.Note("hubA", 0);
        b.Note("hubB", 1_000);
        Assert.True(b.Note("hubA", 2_000));
        Assert.True(b.IsBackedOff("hubA", 2_000));
        Assert.False(b.IsBackedOff("hubB", 2_000));
    }

    [Fact]
    public void HealthyOpenClearsHistory()
    {
        var b = new HubFailureBreaker(threshold: 3, windowMs: 600_000, backoffMs: 90_000);
        b.Note("p", 0);
        b.Note("p", 1_000);
        b.Forget("p");
        Assert.False(b.Note("p", 2_000)); // back to 1 of 3
        Assert.False(b.IsBackedOff("p", 2_000));
    }

    [Fact]
    public void AfterBackoffASecondBurstTripsAgain()
    {
        var b = new HubFailureBreaker(threshold: 2, windowMs: 600_000, backoffMs: 90_000);
        b.Note("p", 0);
        Assert.True(b.Note("p", 1_000)); // trip 1
        Assert.False(b.Note("p", 91_000)); // count reset; needs a fresh burst
        Assert.True(b.Note("p", 92_000)); // trip 2 → escalation repeats after each burst
        Assert.True(b.IsBackedOff("p", 92_000));
    }
}
