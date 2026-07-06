using DeviceMaster.Devices.CorsairLink;
using DeviceMaster.Devices.CorsairLink.Protocol;
using DeviceMaster.Devices.LianLi;

namespace DeviceMaster.Core.Tests;

public class TinyUzTests
{
    [Fact]
    public void SingleLiteral_MatchesReferenceEncoder()
    {
        // Golden value hand-computed from the reference Python encoder:
        // dict header 4096 LE, one type byte (literal bit + ctrl-3 bits = 0x19),
        // the literal, and the closing dictionary position.
        var encoded = TinyUz.Compress([0xAA]);

        Assert.Equal([0x00, 0x10, 0x00, 0x00, 0x19, 0xAA, 0x00], encoded);
    }

    [Fact]
    public void Stream_StartsWithDictHeader_AndEndsWithZeroPos()
    {
        var encoded = TinyUz.Compress(new byte[120]);

        Assert.Equal([0x00, 0x10, 0x00, 0x00], encoded[..4]);
        Assert.Equal(0x00, encoded[^1]);
        // 120 literals + interleaved type bytes + header/terminator
        Assert.InRange(encoded.Length, 120 + 4, 120 + 4 + 120 / 8 + 4);
    }

    [Fact]
    public void EmptyPayload_Throws() =>
        Assert.Throws<ArgumentException>(() => TinyUz.Compress([]));
}

public class Slv3RgbPacketTests
{
    private static readonly byte[] DeviceMac = [1, 2, 3, 4, 5, 6];
    private static readonly byte[] MasterMac = [9, 8, 7, 6, 5, 4];
    private static readonly byte[] Effect = [0xDE, 0xAD, 0xBE, 0xEF];

    [Fact]
    public void Payloads_HaveRepeatedHeaderAndChunkedData()
    {
        var compressed = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(); // -> 2 chunks

        var payloads = Slv3Protocol.BuildRgbRfPayloads(
            DeviceMac, MasterMac, Effect, compressed, ledCount: 120, totalFrames: 1, intervalMs: 5000,
            headerRepeats: 3);

        Assert.Equal(3 + 2, payloads.Count); // 3× header + 2 data packets

        var header = payloads[0];
        Assert.Equal(0x12, header[0]);
        Assert.Equal(0x20, header[1]);
        Assert.Equal(DeviceMac, header[2..8]);
        Assert.Equal(MasterMac, header[8..14]);
        Assert.Equal(Effect, header[14..18]);
        Assert.Equal(0, header[18]);            // packet index
        Assert.Equal(2 + 1, header[19]);        // total packets + 1
        Assert.Equal(300, (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23]);
        Assert.Equal(1, (header[25] << 8) | header[26]);
        Assert.Equal(120, header[27]);
        Assert.Equal(5000, (header[32] << 8) | header[33]);
        Assert.Equal(payloads[0], payloads[2]); // header repeats are identical

        var data1 = payloads[3];
        Assert.Equal(1, data1[18]);
        Assert.Equal(compressed[..220], data1[20..240]);
        var data2 = payloads[4];
        Assert.Equal(2, data2[18]);
        Assert.Equal(compressed[220..300], data2[20..100]);
    }
}

public class LinkLedCatalogTests
{
    // LED counts per model from OpenLinkHub's device metadata (lsh.json) — the hub's own
    // LED enumeration reports 0 connected on fw 3.10, so the catalog sizes the color buffer.
    [Theory]
    [InlineData((byte)LinkDeviceModel.FanQxSeries, (byte)0x00, 34)]
    [InlineData((byte)LinkDeviceModel.FanLxSeries, (byte)0x00, 18)]
    [InlineData((byte)LinkDeviceModel.FanRxMaxRgbSeries, (byte)0x00, 8)]
    [InlineData((byte)LinkDeviceModel.FanRxRgbSeries, (byte)0x00, 8)]
    [InlineData((byte)LinkDeviceModel.PumpXd5Series, (byte)0x00, 22)]
    [InlineData((byte)LinkDeviceModel.PumpXd6Series, (byte)0x00, 22)]
    [InlineData((byte)LinkDeviceModel.WaterBlockXc7Series, (byte)0x00, 24)]
    [InlineData((byte)LinkDeviceModel.LiquidCoolerHSeries, (byte)0x02, 20)]
    public void RgbModels_HaveReferenceLedCounts(byte model, byte variant, int expectedLeds) =>
        Assert.Equal(expectedLeds, LinkDeviceCatalog.Find(model, variant)!.LedCount);

    [Theory]
    [InlineData((byte)LinkDeviceModel.FanRxSeries, (byte)0x00)]        // non-RGB RX
    [InlineData((byte)LinkDeviceModel.FanRxMaxSeries, (byte)0x00)]     // non-RGB RX MAX
    [InlineData((byte)LinkDeviceModel.LcdXd5Elite, (byte)0x00)]        // display module
    [InlineData((byte)LinkDeviceModel.CommanderDuoSeries, (byte)0x00)] // dynamic count via endpoint 0x20
    public void NonRgbModels_HaveZeroLeds(byte model, byte variant) =>
        Assert.Equal(0, LinkDeviceCatalog.Find(model, variant)!.LedCount);
}

public class LinkLedRegistryTests
{
    /// <summary>0x1E payload captured from a live fan hub (fw 3.10.636) with a stale registry.</summary>
    private static byte[] CapturedFanHubRegistry()
    {
        var packet = new byte[64];
        Convert.FromHexString("000008000D00100000011901190000000000000000000119000119000000").CopyTo(packet, 0);
        return packet;
    }

    [Fact]
    public void ParseLedRegistry_DecodesLiveCapture()
    {
        var registry = LinkHubParser.ParseLedRegistry(CapturedFanHubRegistry());

        // the hub believed LED devices lived on channels 2, 3, 13, 15 (all code 0x19) while
        // the chain actually carried fans on 1, 2, 3, 13, 14, 15 — ch1/ch14 stayed dark
        Assert.Equal(new Dictionary<int, byte> { [2] = 0x19, [3] = 0x19, [13] = 0x19, [15] = 0x19 }, registry);
    }

    [Fact]
    public void ParseLedRegistry_PumpHubSingleDevice()
    {
        var packet = new byte[64];
        Convert.FromHexString("000008000D000F000000000000000000000000000001110000").CopyTo(packet, 0);

        var registry = LinkHubParser.ParseLedRegistry(packet);

        Assert.Equal(new Dictionary<int, byte> { [14] = 0x11 }, registry);
    }

    [Fact]
    public void CreateLedRegistryData_RoundTripsThroughParser()
    {
        var codes = new Dictionary<int, byte> { [1] = 0x19, [2] = 0x19, [3] = 0x19, [13] = 0x19, [14] = 0x19, [15] = 0x19 };

        var data = LinkHubPackets.CreateLedRegistryData(maxChannel: 15, codes);

        // wrap as a response payload: registry entries start at [7], count at [6]
        var packet = new byte[7 + data.Length];
        packet[6] = data[0];
        data.AsSpan(1).CopyTo(packet.AsSpan(7));

        Assert.Equal(16, data[0]);         // slots = max channel + 1
        Assert.Equal(0x00, data[1]);       // channel 0 always empty
        Assert.Equal(codes, LinkHubParser.ParseLedRegistry(packet));
    }
}

public class LinkLedParsingTests
{
    [Fact]
    public void ParseLedCounts_ReadsConnectedChannels()
    {
        // stripped packet: [6]=channel count, data at [7]; channel i at data[i*4]:
        // [status u16le][led count u16le], status 2 = connected
        var packet = new byte[7 + 4 * 4];
        packet[6] = 3;
        var data = packet.AsSpan(7);
        data[1 * 4] = 2; data[1 * 4 + 2] = 16;  // ch1: connected, 16 LEDs
        data[2 * 4] = 1;                        // ch2: not connected
        data[3 * 4] = 2; data[3 * 4 + 2] = 34;  // ch3: connected, 34 LEDs

        var counts = LinkHubParser.ParseLedCounts(packet);

        Assert.Equal(2, counts.Count);
        Assert.Equal(16, counts[1]);
        Assert.Equal(34, counts[3]);
        Assert.False(counts.ContainsKey(2));
    }
}
