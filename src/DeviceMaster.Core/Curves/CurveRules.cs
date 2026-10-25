namespace DeviceMaster.Core.Curves;

/// <summary>
/// The rules every fan curve passes through before it is used or saved — one implementation for
/// the desktop editor, the web API and settings loading, so neither app can store a curve the
/// other would reject. A curve is two points: flat at the first point's duty below it, a straight
/// ramp between the two, flat at the second point's duty above it.
/// </summary>
public static class CurveRules
{
    public const int Points = 2;

    /// <summary>The temperature axis shared by every source and both apps, in °C.</summary>
    public const double MinTemperature = 20, MaxTemperature = 100;

    /// <summary>
    /// Sorts, clamps and repairs a curve: whole degrees inside the axis, the points at least
    /// 1 °C apart, duties 0–100 and never falling as temperature rises. Older curves with more
    /// points keep their first and last point. Null when there aren't two points to work with.
    /// </summary>
    public static List<CurvePoint>? Normalize(IEnumerable<CurvePoint>? points)
    {
        var list = (points ?? []).Where(p => double.IsFinite(p.TemperatureC)).OrderBy(p => p.TemperatureC).ToList();
        if (list.Count < Points)
        {
            return null;
        }

        var low = list[0];
        var high = list[^1];
        var lowTemp = Math.Clamp(Math.Round(low.TemperatureC), MinTemperature, MaxTemperature - 1);
        var highTemp = Math.Clamp(Math.Round(high.TemperatureC), lowTemp + 1, MaxTemperature);
        var lowDuty = Math.Clamp(low.DutyPercent, 0, 100);
        var highDuty = Math.Clamp(Math.Max(high.DutyPercent, lowDuty), 0, 100);
        return [new CurvePoint(lowTemp, lowDuty), new CurvePoint(highTemp, highDuty)];
    }

    /// <summary>The factory curve for a source.</summary>
    public static IReadOnlyList<CurvePoint> DefaultFor(bool coolant) =>
        coolant ? [new(25, 30), new(38, 100)] : [new(40, 30), new(85, 100)];
}
