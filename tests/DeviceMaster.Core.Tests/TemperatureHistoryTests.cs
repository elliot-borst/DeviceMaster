using DeviceMaster.Control;

namespace DeviceMaster.Core.Tests;

public class TemperatureHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Capacity_bounds_the_buffer_and_keeps_the_newest()
    {
        var history = new TemperatureHistory(TimeSpan.FromHours(1), capacity: 5);
        for (var i = 0; i < 12; i++)
        {
            history.Add(At(i), i, 100 - i);
        }

        Assert.Equal(5, history.Count);
        var all = history.Window(At(11), TimeSpan.FromHours(1));
        Assert.Equal([7.0, 8, 9, 10, 11], all.Select(s => s.CpuC!.Value));
        Assert.Equal(At(11), history.Latest!.Value.Time);
    }

    [Fact]
    public void Samples_older_than_the_span_age_out()
    {
        var history = new TemperatureHistory(TimeSpan.FromMinutes(10), capacity: 10_000);
        for (var i = 0; i <= 900; i++) // 15 minutes at 1 Hz
        {
            history.Add(At(i), 40, 30);
        }

        Assert.Equal(601, history.Count); // t = 300 s … 900 s inclusive
        Assert.Equal(At(300), history.Window(At(900), TimeSpan.FromHours(1))[0].Time);
    }

    [Fact]
    public void Window_returns_only_recent_samples_oldest_first()
    {
        var history = TemperatureHistory.TenMinutes();
        for (var i = 0; i < 600; i++)
        {
            history.Add(At(i), i, null);
        }

        var lastMinute = history.Window(At(599), TimeSpan.FromMinutes(1));
        Assert.Equal(61, lastMinute.Count); // 539 … 599
        Assert.Equal(539, lastMinute[0].CpuC);
        Assert.Equal(599, lastMinute[^1].CpuC);
        Assert.True(lastMinute.Zip(lastMinute.Skip(1)).All(p => p.First.Time < p.Second.Time));
    }

    [Fact]
    public void Too_close_and_out_of_order_samples_are_dropped()
    {
        var history = new TemperatureHistory(TimeSpan.FromMinutes(10), 100, TimeSpan.FromMilliseconds(500));
        Assert.True(history.Add(At(0), 40, 30));
        Assert.False(history.Add(At(0.2), 41, 31)); // an extra refresh inside the same second
        Assert.False(history.Add(At(-5), 41, 31));  // clock went backwards
        Assert.True(history.Add(At(1), 42, 32));
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void Null_zero_and_non_finite_readings_are_kept_as_gaps()
    {
        var history = TemperatureHistory.TenMinutes();
        history.Add(At(0), 40, 30);
        history.Add(At(1), null, 31);
        history.Add(At(2), 0.0, double.NaN);
        history.Add(At(3), 43, 33);

        var samples = history.Window(At(3), TimeSpan.FromMinutes(10));
        Assert.Equal(4, samples.Count);
        Assert.Null(samples[1].CpuC);
        Assert.Null(samples[2].CpuC);
        Assert.Null(samples[2].GpuC);

        var cpu = TemperatureHistory.Segments(samples, s => s.CpuC, TimeSpan.FromSeconds(5));
        Assert.Equal(2, cpu.Count);
        Assert.Equal([40.0], cpu[0].Select(p => p.Value));
        Assert.Equal([43.0], cpu[1].Select(p => p.Value));

        var gpu = TemperatureHistory.Segments(samples, s => s.GpuC, TimeSpan.FromSeconds(5));
        Assert.Equal(2, gpu.Count);
        Assert.Equal([30.0, 31], gpu[0].Select(p => p.Value));
    }

    [Fact]
    public void A_long_time_gap_splits_a_line()
    {
        var history = TemperatureHistory.TenMinutes();
        history.Add(At(0), 40, 30);
        history.Add(At(1), 41, 30);
        history.Add(At(60), 45, 30); // fan control was off for a minute
        history.Add(At(61), 46, 30);

        var runs = TemperatureHistory.Segments(history.Window(At(61), TimeSpan.FromMinutes(10)), s => s.CpuC, TimeSpan.FromSeconds(5));
        Assert.Equal(2, runs.Count);
        Assert.Equal(2, runs[0].Count);
        Assert.Equal(2, runs[1].Count);
    }

    [Fact]
    public void Empty_history_has_no_latest_and_no_window()
    {
        var history = TemperatureHistory.TenMinutes();
        Assert.Null(history.Latest);
        Assert.Empty(history.Window(T0, TimeSpan.FromMinutes(10)));
        Assert.Empty(TemperatureHistory.Segments([], s => s.CpuC, TimeSpan.FromSeconds(5)));
    }
}
