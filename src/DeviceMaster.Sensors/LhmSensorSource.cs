using DeviceMaster.Core.Sensors;
using LibreHardwareMonitor.Hardware;

namespace DeviceMaster.Sensors;

/// <summary>
/// LibreHardwareMonitor-backed sensor source for CPU/GPU/motherboard readings.
/// Run elevated for full data — CPU package temperatures need LHM's kernel driver,
/// which only loads with administrator rights.
/// Coolant temperature is NOT read here; it comes from the iCUE Link hub's own
/// temperature sensor once the CorsairLink device layer lands (Stage 1).
/// </summary>
public sealed class LhmSensorSource : ISensorSource
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();

    public LhmSensorSource()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsMemoryEnabled = true, // RAM load for the LCD metrics
        };
        _computer.Open();
    }

    public string Name => "LibreHardwareMonitor";

    /// <summary>
    /// Polls every hardware device once (the expensive part — NvAPI/kernel-driver/SuperIO I/O).
    /// Call this once per control tick, then read with <c>refresh: false</c> from every consumer so
    /// a single tick does one poll instead of one per consumer (which stretched the tick toward 2 s).
    /// </summary>
    public void RefreshHardware() => _computer.Accept(_visitor);

    public IReadOnlyList<SensorReading> Read() => Read(refresh: true);

    /// <param name="refresh">
    /// Poll the hardware first. Pass false to read the most recent <see cref="RefreshHardware"/>
    /// snapshot without re-polling (one poll per tick, shared across consumers).
    /// </param>
    public IReadOnlyList<SensorReading> Read(bool refresh)
    {
        if (refresh)
        {
            _computer.Accept(_visitor);
        }

        var now = DateTimeOffset.Now;
        var readings = new List<SensorReading>();
        foreach (var hardware in _computer.Hardware)
            Collect(hardware, readings, now);
        return readings;
    }

    /// <summary>
    /// One-shot headline stats for the Turzx dashboard: CPU/GPU name, load, temperature, power,
    /// and RAM/VRAM used (GB). Reads sensor types the generic <see cref="Read"/> path drops
    /// (Power, Data/SmallData), matched by hardware type + sensor-name keyword with fallbacks so
    /// it stays vendor-generic. Prefers a discrete NVIDIA GPU when an integrated one is also present.
    /// </summary>
    /// <param name="refresh">Poll the hardware first (default); false reads the latest snapshot.</param>
    public SystemStats ReadSystemStats(bool refresh = true)
    {
        if (refresh)
        {
            _computer.Accept(_visitor);
        }

        IHardware? cpu = null, mem = null, gpu = null;
        foreach (var hw in _computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    cpu ??= hw;
                    break;
                case HardwareType.Memory:
                    // LHM exposes TWO Memory devices, both with a "Memory Used" Data sensor:
                    // "Virtual Memory" (commit charge, ~total — misleading) and "Total Memory"
                    // (physical in-use = Task Manager's "In use"). Prefer the physical one, whatever
                    // the enumeration order, by rejecting the "Virtual" device once a plain one appears.
                    if (mem is null || mem.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                    {
                        mem = hw;
                    }

                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    // prefer NVIDIA (the discrete card) over an integrated GPU
                    if (gpu is null || (hw.HardwareType == HardwareType.GpuNvidia && gpu.HardwareType != HardwareType.GpuNvidia))
                    {
                        gpu = hw;
                    }

                    break;
            }
        }

        var (vramSensor, vramGb) = gpu is null ? (null, (double?)null) : FindVram(gpu);

        return new SystemStats(
            CpuName: cpu?.Name ?? "CPU",
            CpuLoadPercent: cpu is null ? null : Pick(cpu, SensorType.Load, n => n.Contains("Total")) ?? Max(cpu, SensorType.Load),
            CpuTempC: cpu is null ? null : Pick(cpu, SensorType.Temperature, n => n.Contains("Tctl") || n.Contains("Package") || n.Contains("Core (")) ?? Max(cpu, SensorType.Temperature),
            CpuPowerW: cpu is null ? null : Pick(cpu, SensorType.Power, n => n.Contains("Package")) ?? Max(cpu, SensorType.Power),
            RamUsedGb: mem is null ? null : Pick(mem, SensorType.Data, n => n.Equals("Memory Used", StringComparison.OrdinalIgnoreCase)) ?? Pick(mem, SensorType.Data, n => n.Contains("Used") && !n.Contains("Virtual")),
            GpuName: gpu?.Name ?? "GPU",
            GpuLoadPercent: gpu is null ? null : Pick(gpu, SensorType.Load, n => n.Contains("GPU Core")) ?? Pick(gpu, SensorType.Load, n => n.Contains("Core")),
            GpuTempC: gpu is null ? null : Pick(gpu, SensorType.Temperature, n => n.Contains("GPU Core")) ?? Pick(gpu, SensorType.Temperature, n => n.Contains("Core")) ?? Max(gpu, SensorType.Temperature),
            GpuPowerW: gpu is null ? null : Pick(gpu, SensorType.Power, n => n.Contains("GPU Package") || n.Contains("Package") || n.Contains("Power")) ?? Max(gpu, SensorType.Power),
            VramUsedGb: vramGb);
    }

    /// <summary>First sensor value of the given type whose name matches, or null.</summary>
    private static double? Pick(IHardware hardware, SensorType type, Func<string, bool> nameMatch)
    {
        foreach (var s in hardware.Sensors)
        {
            if (s.SensorType == type && s.Value is { } v && !float.IsNaN(v) && nameMatch(s.Name))
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>Largest sensor value of the given type, or null.</summary>
    private static double? Max(IHardware hardware, SensorType type)
    {
        double? max = null;
        foreach (var s in hardware.Sensors)
        {
            if (s.SensorType == type && s.Value is { } v && !float.IsNaN(v) && (max is null || v > max))
            {
                max = v;
            }
        }

        return max;
    }

    /// <summary>VRAM used in GB: "GPU Memory Used" is SmallData (MB) on NVIDIA, Data (GB) elsewhere.</summary>
    private static (ISensor?, double?) FindVram(IHardware gpu)
    {
        ISensor? best = null;
        foreach (var s in gpu.Sensors)
        {
            var isMem = (s.SensorType is SensorType.SmallData or SensorType.Data)
                && s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase)
                && !s.Name.Contains("Shared", StringComparison.OrdinalIgnoreCase);
            if (isMem && s.Value is { } v && !float.IsNaN(v))
            {
                // prefer the dedicated/"GPU Memory Used" reading over D3D per-process ones
                if (best is null || s.Name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase))
                {
                    best = s;
                }
            }
        }

        if (best?.Value is not { } val)
        {
            return (null, null);
        }

        return (best, best.SensorType == SensorType.Data ? val : val / 1024.0);
    }

    public void Dispose() => _computer.Close();

    private static void Collect(IHardware hardware, List<SensorReading> readings, DateTimeOffset now)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } value || float.IsNaN(value)) continue;
            if (Map(sensor.SensorType) is not { } kind) continue;
            readings.Add(new SensorReading(
                sensor.Identifier.ToString(),
                $"{hardware.Name} / {sensor.Name}",
                kind,
                value,
                Unit(sensor.SensorType),
                now));
        }

        foreach (var sub in hardware.SubHardware)
            Collect(sub, readings, now);
    }

    private static SensorKind? Map(SensorType type) => type switch
    {
        SensorType.Temperature => SensorKind.Temperature,
        SensorType.Fan => SensorKind.FanRpm,
        SensorType.Control => SensorKind.DutyCycle,
        SensorType.Load => SensorKind.Load,
        SensorType.Flow => SensorKind.FlowRate,
        SensorType.Power => SensorKind.Power,
        SensorType.Clock => SensorKind.Clock,
        _ => null,
    };

    private static string Unit(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Fan => "RPM",
        SensorType.Control or SensorType.Load => "%",
        SensorType.Flow => "L/h",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        _ => "",
    };

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}
