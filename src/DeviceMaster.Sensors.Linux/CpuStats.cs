using System.Globalization;

namespace DeviceMaster.Sensors.Linux;

/// <summary>CPU and memory headline figures for the dashboard; any field may be null.</summary>
public sealed record CpuSnapshot(string? Name, double? LoadPercent, double? PowerW, double? RamUsedGb, double? RamTotalGb);

/// <summary>
/// CPU load (/proc/stat), package power (RAPL powercap energy counter, which AMD Zen exposes too)
/// and memory (/proc/meminfo). Load and power are rates, so each call compares against the
/// previous call; the first call returns null for both. Everything is read-only procfs/sysfs.
/// </summary>
public sealed class CpuStats(string procRoot = "/proc", string sysRoot = "/sys")
{
    private (ulong Busy, ulong Total)? _lastCpu;
    private (ulong EnergyUj, long Ticks)? _lastEnergy;
    private string? _name;

    public CpuSnapshot Sample()
    {
        double? load = null, power = null, used = null, total = null;
        try
        {
            if (ParseProcStat(File.ReadLines(Path.Combine(procRoot, "stat")).First()) is { } cpu)
            {
                if (_lastCpu is { } last && cpu.Total > last.Total)
                {
                    load = 100.0 * (cpu.Busy - last.Busy) / (cpu.Total - last.Total);
                }

                _lastCpu = cpu;
            }
        }
        catch
        {
            // no /proc/stat — load stays unknown
        }

        try
        {
            var raplDir = Path.Combine(sysRoot, "class", "powercap", "intel-rapl:0");
            var energy = ulong.Parse(File.ReadAllText(Path.Combine(raplDir, "energy_uj")).Trim(), CultureInfo.InvariantCulture);
            var now = Environment.TickCount64;
            if (_lastEnergy is { } last && now > last.Ticks)
            {
                var range = ulong.Parse(File.ReadAllText(Path.Combine(raplDir, "max_energy_range_uj")).Trim(), CultureInfo.InvariantCulture);
                power = PowerWatts(last.EnergyUj, energy, range, (now - last.Ticks) / 1000.0);
            }

            _lastEnergy = (energy, now);
        }
        catch
        {
            // no RAPL (or unreadable) — power stays unknown
        }

        try
        {
            (used, total) = ParseMemInfo(File.ReadAllText(Path.Combine(procRoot, "meminfo")));
        }
        catch
        {
            // memory stays unknown
        }

        try
        {
            _name ??= File.ReadLines(Path.Combine(procRoot, "cpuinfo"))
                .FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?
                .Split(':', 2)[1].Trim();
        }
        catch
        {
            // name stays unknown
        }

        return new CpuSnapshot(_name, load, power, used, total);
    }

    /// <summary>The aggregate "cpu" line: busy = everything but idle + iowait.</summary>
    public static (ulong Busy, ulong Total)? ParseProcStat(string firstLine)
    {
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts[0] != "cpu")
        {
            return null;
        }

        // user nice system idle iowait irq softirq steal (guest columns are already in user/nice)
        var values = parts.Skip(1).Take(8).Select(p => ulong.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var total = values.Aggregate(0UL, (a, b) => a + b);
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return (total - idle, total);
    }

    /// <summary>Watts from two RAPL energy readings, handling the counter wrapping at its range.</summary>
    public static double? PowerWatts(ulong previousUj, ulong currentUj, ulong rangeUj, double seconds)
    {
        if (seconds <= 0)
        {
            return null;
        }

        var delta = currentUj >= previousUj ? currentUj - previousUj : rangeUj - previousUj + currentUj;
        return delta / 1_000_000.0 / seconds;
    }

    /// <summary>(used, total) in GB from /proc/meminfo; used = MemTotal − MemAvailable.</summary>
    public static (double? Used, double? Total) ParseMemInfo(string text)
    {
        double? Kb(string key)
        {
            var line = text.Split('\n').FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal));
            var number = line?.Split(':', 2)[1].Trim().Split(' ')[0];
            return number is not null && double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        var total = Kb("MemTotal");
        var available = Kb("MemAvailable");
        const double kbPerGb = 1024.0 * 1024.0;
        return (total is { } t && available is { } a ? (t - a) / kbPerGb : null, total / kbPerGb);
    }
}
