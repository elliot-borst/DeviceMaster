namespace DeviceMaster.Core.Safety;

/// <summary>
/// Non-negotiable limits for a water-cooled loop. These are enforced at the lowest device-write
/// layer; curve/UI layers may be stricter but never looser.
/// </summary>
public static class SafetyLimits
{
    /// <summary>Pump duty is never written below this, regardless of what any curve or user input says.</summary>
    public const int PumpMinimumDutyPercent = 50;

    /// <summary>Duty applied to pump and fans when anything goes wrong (sensor failure, protocol error, unknown state).</summary>
    public const int FailsafeDutyPercent = 100;

    /// <summary>
    /// Floor for motherboard SuperIO headers and GPU coolers. Unlike the Corsair/Lian Li
    /// paths, SuperIO has no hardware fallback — a crashed process leaves the last written
    /// duty in place — so these are never driven below a duty that keeps air moving.
    /// </summary>
    public const int HeaderMinimumDutyPercent = 30;
}

/// <summary>Pure clamping helpers so every write path shares the same, unit-tested safety behaviour.</summary>
public static class SafetyGuard
{
    /// <summary>Clamps a requested pump duty into [PumpMinimumDutyPercent, 100].</summary>
    public static int ClampPumpDuty(int requestedPercent) =>
        Math.Clamp(requestedPercent, SafetyLimits.PumpMinimumDutyPercent, 100);

    /// <summary>Clamps a requested fan duty into [0, 100].</summary>
    public static int ClampFanDuty(int requestedPercent) => Math.Clamp(requestedPercent, 0, 100);

    /// <summary>Clamps a motherboard-header/GPU duty into [HeaderMinimumDutyPercent, 100].</summary>
    public static int ClampHeaderDuty(int requestedPercent) =>
        Math.Clamp(requestedPercent, SafetyLimits.HeaderMinimumDutyPercent, 100);

    /// <summary>Duty to apply when a curve's temperature source failed to read.</summary>
    public static int DutyOnSensorFailure() => SafetyLimits.FailsafeDutyPercent;
}
