using DeviceMaster.Devices.CorsairLink;
using DeviceMaster.Devices.CorsairLink.Protocol;

namespace DeviceMaster.Core.Tests;

public class LinkHubPacketsTests
{
    [Fact]
    public void CommandPacket_HasReportIdHeaderAndCommandAtOffset3()
    {
        var packet = LinkHubPackets.CreateCommandPacket([0x01, 0x03, 0x00, 0x02]);

        Assert.Equal(LinkHubProtocol.PacketSizeOut, packet.Length);
        Assert.Equal(0x00, packet[0]); // report id
        Assert.Equal(0x00, packet[1]);
        Assert.Equal(0x01, packet[2]);
        Assert.Equal([0x01, 0x03, 0x00, 0x02], packet[3..7]);
        Assert.All(packet[7..], b => Assert.Equal(0x00, b));
    }

    [Fact]
    public void CommandPacket_AppendsDataAfterCommand()
    {
        var packet = LinkHubPackets.CreateCommandPacket([0x0d, 0x01], [0x36]);

        Assert.Equal(0x0d, packet[3]);
        Assert.Equal(0x01, packet[4]);
        Assert.Equal(0x36, packet[5]);
    }

    [Fact]
    public void WriteData_EncodesLengthTypeAndPayload()
    {
        byte[] data = [0xAA, 0xBB, 0xCC];
        var buffer = LinkHubPackets.CreateWriteData([0x07, 0x00], data);

        Assert.Equal(5, buffer[0]); // data length + 2, little endian
        Assert.Equal(0, buffer[1]);
        Assert.Equal(0, buffer[2]);
        Assert.Equal(0, buffer[3]);
        Assert.Equal(0x07, buffer[4]);
        Assert.Equal(0x00, buffer[5]);
        Assert.Equal(data, buffer[6..]);
    }

    [Fact]
    public void SpeedData_SerializesChannelDutyPairs()
    {
        var data = LinkHubPackets.CreateSoftwareSpeedFixedPercentData(
            new Dictionary<int, byte> { [1] = 50, [3] = 100 });

        Assert.Equal(9, data.Length);
        Assert.Equal(2, data[0]);
        Assert.Equal(1, data[1]);
        Assert.Equal(50, data[3]);
        Assert.Equal(3, data[5]);
        Assert.Equal(100, data[7]);
    }
}

public class LinkHubParserTests
{
    [Fact]
    public void FirmwareVersion_ParsesMajorMinorPatch()
    {
        var packet = new byte[16];
        packet[4] = 2;
        packet[5] = 5;
        packet[6] = 15; // patch, little-endian int16

        Assert.Equal("2.5.15", LinkHubParser.GetFirmwareVersion(packet));
    }

    [Fact]
    public void ParseSpeeds_ReadsAvailableAndUnavailableSensors()
    {
        var packet = new byte[16];
        packet[6] = 2;
        packet[7] = 0x00; // ch0 available
        packet[8] = 0xE8; // 1000 rpm little-endian
        packet[9] = 0x03;
        packet[10] = 0x01; // ch1 unavailable

        var speeds = LinkHubParser.ParseSpeeds(packet);

        Assert.Equal(2, speeds.Count);
        Assert.True(speeds[0].IsAvailable);
        Assert.Equal(1000, speeds[0].Rpm);
        Assert.False(speeds[1].IsAvailable);
        Assert.Null(speeds[1].Rpm);
    }

    [Fact]
    public void ParseTemperatures_ScalesTenthsOfDegree()
    {
        var packet = new byte[16];
        packet[6] = 1;
        packet[7] = 0x00;
        packet[8] = 0x4F; // 335 -> 33.5 °C
        packet[9] = 0x01;

        var temps = LinkHubParser.ParseTemperatures(packet);

        Assert.Single(temps);
        Assert.Equal(33.5f, temps[0].TemperatureCelsius);
    }

    private static byte[] BuildSubDevicePayload()
    {
        // lastChannel=3; ch1 = model 0x01 (QX fan) id "ABC1"; ch2 empty; ch3 = model 0x0c (XD5) id "XD5A"
        var payload = new List<byte> { 3 };
        payload.AddRange([0, 0, 0x01, 0x00, 0, 0, 0, 4]);
        payload.AddRange("ABC1"u8.ToArray());
        payload.AddRange(new byte[8]);
        payload.AddRange([0, 0, 0x0c, 0x00, 0, 0, 0, 4]);
        payload.AddRange("XD5A"u8.ToArray());
        payload.AddRange(new byte[8]); // trailing padding
        return payload.ToArray();
    }

    [Fact]
    public void ParseSubDevices_ReadsDevicesAndSkipsEmptyChannels()
    {
        var packet = new byte[6].Concat(BuildSubDevicePayload()).ToArray();

        var devices = LinkHubParser.ParseSubDevices(packet);

        Assert.Equal(2, devices.Count);
        Assert.Equal((1, "ABC1", (byte)0x01), (devices[0].Channel, devices[0].Id, devices[0].Model));
        Assert.Equal((3, "XD5A", (byte)0x0c), (devices[1].Channel, devices[1].Id, devices[1].Model));
    }

    [Fact]
    public void ParseSubDevices_MergesContinuationPacket()
    {
        var payload = BuildSubDevicePayload();
        var main = new byte[6].Concat(payload[..20]).ToArray();
        var continuation = new byte[4].Concat(payload[20..]).ToArray();

        var devices = LinkHubParser.ParseSubDevices(main, continuation);

        Assert.Equal(2, devices.Count);
        Assert.Equal("XD5A", devices[1].Id);
    }
}

public class LinkDutyPlannerTests
{
    private static LinkChannel Fan(int channel) =>
        new(channel, $"FAN{channel}", 0x01, 0x00, LinkDeviceCatalog.Find(0x01, 0x00));

    private static LinkChannel Pump(int channel) =>
        new(channel, $"PMP{channel}", 0x0c, 0x00, LinkDeviceCatalog.Find(0x0c, 0x00));

    private static LinkChannel Unknown(int channel) => new(channel, $"UNK{channel}", 0xEE, 0x00, null);

    [Fact]
    public void PumpChannels_AlwaysGetFailsafeDuty_IgnoringRequests()
    {
        var duties = LinkDutyPlanner.BuildDutyMap([Fan(1), Pump(2)], new Dictionary<int, int> { [1] = 70, [2] = 20 });

        Assert.Equal(70, duties[1]);
        Assert.Equal(100, duties[2]);
    }

    [Theory]
    [InlineData(150, 100)]
    [InlineData(-10, 0)]
    [InlineData(65, 65)]
    public void FanRequests_AreClamped(int requested, int expected)
    {
        var duties = LinkDutyPlanner.BuildDutyMap([Fan(1)], new Dictionary<int, int> { [1] = requested });
        Assert.Equal(expected, duties[1]);
    }

    [Fact]
    public void UnrequestedFans_GetReferenceDefault()
    {
        var duties = LinkDutyPlanner.BuildDutyMap([Fan(1), Fan(2)], new Dictionary<int, int> { [2] = 80 });

        Assert.Equal(LinkDutyPlanner.DefaultFanDutyPercent, duties[1]);
        Assert.Equal(80, duties[2]);
    }

    [Fact]
    public void UnknownDeviceOnChain_AbortsAllWrites() =>
        Assert.Throws<InvalidOperationException>(() => LinkDutyPlanner.BuildDutyMap([Fan(1), Unknown(2)]));

    [Fact]
    public void UnknownDevice_IsTreatedAsPump() => Assert.True(Unknown(1).IsPump);

    [Fact]
    public void NonSpeedDevices_AreExcludedFromWrites()
    {
        var lcd = new LinkChannel(13, "LCD", 0x0e, 0x00, LinkDeviceCatalog.Find(0x0e, 0x00));

        var duties = LinkDutyPlanner.BuildDutyMap([Fan(1), lcd]);

        Assert.True(lcd.IsKnown);
        Assert.False(duties.ContainsKey(13));
        Assert.Equal(LinkDutyPlanner.DefaultFanDutyPercent, duties[1]);
    }
}

public class LinkHubFirmwareTests
{
    [Theory]
    [InlineData("2.5.0", true)]
    [InlineData("2.9.1", true)]
    [InlineData("3.0.0", true)]
    [InlineData("2.4.9", false)]
    [InlineData("1.9.2", false)]
    [InlineData("?", false)]
    public void SupportsAdditionalSubDevices_ChecksFirmware25(string firmware, bool expected) =>
        Assert.Equal(expected, LinkHub.SupportsAdditionalSubDevices(firmware));
}
