using DeviceMaster.Core.Safety;
using DeviceMaster.Devices.CorsairLink.Protocol;

namespace DeviceMaster.Devices.CorsairLink;

/// <summary>A populated channel on a Link hub's chain.</summary>
public sealed record LinkChannel(int Channel, string Id, byte Model, byte Variant, LinkDeviceInfo? Info)
{
    public bool IsKnown => Info is not null;

    /// <summary>Unknown devices are treated as pumps — maximum caution.</summary>
    public bool IsPump => Info?.IsPump ?? true;

    public string Name => Info?.Name ?? $"UNKNOWN (model=0x{Model:X2}, variant=0x{Variant:X2})";
}

/// <summary>
/// Pure duty-map computation for a speed write. Every write to the hub covers all
/// speed-controllable channels, so this is where the safety policy lives:
/// - Pump channels get <paramref name="pumpDutyPercent"/> hard-floored at
///   SafetyLimits.PumpMinimumDutyPercent — fan-duty requests never reach a pump.
/// - Channels without a controllable speed (LCD modules, XC7 water blocks) are excluded.
/// - Fan duties are clamped to [0, 100]; fans without an explicit request get the
///   reference default of 50%.
/// - Any unknown device on the chain aborts the whole write (we cannot tell fan from pump).
/// </summary>
public static class LinkDutyPlanner
{
    public const int DefaultFanDutyPercent = 50;

    public static IReadOnlyDictionary<int, byte> BuildDutyMap(
        IReadOnlyList<LinkChannel> channels,
        IReadOnlyDictionary<int, int>? requestedDuties = null,
        int pumpDutyPercent = SafetyLimits.FailsafeDutyPercent)
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
            if (channel.IsPump)
            {
                duties[channel.Channel] = (byte)SafetyGuard.ClampPumpDuty(pumpDutyPercent);
                continue;
            }

            if (channel.Info is not { } info || !info.Flags.HasFlag(LinkDeviceFlags.ControlsSpeed))
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
