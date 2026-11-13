using DeviceMaster.Core.Safety;

namespace DeviceMaster.Core.Curves;

/// <summary>
/// The "hottest of CPU and GPU" curve input (#33): one fleet-wide fan curve that reacts to
/// whichever part of the machine is working hardest. Pure functions — both loops feed them their
/// own sensor readings.
/// </summary>
public static class HottestTemperature
{
    /// <summary>
    /// Combines the two readings. Null (= failsafe 100 %) when the CPU reading is unusable, or when
    /// the machine has a GPU (<paramref name="gpuExpected"/>) whose reading is unusable: falling
    /// back to the CPU alone would hide a hot GPU. A machine without a GPU uses the CPU alone.
    /// </summary>
    public static double? Combine(double? cpuC, double? gpuC, bool gpuExpected)
    {
        if (!SensorValidity.IsPlausibleTemperature(cpuC))
        {
            return null;
        }

        if (!SensorValidity.IsPlausibleTemperature(gpuC))
        {
            return gpuExpected ? null : cpuC;
        }

        return Math.Max(cpuC!.Value, gpuC!.Value);
    }
}

/// <summary>
/// Smooths a die temperature for the fan curve so CPU boost spikes (a few seconds at +20 °C) don't
/// make the fans rev up and down. Rises with a short time constant (a real load still ramps the
/// fans within seconds) and falls with a longer one (no hunting as a load comes and goes). An
/// unusable reading resets it, so the failsafe is never delayed or masked.
/// </summary>
public sealed class TemperatureSmoother
{
    public const double DefaultRiseSeconds = 3, DefaultFallSeconds = 15;

    private readonly double _riseSeconds;
    private readonly double _fallSeconds;
    private double? _value;
    private long _lastMs;

    public TemperatureSmoother(double riseSeconds = DefaultRiseSeconds, double fallSeconds = DefaultFallSeconds)
    {
        _riseSeconds = riseSeconds;
        _fallSeconds = fallSeconds;
    }

    /// <summary>The next smoothed value for <paramref name="rawC"/> at <paramref name="nowMs"/>; null passes straight through.</summary>
    public double? Next(double? rawC, long nowMs)
    {
        if (!SensorValidity.IsPlausibleTemperature(rawC))
        {
            _value = null; // failsafe now, and start fresh once the sensor is back
            return null;
        }

        var raw = rawC!.Value;
        if (_value is not { } previous)
        {
            _value = raw;
        }
        else
        {
            var dt = Math.Clamp((nowMs - _lastMs) / 1000.0, 0, 60);
            var tau = raw > previous ? _riseSeconds : _fallSeconds;
            var alpha = tau <= 0 ? 1 : 1 - Math.Exp(-dt / tau);
            _value = previous + (raw - previous) * alpha;
        }

        _lastMs = nowMs;
        return _value;
    }
}
