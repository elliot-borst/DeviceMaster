namespace DeviceMaster.Core.Curves;

/// <summary>
/// The rules every user-edited fan curve passes through before it is saved — one implementation
/// for the desktop editor and the web API, so neither can store a curve the other would reject.
/// </summary>
public static class CurveRules
{
    public const int MinPoints = 2;
    public const int MaxPoints = 8;

    /// <summary>Temperature axis for a curve source: (min, max) in °C.</summary>
    public static (double Min, double Max) RangeFor(bool coolant) => coolant ? (20, 70) : (30, 100);

    /// <summary>
    /// Sorts, clamps and repairs a curve: temperatures inside the source's range, whole degrees,
    /// at least 1 °C apart; duties 0–100 and never falling as temperature rises; the hottest
    /// point always 100% (running hotter than the curve's end means full speed). Returns null
    /// when there are too few or too many points to be a curve.
    /// </summary>
    public static List<CurvePoint>? Normalize(IEnumerable<CurvePoint> points, bool coolant)
    {
        var (min, max) = RangeFor(coolant);
        var list = points
            .Where(p => double.IsFinite(p.TemperatureC))
            .OrderBy(p => p.TemperatureC)
            .ToList();
        if (list.Count is < MinPoints or > MaxPoints)
        {
            return null;
        }

        var result = new List<CurvePoint>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            // leave room for the points still to come, each at least 1 °C further on
            var lowest = result.Count > 0 ? result[^1].TemperatureC + 1 : min;
            var highest = max - (list.Count - 1 - i);
            var temp = Math.Clamp(Math.Round(list[i].TemperatureC), lowest, highest);
            var duty = Math.Clamp(list[i].DutyPercent, 0, 100);
            if (result.Count > 0)
            {
                duty = Math.Max(duty, result[^1].DutyPercent);
            }

            result.Add(new CurvePoint(temp, duty));
        }

        result[^1] = result[^1] with { DutyPercent = 100 };
        return result;
    }

    /// <summary>The factory curve for a source.</summary>
    public static IReadOnlyList<CurvePoint> DefaultFor(bool coolant) =>
        (coolant ? FanCurve.DefaultCoolant : FanCurve.DefaultCpuGpu).Points;
}
