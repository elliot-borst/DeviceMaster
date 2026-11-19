using DeviceMaster.Core.Safety;

namespace DeviceMaster.Core.Tests;

public class SafetyGuardTests
{
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(42, 42)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public void FanDuty_ClampsToPercentRange(int requested, int expected) =>
        Assert.Equal(expected, SafetyGuard.ClampFanDuty(requested));

    [Fact]
    public void SensorFailure_MeansFullDuty() =>
        Assert.Equal(SafetyLimits.FailsafeDutyPercent, SafetyGuard.DutyOnSensorFailure());

    [Fact]
    public void FailsafeIsFullDuty() =>
        Assert.Equal(100, SafetyLimits.FailsafeDutyPercent);
}
