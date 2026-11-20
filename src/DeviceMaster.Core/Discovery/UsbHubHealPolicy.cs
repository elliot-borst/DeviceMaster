namespace DeviceMaster.Core.Discovery;

/// <summary>
/// When to re-enumerate a USB hub whose ports failed to enumerate (#101) — pure, so it is tested.
/// A failure must be seen for <see cref="ConfirmMs"/> first (a port mid-enumeration can flicker
/// through a failure state), then each hub is healed with backoff, at most
/// <see cref="BackoffMs"/>.Length + 1 times, until its ports have stayed healthy for
/// <see cref="ClearMs"/>. A hub cycle drops the hub off the bus for a moment, so "healthy" is only
/// believed after a while — otherwise a hub that fails again on every cycle would be cycled forever.
/// </summary>
public sealed class UsbHubHealPolicy
{
    public const long ConfirmMs = 10_000;
    public const long ClearMs = 300_000;
    public static readonly long[] BackoffMs = [30_000, 120_000, 600_000];

    private sealed class Track
    {
        public long FirstFailed;
        public long LastFailed;
        public int Attempts;
        public long NextAllowed;
        public bool GaveUp;
    }

    private readonly Dictionary<string, Track> _hubs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What to do for one hub this round.</summary>
    public sealed record Decision(string HubInstanceId, bool Heal, bool GiveUp, int Attempt);

    /// <summary>
    /// Feeds one scan (the hubs that have at least one failed port right now) and returns the hubs
    /// to heal (<c>Heal</c>) or to report as given up on (<c>GiveUp</c>, once).
    /// </summary>
    public IReadOnlyList<Decision> Next(IEnumerable<string> failedHubs, long now)
    {
        var decisions = new List<Decision>();
        var failed = new HashSet<string>(failedHubs, StringComparer.OrdinalIgnoreCase);
        foreach (var hub in failed)
        {
            if (!_hubs.TryGetValue(hub, out var track))
            {
                _hubs[hub] = track = new Track { FirstFailed = now };
            }

            track.LastFailed = now;
            if (track.GaveUp || now - track.FirstFailed < ConfirmMs || now < track.NextAllowed)
            {
                continue;
            }

            if (track.Attempts > BackoffMs.Length)
            {
                track.GaveUp = true;
                decisions.Add(new Decision(hub, Heal: false, GiveUp: true, track.Attempts));
                continue;
            }

            track.Attempts++;
            track.NextAllowed = now + BackoffMs[Math.Min(track.Attempts - 1, BackoffMs.Length - 1)];
            decisions.Add(new Decision(hub, Heal: true, GiveUp: false, track.Attempts));
        }

        foreach (var hub in _hubs.Where(h => !failed.Contains(h.Key) && now - h.Value.LastFailed >= ClearMs).Select(h => h.Key).ToList())
        {
            _hubs.Remove(hub);
        }

        return decisions;
    }
}
