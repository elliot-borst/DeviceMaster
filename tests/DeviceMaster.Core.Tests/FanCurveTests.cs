using DeviceMaster.Core.Curves;

namespace DeviceMaster.Core.Tests;

public class FanCurveTests
{
    private static readonly FanCurve Curve = new([
        new CurvePoint(25, 30),
        new CurvePoint(30, 50),
        new CurvePoint(40, 100),
    ]);

    [Theory]
    [InlineData(10, 30)]    // below first point -> flat
    [InlineData(25, 30)]
    [InlineData(27.5, 40)]  // midpoint interpolation
    [InlineData(30, 50)]
    [InlineData(35, 75)]
    [InlineData(40, 100)]
    [InlineData(60, 100)]   // above last point -> flat
    public void EvaluateDuty_InterpolatesLinearly(double temp, int expected) =>
        Assert.Equal(expected, Curve.EvaluateDuty(temp));

    [Fact]
    public void Construction_SortsPointsAndClampsDuties()
    {
        var curve = new FanCurve([new CurvePoint(40, 150), new CurvePoint(20, -10)]);

        Assert.Equal(20, curve.Points[0].TemperatureC);
        Assert.Equal(0, curve.Points[0].DutyPercent);
        Assert.Equal(100, curve.Points[1].DutyPercent);
    }

    [Fact]
    public void Construction_RejectsEmptyCurve() =>
        Assert.Throws<ArgumentException>(() => new FanCurve([]));

    [Theory]
    [InlineData(null, false)]
    [InlineData(0.0, false)]
    [InlineData(-3.0, false)]
    [InlineData(120.0, false)]
    [InlineData(27.5, true)]
    [InlineData(95.0, true)]
    public void SensorValidity_GatesImplausibleReadings(double? temp, bool expected) =>
        Assert.Equal(expected, SensorValidity.IsPlausibleTemperature(temp));

    [Fact]
    public void DefaultCurves_EndAtFullDuty()
    {
        Assert.Equal(100, FanCurve.DefaultCoolant.EvaluateDuty(100));
        Assert.Equal(100, FanCurve.DefaultCpuGpu.EvaluateDuty(100));
    }
}
