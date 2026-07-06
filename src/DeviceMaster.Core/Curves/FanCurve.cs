using DeviceMaster.Core.Safety;

namespace DeviceMaster.Core.Curves;

public sealed record CurvePoint(double TemperatureC, int DutyPercent);

/// <summary>
/// A temperature→duty fan curve: linear interpolation between sorted points, flat beyond the
/// ends. Duties are clamped to [0, 100] at construction. Curve evaluation never sees invalid
/// temperatures — callers must gate readings through <see cref="SensorValidity"/> first and
/// apply the failsafe (100%) when a reading is missing or implausible.
/// </summary>
public sealed class FanCurve
{
    public IReadOnlyList<CurvePoint> Points { get; }

    public FanCurve(IEnumerable<CurvePoint> points)
    {
        Points = points
            .Select(p => p with { DutyPercent = SafetyGuard.ClampFanDuty(p.DutyPercent) })
            .OrderBy(p => p.TemperatureC)
            .ToList();

        if (Points.Count == 0)
        {
            throw new ArgumentException("A fan curve needs at least one point.");
        }
    }

    public int EvaluateDuty(double temperatureC)
    {
        if (temperatureC <= Points[0].TemperatureC)
        {
            return Points[0].DutyPercent;
        }

        for (var i = 1; i < Points.Count; i++)
        {
            var (right, left) = (Points[i], Points[i - 1]);
            if (temperatureC > right.TemperatureC)
            {
                continue;
            }

            var span = right.TemperatureC - left.TemperatureC;
            if (span <= 0)
            {
                return right.DutyPercent;
            }

            var fraction = (temperatureC - left.TemperatureC) / span;
            return SafetyGuard.ClampFanDuty(
                (int)Math.Round(left.DutyPercent + fraction * (right.DutyPercent - left.DutyPercent)));
        }

        return Points[^1].DutyPercent;
    }

    /// <summary>Radiator-fan curve driven by loop coolant temperature.</summary>
    public static FanCurve DefaultCoolant { get; } = new([
        new CurvePoint(25, 30),
        new CurvePoint(30, 45),
        new CurvePoint(34, 70),
        new CurvePoint(38, 100),
    ]);

    /// <summary>Fallback curve for CPU/GPU temperature sources.</summary>
    public static FanCurve DefaultCpuGpu { get; } = new([
        new CurvePoint(40, 30),
        new CurvePoint(60, 50),
        new CurvePoint(75, 75),
        new CurvePoint(85, 100),
    ]);
}

/// <summary>Plausibility gate for temperature readings (failed sensors often read 0 or garbage).</summary>
public static class SensorValidity
{
    public static bool IsPlausibleTemperature(double? temperatureC) =>
        temperatureC is { } t && t > 0 && t <= 115;
}
