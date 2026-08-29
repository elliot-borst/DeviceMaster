using DeviceMaster.Devices.CorsairLink;
using DeviceMaster.Devices.CorsairLink.Protocol;

namespace DeviceMaster.Core.Tests;

public class LedRegistryPlannerTests
{
    // a healthy 6-fan chain of RX MAX RGB fans: registry code 0x19 read back live
    private static readonly Dictionary<int, byte> SixFanChain = new()
    {
        [1] = 0x19, [2] = 0x19, [3] = 0x19, [13] = 0x19, [14] = 0x19, [15] = 0x19,
    };

    [Fact]
    public void HealthyRegistry_NeedsNoWrite()
    {
        var current = SixFanChain.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Null(LedRegistryPlanner.Compute(current, SixFanChain));
    }

    [Fact]
    public void PartialEnrollment_IsLeftToTheHub()
    {
        // channels missing from the registry are the hub's own enrollment to complete —
        // the planner must never add entries for them
        var current = new Dictionary<int, byte> { [2] = 0x19, [13] = 0x19 };

        Assert.Null(LedRegistryPlanner.Compute(current, SixFanChain));
    }

    [Fact]
    public void PhantomChannels_AreDropped()
    {
        var current = new Dictionary<int, byte> { [2] = 0x19, [7] = 0x19, [13] = 0x19 };

        var plan = LedRegistryPlanner.Compute(current, SixFanChain);

        Assert.NotNull(plan);
        Assert.Equal([7], plan.Phantoms);
        Assert.Empty(plan.CodeRepairs);
        Assert.Equal(new Dictionary<int, byte> { [2] = 0x19, [13] = 0x19 }, plan.Target);
    }

    [Fact]
    public void JunkCodes_AreRepairedFromTheCatalog()
    {
        // the corrupted-flash state seen live 2026-08-29: enrolled entries carrying junk codes
        var current = new Dictionary<int, byte> { [2] = 0x01, [13] = 0x00, [15] = 0x00 };

        var plan = LedRegistryPlanner.Compute(current, SixFanChain);

        Assert.NotNull(plan);
        Assert.Empty(plan.Phantoms);
        Assert.Equal([2, 13, 15], plan.CodeRepairs);
        Assert.Equal(new Dictionary<int, byte> { [2] = 0x19, [13] = 0x19, [15] = 0x19 }, plan.Target);
    }

    [Fact]
    public void UnknownCatalogCode_KeepsWhatTheHubUses()
    {
        // catalog code 0 = unknown: the read-back code is kept verbatim, never "repaired"
        var chain = new Dictionary<int, byte> { [13] = 0x00 };
        var current = new Dictionary<int, byte> { [13] = 0x42 };

        Assert.Null(LedRegistryPlanner.Compute(current, chain));
    }

    [Fact]
    public void GarbageParse_WouldBeFullyRecognized()
    {
        // shape of the misparsed foreign packet that used to drive flash writes: entries on
        // odd channels 17..51 alongside the three real ones — everything junk must go, the
        // real channels must come out repaired
        var current = new Dictionary<int, byte> { [2] = 0x01, [13] = 0x00, [15] = 0x00 };
        for (var ch = 17; ch <= 51; ch += 2)
        {
            current[ch] = 0x2C;
        }

        var plan = LedRegistryPlanner.Compute(current, SixFanChain);

        Assert.NotNull(plan);
        Assert.Equal(Enumerable.Range(0, 18).Select(i => 17 + i * 2), plan.Phantoms);
        Assert.Equal([2, 13, 15], plan.CodeRepairs);
        Assert.Equal([2, 13, 15], plan.Target.Keys.OrderBy(k => k));
        Assert.All(plan.Target.Values, code => Assert.Equal(0x19, code));
    }

    [Fact]
    public void PlanSignature_IsStableAcrossReadOrder()
    {
        var chain = SixFanChain;
        var a = LedRegistryPlanner.Compute(
            new Dictionary<int, byte> { [2] = 0x01, [7] = 0x10, [13] = 0x00 }, chain);
        var b = LedRegistryPlanner.Compute(
            new Dictionary<int, byte> { [13] = 0x00, [2] = 0x01, [7] = 0x10 }, chain);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a.Signature, b.Signature);
    }

    [Fact]
    public void ParseLedRegistry_DecodesTheLiveDump()
    {
        // 0x1E raw captured from the corrupted fan hub (fw 3.10.636, 2026-08-29) — anchors
        // the wire format: [4,5]=data type 0x0D00, [6]=slot count, then per channel either
        // 0x00 (empty) or 0x01 + command code
        var packet = Convert.FromHexString(
            "000008000D00100000010100000000000000000000010000010000000000000000000000000000000000000000000000");

        var registry = LinkHubParser.ParseLedRegistry(packet);

        Assert.Equal(new Dictionary<int, byte> { [2] = 0x01, [13] = 0x00, [15] = 0x00 }, registry);
    }
}
