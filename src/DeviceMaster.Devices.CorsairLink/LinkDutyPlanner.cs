using DeviceMaster.Core.Safety;

namespace DeviceMaster.Devices.CorsairLink;

/// <summary>A populated channel on a Link hub's chain.</summary>
public sealed record LinkChannel(int Channel, string Id, byte Model, byte Variant, LinkDeviceInfo? Info)
{
    public bool IsKnown => Info is not null;

    /// <summary>A fan (or other cooler) whose speed this hub lets software set.</summary>
    public bool ControlsSpeed => Info?.Flags.HasFlag(LinkDeviceFlags.ControlsSpeed) == true;

    public string Name => Info?.Name ?? $"UNKNOWN (model=0x{Model:X2}, variant=0x{Variant:X2})";
}

/// <summary>
/// Pure duty-map computation for a speed write. Every write to the hub covers all
/// speed-controllable channels, so this is where the safety policy lives:
/// - Channels without a controllable speed are excluded.
/// - Fan duties are clamped to [0, 100]; fans without an explicit request get the
///   reference default of 50%.
/// - Any unknown device on the chain aborts the whole write (it could be a pump or anything
///   else we must not drive). Liquid-cooling devices are deliberately not catalogued, so a
///   chain with a pump is never written to.
/// </summary>
public static class LinkDutyPlanner
{
    public const int DefaultFanDutyPercent = 50;

    public static IReadOnlyDictionary<int, byte> BuildDutyMap(
        IReadOnlyList<LinkChannel> channels,
        IReadOnlyDictionary<int, int>? requestedDuties = null)
    {
        var unknown = channels.Where(c => !c.IsKnown).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                "Refusing to write speeds: unrecognized device(s) on the Link chain: "
                + string.Join(", ", unknown.Select(c => $"channel {c.Channel} {c.Name}"))
                + ". Add them to LinkDeviceCatalog first.");
        }

        var duties = new Dictionary<int, byte>(channels.Count);
        foreach (var channel in channels)
        {
            if (!channel.ControlsSpeed)
            {
                continue;
            }

            var requested = requestedDuties is not null && requestedDuties.TryGetValue(channel.Channel, out var r)
                ? r
                : DefaultFanDutyPercent;
            duties[channel.Channel] = (byte)SafetyGuard.ClampFanDuty(requested);
        }

        return duties;
    }
}
