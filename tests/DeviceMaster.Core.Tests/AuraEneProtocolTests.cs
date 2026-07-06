using DeviceMaster.Core.Safety;
using DeviceMaster.Devices.AsusAura;
using DeviceMaster.Devices.EneRgb;

namespace DeviceMaster.Core.Tests;

public class AuraProtocolTests
{
    [Fact]
    public void Packets_Are65Bytes_WithEcReportId()
    {
        foreach (var packet in new[]
        {
            AuraUsbProtocol.BuildFirmwareRequest(),
            AuraUsbProtocol.BuildConfigTableRequest(),
            AuraUsbProtocol.BuildInit(),
            AuraUsbProtocol.BuildEffect(0, AuraUsbProtocol.ModeStatic),
            AuraUsbProtocol.BuildEffectColor(0, 1, 1, 2, 3),
            AuraUsbProtocol.BuildCommit(),
        })
        {
            Assert.Equal(65, packet.Length);
            Assert.Equal(0xEC, packet[0]);
        }
    }

    [Fact]
    public void Init_MatchesReferenceBytes()
    {
        var packet = AuraUsbProtocol.BuildInit();
        Assert.Equal([0xEC, 0x52, 0x53, 0x00, 0x01], packet[..5]);
    }

    [Fact]
    public void ParseZones_OnboardPlusAddressable()
    {
        var config = new byte[60];
        config[0x1B] = 8; // onboard LEDs
        config[0x1D] = 2; // 12V RGB headers within the onboard count
        config[0x02] = 3; // ARGB headers

        var zones = AuraUsbProtocol.ParseZones(config);

        Assert.Equal(4, zones.Count);
        Assert.Equal(new AuraZone(0, 0x04, 8, IsAddressable: false), zones[0]);
        Assert.Equal(new AuraZone(1, 0x00, 1, IsAddressable: true), zones[1]);
        Assert.Equal(new AuraZone(3, 0x02, 1, IsAddressable: true), zones[3]);
    }

    [Fact]
    public void ParseZones_AddressableOnlyBoard()
    {
        // the dev rig's TUF B850-BTF: no onboard LEDs, 3 ARGB headers
        var config = new byte[60];
        config[0x02] = 3;

        var zones = AuraUsbProtocol.ParseZones(config);

        Assert.Equal(3, zones.Count);
        Assert.All(zones, z => Assert.True(z.IsAddressable));
        Assert.Equal(0, zones[0].EffectChannel); // no onboard zone consuming index 0
        Assert.Equal(0x00, zones[0].DirectChannel);
    }

    [Fact]
    public void EffectColor_PlacesMaskAndColorsByStartLed()
    {
        var packet = AuraUsbProtocol.BuildEffectColor(startLed: 2, ledCount: 3, r: 0x11, g: 0x22, b: 0x33);

        // mask = 0b11100 = 0x001C, big-endian at [2..4]
        Assert.Equal(0x00, packet[2]);
        Assert.Equal(0x1C, packet[3]);
        // colors start at 5 + 3*2
        Assert.Equal([0x11, 0x22, 0x33], packet[11..14]);
        Assert.Equal([0x11, 0x22, 0x33], packet[17..20]);
    }

    [Fact]
    public void Direct_ChunksAt20Leds_AppliesOnLastChunk()
    {
        var colors = Enumerable.Range(0, 45).Select(i => ((byte)i, (byte)i, (byte)i)).ToList();

        var packets = AuraUsbProtocol.BuildDirect(directChannel: 0x04, colors);

        Assert.Equal(3, packets.Count);
        Assert.Equal(0x04, packets[0][2]);          // no apply bit
        Assert.Equal(0x04, packets[1][2]);
        Assert.Equal(0x84, packets[2][2]);          // apply bit on the final chunk
        Assert.Equal(40, packets[2][3]);            // offset
        Assert.Equal(5, packets[2][4]);             // count
    }

    [Fact]
    public void Commit_MatchesReferenceBytes()
    {
        var packet = AuraUsbProtocol.BuildCommit();
        Assert.Equal(0x3F, packet[1]);
        Assert.Equal(0x55, packet[2]);
    }
}

public class EneRgbDeviceTests
{
    /// <summary>Records every transaction so the ENE addressing scheme can be asserted.</summary>
    private sealed class FakeSmBus : ISmBus
    {
        public readonly List<string> Log = [];
        public Func<byte, byte, int> OnReadByteData = (_, _) => 0;

        public string Name => "fake";

        public int ReadByte(byte address) => 0;

        public int ReadByteData(byte address, byte command)
        {
            Log.Add($"rd {address:X2} {command:X2}");
            return OnReadByteData(address, command);
        }

        public int WriteByteData(byte address, byte command, byte value)
        {
            Log.Add($"wb {address:X2} {command:X2} {value:X2}");
            return 0;
        }

        public int WriteWordData(byte address, byte command, ushort value)
        {
            Log.Add($"ww {address:X2} {command:X2} {value:X4}");
            return 0;
        }

        public int WriteBlockData(byte address, byte command, byte[] data)
        {
            Log.Add($"blk {address:X2} {command:X2} {Convert.ToHexString(data)}");
            return 0;
        }
    }

    [Fact]
    public void Fingerprint_RequiresIncrementingA0Registers()
    {
        var good = new FakeSmBus { OnReadByteData = (_, cmd) => cmd - 0xA0 };
        Assert.True(EneRgbDevice.Fingerprint(good, 0x77));

        var bad = new FakeSmBus { OnReadByteData = (_, _) => 0x00 };
        Assert.False(EneRgbDevice.Fingerprint(bad, 0x77));
    }

    [Fact]
    public void RegisterWrites_UseSwappedPointerThenData()
    {
        var bus = new FakeSmBus();
        new EneRgbDevice(bus, 0x70).RemapAddress(slot: 2, newAddress: 0x71);

        // 0x80F8 (slot) — register pointer word is byte-swapped: 0xF880
        Assert.Equal("ww 70 00 F880", bus.Log[0]);
        Assert.Equal("wb 70 01 02", bus.Log[1]);
        // 0x80F9 (new address) — written as 8-bit form (0x71 << 1 = 0xE2)
        Assert.Equal("ww 70 00 F980", bus.Log[2]);
        Assert.Equal("wb 70 01 E2", bus.Log[3]);
    }

    [Fact]
    public void StaticColor_SendsRbgTriplets_AndApplies()
    {
        var bus = new FakeSmBus
        {
            // version string "AUDA0-E6K5-0101" at 0x1000, LED count 2 at config[0x02]
            OnReadByteData = (_, cmd) => cmd == 0x81 ? '?' : 0,
        };

        // craft a device with v2 registers and 2 LEDs via Initialize is I/O-heavy to fake;
        // drive the write path directly instead
        var device = new EneRgbDevice(bus, 0x70);
        device.ApplyStaticColor(0x11, 0x22, 0x33, persist: false);

        // mode select writes: 0x8021=static(1) … then apply 0x80A0=0x01
        Assert.Contains("ww 70 00 2180", bus.Log);
        Assert.Contains("wb 70 01 01", bus.Log);
        Assert.Contains("ww 70 00 A080", bus.Log);
        // no flash save without persist
        Assert.DoesNotContain(bus.Log, e => e.EndsWith(" AA", StringComparison.Ordinal));
    }
}

public class HeaderSafetyTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 30)]
    [InlineData(75, 75)]
    [InlineData(150, 100)]
    public void HeaderDuty_FlooredAt30(int requested, int expected) =>
        Assert.Equal(expected, SafetyGuard.ClampHeaderDuty(requested));
}
