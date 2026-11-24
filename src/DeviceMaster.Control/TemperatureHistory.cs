namespace DeviceMaster.Control;

/// <summary>
/// A bounded ring buffer of CPU and GPU temperature readings for the Cooling page's history
/// chart (#113). Samples older than <see cref="Span"/> fall off, and at most
/// <see cref="Capacity"/> are kept. A null reading is stored as such so the chart leaves a gap.
/// Pure: the caller passes the time, so it is testable without a clock.
/// </summary>
public sealed class TemperatureHistory
{
    public readonly record struct Sample(DateTimeOffset Time, double? CpuC, double? GpuC);

    private readonly Sample[] _ring;
    private int _head; // index of the oldest sample
    private int _count;

    /// <param name="span">How far back samples are kept.</param>
    /// <param name="capacity">Hard bound on stored samples (span / sample rate, plus slack).</param>
    /// <param name="minInterval">Samples closer than this to the previous one are dropped, so an
    /// extra status refresh doesn't double up a second.</param>
    public TemperatureHistory(TimeSpan span, int capacity, TimeSpan minInterval = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(span, TimeSpan.Zero);
        Span = span;
        MinInterval = minInterval;
        _ring = new Sample[capacity];
    }

    /// <summary>Ten minutes at about 1 Hz (the Cooling page's status refresh).</summary>
    public static TemperatureHistory TenMinutes() => new(TimeSpan.FromMinutes(10), 1200, TimeSpan.FromMilliseconds(500));

    public TimeSpan Span { get; }
    public TimeSpan MinInterval { get; }
    public int Capacity => _ring.Length;
    public int Count => _count;

    /// <summary>The newest sample, or null when empty.</summary>
    public Sample? Latest => _count == 0 ? null : _ring[(_head + _count - 1) % _ring.Length];

    /// <summary>Adds a reading. Out-of-order or too-close samples are ignored (returns false).
    /// Non-finite or ≤ 0 °C values are stored as null (a gap).</summary>
    public bool Add(DateTimeOffset time, double? cpuC, double? gpuC)
    {
        if (Latest is { } last && time - last.Time < (MinInterval > TimeSpan.Zero ? MinInterval : TimeSpan.FromTicks(1)))
        {
            return false;
        }

        var sample = new Sample(time, Clean(cpuC), Clean(gpuC));
        if (_count == _ring.Length)
        {
            _ring[_head] = sample; // overwrite the oldest
            _head = (_head + 1) % _ring.Length;
        }
        else
        {
            _ring[(_head + _count) % _ring.Length] = sample;
            _count++;
        }

        // age out
        var cutoff = time - Span;
        while (_count > 0 && _ring[_head].Time < cutoff)
        {
            _head = (_head + 1) % _ring.Length;
            _count--;
        }

        return true;
    }

    /// <summary>Samples no older than <paramref name="window"/> before <paramref name="now"/>, oldest first.</summary>
    public IReadOnlyList<Sample> Window(DateTimeOffset now, TimeSpan window)
    {
        var from = now - window;
        var result = new List<Sample>(_count);
        for (var i = 0; i < _count; i++)
        {
            var s = _ring[(_head + i) % _ring.Length];
            if (s.Time >= from && s.Time <= now)
            {
                result.Add(s);
            }
        }

        return result;
    }

    /// <summary>
    /// Splits one series into unbroken runs for drawing: a null reading, or a time gap longer than
    /// <paramref name="maxGap"/> (fan control was off, the app was busy), ends a run.
    /// </summary>
    public static List<List<(DateTimeOffset Time, double Value)>> Segments(
        IReadOnlyList<Sample> samples, Func<Sample, double?> select, TimeSpan maxGap)
    {
        var runs = new List<List<(DateTimeOffset, double)>>();
        List<(DateTimeOffset, double)>? current = null;
        DateTimeOffset? previous = null;
        foreach (var s in samples)
        {
            var value = select(s);
            if (value is null || (previous is { } p && s.Time - p > maxGap))
            {
                current = null;
            }

            if (value is { } x)
            {
                if (current is null)
                {
                    current = [];
                    runs.Add(current);
                }

                current.Add((s.Time, x));
            }

            previous = s.Time;
        }

        return runs;
    }

    // ≤ 0 °C is a failed read (an unelevated CPU sensor reports 0), not a temperature
    private static double? Clean(double? value) => value is { } v && double.IsFinite(v) && v > 0 ? v : null;
}
