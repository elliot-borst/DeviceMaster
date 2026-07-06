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
        };
        _computer.Open();
    }

    public string Name => "LibreHardwareMonitor";

    public IReadOnlyList<SensorReading> Read()
    {
        _computer.Accept(_visitor);
        var now = DateTimeOffset.Now;
        var readings = new List<SensorReading>();
        foreach (var hardware in _computer.Hardware)
            Collect(hardware, readings, now);
        return readings;
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
        _ => null,
    };

    private static string Unit(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Fan => "RPM",
        SensorType.Control or SensorType.Load => "%",
        SensorType.Flow => "L/h",
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
