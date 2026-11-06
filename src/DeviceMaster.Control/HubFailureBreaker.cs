namespace DeviceMaster.Control;

/// <summary>
/// Circuit breaker for Link hub sessions. The 2026-10-30 incident (ServerStack #211): after a
/// 5 s USB blip the pump hub reopened fine but EVERY telemetry read threw, so the hub was
/// disposed and reopened every ~6 s forever — no duty ever reached the pump and the fan hub's
/// hardware failsafe sat at 100%. Rediscovery cannot tell that loop from a slow bus, so this
/// breaker counts recent session failures per hub path and trips after a threshold within a
/// sliding window. While tripped, the path is backed off (no reopen hammering, hardware failsafe
/// stays active) and the caller escalates to a software USB replug. Pure and clock-injected so
/// the flap logic has tests.
/// </summary>
public sealed class HubFailureBreaker
{
    /// <summary>Default backoff a tripped hub path gets (90 s of no reopen attempts).</summary>
    public const long DefaultBackoffMs = 90_000;

    private readonly int _threshold;
    private readonly long _windowMs;
    private readonly long _backoffMs;
    private readonly Dictionary<string, List<long>> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _backoffUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="threshold">Session failures within the window that trip the breaker.</param>
    /// <param name="windowMs">Sliding window a failure counts in.</param>
    /// <param name="backoffMs">How long a tripped path is left closed before a fresh attempt.</param>
    public HubFailureBreaker(int threshold = 3, long windowMs = 600_000, long backoffMs = DefaultBackoffMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        _threshold = threshold;
        _windowMs = windowMs;
        _backoffMs = backoffMs;
    }

    /// <summary>True while the path is tripped and must not be reopened.</summary>
    public bool IsBackedOff(string devicePath, long nowMs) =>
        _backoffUntil.TryGetValue(devicePath, out var until) && nowMs < until;

    /// <summary>
    /// Records one failed session for the path. Returns true when this failure TRIPS the breaker
    /// (the caller should escalate); a trip also starts the backoff. Failures after a trip count
    /// toward the next trip only once the backoff has expired.
    /// </summary>
    public bool Note(string devicePath, long nowMs)
    {
        if (!_failures.TryGetValue(devicePath, out var stamps))
        {
            _failures[devicePath] = stamps = [];
        }

        stamps.RemoveAll(t => nowMs - t > _windowMs);
        stamps.Add(nowMs);

        if (stamps.Count < _threshold)
        {
            return false;
        }

        // trip: back the path off and reset its count so the next trip needs a fresh burst
        _backoffUntil[devicePath] = nowMs + _backoffMs;
        stamps.Clear();
        return true;
    }

    /// <summary>Clears all history for a path (e.g. after a healthy session, or on shutdown).</summary>
    public void Forget(string devicePath)
    {
        _failures.Remove(devicePath);
        _backoffUntil.Remove(devicePath);
    }
}
