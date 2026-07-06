using DeviceMaster.Core.Safety;

namespace DeviceMaster.Core.Tests;

public class SafetyGuardTests
{
    [Theory]
    [InlineData(-10, 50)]
    [InlineData(0, 50)]
    [InlineData(49, 50)]
    [InlineData(50, 50)]
    [InlineData(75, 75)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void PumpDuty_NeverLeavesSafeRange(int requested, int expected) =>
        Assert.Equal(expected, SafetyGuard.ClampPumpDuty(requested));

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
