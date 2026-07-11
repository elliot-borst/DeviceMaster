namespace DeviceMaster.Core.Sensors;

public enum SensorKind { Temperature, FanRpm, PumpRpm, DutyCycle, Load, FlowRate, Power, Clock }

public sealed record SensorReading(
    string Id, string Name, SensorKind Kind, double Value, string Unit, DateTimeOffset Timestamp);

/// <summary>
/// A source of hardware readings. LibreHardwareMonitor is the first implementation; later stages
/// add hub-reported sources (coolant temperature and RPMs from the iCUE Link hub).
/// </summary>
public interface ISensorSource : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Refresh and return current readings. Implementations must throw on total failure rather
    /// than return stale data — callers treat failure as the failsafe trigger (fans/pump to 100%).
    /// </summary>
    IReadOnlyList<SensorReading> Read();
}
