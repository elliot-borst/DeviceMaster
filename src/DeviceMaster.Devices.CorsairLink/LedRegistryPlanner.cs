namespace DeviceMaster.Devices.CorsairLink;

/// <summary>
/// Pure planner for LED registry maintenance (endpoint 0x1E). Given the registry read back
/// from the hub and the LED-capable channels on the enumerated chain, decides what — if
/// anything — is worth writing back. Deliberately conservative:
///  - drops PHANTOM entries (registered channels with no device on the chain),
///  - repairs an entry whose command code contradicts the catalog-known code of the device
///    actually on that channel (misparsed reads written back to flash left junk codes and
///    dark fans, 2026-08),
///  - never ADDS entries (LED enrollment is the hub's own process) and never invents codes.
/// </summary>
public static class LedRegistryPlanner
{
    public sealed record Plan(
        IReadOnlyList<int> Phantoms,
        IReadOnlyList<int> CodeRepairs,
        IReadOnlyDictionary<int, byte> Target)
    {
        /// <summary>Stable fingerprint so callers can demand the same plan on consecutive reads.</summary>
        public string Signature =>
            "ph:" + string.Join(",", Phantoms)
            + "|fix:" + string.Join(",", CodeRepairs)
            + "|" + string.Join(",", Target.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value:X2}"));
    }

    /// <summary>
    /// <paramref name="chainLedCodes"/> maps each LED-capable chain channel to its catalog
    /// LED command code (0 = code unknown). Returns null when the registry needs no write.
    /// </summary>
    public static Plan? Compute(
        IReadOnlyDictionary<int, byte> current,
        IReadOnlyDictionary<int, byte> chainLedCodes)
    {
        var phantoms = current.Keys.Where(ch => !chainLedCodes.ContainsKey(ch)).OrderBy(ch => ch).ToList();
        var repairs = new List<int>();
        var target = new Dictionary<int, byte>();

        foreach (var (channel, code) in current)
        {
            if (!chainLedCodes.TryGetValue(channel, out var catalogCode))
            {
                continue; // phantom — dropped from the target
            }

            if (catalogCode != 0 && code != catalogCode)
            {
                repairs.Add(channel);
                target[channel] = catalogCode;
            }
            else
            {
                target[channel] = code;
            }
        }

        repairs.Sort();
        return phantoms.Count == 0 && repairs.Count == 0 ? null : new Plan(phantoms, repairs, target);
    }
}
