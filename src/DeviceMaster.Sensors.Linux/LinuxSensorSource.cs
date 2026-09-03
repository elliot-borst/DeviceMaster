using DeviceMaster.Core.Sensors;

namespace DeviceMaster.Sensors.Linux;

/// <summary>
/// <see cref="ISensorSource"/> backed by the kernel hwmon tree (CPU) and nvidia-smi (GPU).
/// Throws on total failure — the control loop treats that as the failsafe trigger
/// (fans/pump to 100%), exactly like the Windows LHM source does.
/// </summary>
public sealed class LinuxSensorSource : ISensorSource
{
    public string Name { get; } = "hwmon + nvidia-smi";

    private readonly string _nvidiaSmiPath;

    public LinuxSensorSource(string nvidiaSmiPath = "/usr/bin/nvidia-smi")
    {
        _nvidiaSmiPath = nvidiaSmiPath;
    }

    public IReadOnlyList<SensorReading> Read()
    {
        var now = DateTimeOffset.Now;
        var readings = new List<SensorReading>();
        var sawAny = false;

        try
        {
            var temps = Hwmon.ReadTemperatures();
            var cpu = Hwmon.CpuTemperatureC(temps);
            if (cpu is { } c)
            {
                readings.Add(new SensorReading("cpu", "CPU temperature", SensorKind.Temperature, c, "°C", now));
                sawAny = true;
            }
        }
        catch
        {
            // one source failing is fine as long as another reads
        }

        try
        {
            var gpu = NvidiaSmi.ReadFirst(_nvidiaSmiPath);
            if (gpu is { } g)
            {
                readings.Add(new SensorReading("gpu", $"GPU temperature ({g.Name})", SensorKind.Temperature, g.TemperatureC, "°C", now));
                if (g.UtilizationPercent is { } u)
                {
                    readings.Add(new SensorReading("gpu-util", "GPU utilization", SensorKind.Load, u, "%", now));
                }

                if (g.PowerW is { } w)
                {
                    readings.Add(new SensorReading("gpu-power", "GPU power", SensorKind.Power, w, "W", now));
                }

                sawAny = true;
            }
        }
        catch
        {
        }

        if (!sawAny)
        {
            throw new IOException("No Linux temperature source readable (hwmon and nvidia-smi both failed).");
        }

        return readings;
    }

    public void Dispose()
    {
    }
}
