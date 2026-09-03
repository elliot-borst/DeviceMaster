using System.Diagnostics;

namespace DeviceMaster.Sensors.Linux;

/// <summary>One GPU's headline figures as reported by nvidia-smi.</summary>
public sealed record GpuReading(
    string Name, double TemperatureC, double? UtilizationPercent, double? PowerW,
    double? MemoryUsedMb, double? MemoryTotalMb);

/// <summary>
/// Thin nvidia-smi wrapper (the GPU is the only meaningful sensor on this host besides the
/// hub chain). Null result = not present/not readable — the failsafe policy upstream treats
/// that as "sensor unavailable", never as 0°C.
/// </summary>
public static class NvidiaSmi
{
    public static GpuReading? ReadFirst(string path = "/usr/bin/nvidia-smi", int timeoutMs = 3000)
    {
        string output;
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "--query-gpu=name,temperature.gpu,utilization.gpu,power.draw,memory.used,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }) ?? throw new IOException("nvidia-smi would not start");

            output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        var line = output.Split('\n').FirstOrDefault(l => l.Contains(","));
        return line is null ? null : ParseLine(line);
    }

    /// <summary>Parses one nvidia-smi CSV row (pure — unit-tested).</summary>
    public static GpuReading? ParseLine(string line)
    {
        var parts = line.Split(',').Select(p => p.Trim()).ToArray();
        if (parts.Length < 2)
        {
            return null;
        }

        return new GpuReading(
            Name: parts[0],
            TemperatureC: int.Parse(parts[1]),
            UtilizationPercent: parts.Length > 2 ? int.Parse(parts[2]) : null,
            PowerW: parts.Length > 3 ? ParseDouble(parts[3]) : null,
            MemoryUsedMb: parts.Length > 4 ? int.Parse(parts[4]) : null,
            MemoryTotalMb: parts.Length > 5 ? int.Parse(parts[5]) : null);
    }

    private static double? ParseDouble(string text)
    {
        // nvidia-smi prints power.draw like "245.10 W" — the unit is stripped by nounits,
        // but tolerate a trailing one anyway
        return double.TryParse(text.Replace("W", "", StringComparison.Ordinal).Trim(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
