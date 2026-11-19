namespace DeviceMaster.Core.Sensors;

public enum SensorKind { Temperature, FanRpm, DutyCycle, Load, Power, Clock }

public sealed record SensorReading(
    string Id, string Name, SensorKind Kind, double Value, string Unit, DateTimeOffset Timestamp);

/// <summary>
/// A source of hardware readings (LibreHardwareMonitor on Windows, hwmon on Linux).
/// </summary>
public interface ISensorSource : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Refresh and return current readings. Implementations must throw on total failure rather
    /// than return stale data — callers treat failure as the failsafe trigger (fans to 100%).
    /// </summary>
    IReadOnlyList<SensorReading> Read();
}
