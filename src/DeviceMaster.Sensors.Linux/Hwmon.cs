namespace DeviceMaster.Sensors.Linux;

/// <summary>One temperature sensor from the hwmon sysfs tree.</summary>
public sealed record HwmonTemp(string Sensor, string Label, double ValueC)
{
    public string Key => string.IsNullOrEmpty(Label) ? Sensor : Label;
}

/// <summary>
/// Read-only scan of /sys/class/hwmon (the kernel's sensor tree — k10temp for AMD, coretemp
/// for Intel, …). No driver or root needed once the nodes exist; values are millidegrees.
/// </summary>
public static class Hwmon
{
    /// <summary>All readable temperature sensors, e.g. ("k10temp", "Tctl", 58.625).</summary>
    public static IReadOnlyList<HwmonTemp> ReadTemperatures(string sysRoot = "/sys")
    {
        var results = new List<HwmonTemp>();
        var hwmonRoot = Path.Combine(sysRoot, "class", "hwmon");
        if (!Directory.Exists(hwmonRoot))
        {
            return results;
        }

        foreach (var device in Directory.GetDirectories(hwmonRoot, "hwmon*"))
        {
            var sensor = ReadFile(Path.Combine(device, "name"));
            if (sensor is null)
            {
                continue;
            }

            foreach (var input in Directory.GetFiles(device, "temp*_input").OrderBy(x => x))
            {
                if (!int.TryParse(ReadFile(input), out var millidegrees))
                {
                    continue;
                }

                var channel = Path.GetFileName(input).Replace("temp", "", StringComparison.Ordinal).Replace("_input", "", StringComparison.Ordinal);
                var label = ReadFile(Path.Combine(device, $"temp{channel}_label"));
                results.Add(new HwmonTemp(sensor, label ?? channel, millidegrees / 1000.0));
            }
        }

        return results;
    }

    /// <summary>
    /// The canonical CPU temperature: k10temp's Tctl (thermal control — the throttle-limit
    /// sensor) when present, else Tdie, else nct67087's "CPU" channel (Supermicro boards),
    /// else the first CPU-ish reading. Null when nothing readable (callers treat that as the
    /// failsafe trigger).
    /// </summary>
    public static double? CpuTemperatureC(IReadOnlyList<HwmonTemp>? all = null)
    {
        all ??= ReadTemperatures();
        return all.FirstOrDefault(t => t.Sensor == "k10temp" && (t.Key == "Tctl" || t.Key == "1"))?.ValueC
            ?? all.FirstOrDefault(t => t.Sensor == "k10temp")?.ValueC
            ?? all.FirstOrDefault(t => t.Sensor == "nct67087" && t.Key == "CPU")?.ValueC
            ?? all.FirstOrDefault(t => t.Sensor is "coretemp" or "k10temp" or "cpu_thermal")?.ValueC;
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return null;
        }
    }
}
