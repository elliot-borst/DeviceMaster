using DeviceMaster.Devices.LianLi;

namespace DeviceMaster.Core.Tests;

public class Slv3ProtocolTests
{
    private static readonly byte[] DeviceMac = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
    private static readonly byte[] MasterMac = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];

    [Fact]
    public void PwmRfData_MatchesReferenceLayout()
    {
        var data = Slv3Protocol.BuildPwmRfData(DeviceMac, MasterMac, rxType: 3, masterChannel: 8, sequenceIndex: 2, [64, 64, 64, 0]);

        Assert.Equal(Slv3Protocol.RfDataSize, data.Length);
        Assert.Equal(0x12, data[0]);
        Assert.Equal(0x10, data[1]);
        Assert.Equal(DeviceMac, data[2..8]);
        Assert.Equal(MasterMac, data[8..14]);
        Assert.Equal(3, data[14]);
        Assert.Equal(8, data[15]);
        Assert.Equal(2, data[16]);
        Assert.Equal([64, 64, 64, 0], data[17..21]);
        Assert.All(data[21..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void MasterClockRfData_CarriesMasterMacOnly()
    {
        var data = Slv3Protocol.BuildMasterClockRfData(MasterMac);

        Assert.Equal(0x12, data[0]);
        Assert.Equal(0x14, data[1]);
        Assert.Equal(MasterMac, data[8..14]);
        Assert.All(data[14..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void ChunkRfData_ProducesFourFramedUsbPackets()
    {
        var rfData = Enumerable.Range(0, Slv3Protocol.RfDataSize).Select(i => (byte)i).ToArray();

        var packets = Slv3Protocol.ChunkRfData(rfData, channel: 8, rxType: 3);

        Assert.Equal(4, packets.Length);
        for (byte i = 0; i < 4; i++)
        {
            Assert.Equal(64, packets[i].Length);
            Assert.Equal(0x10, packets[i][0]);
            Assert.Equal(i, packets[i][1]);
            Assert.Equal(8, packets[i][2]);
            Assert.Equal(3, packets[i][3]);
            Assert.Equal(rfData[(i * 60)..((i + 1) * 60)], packets[i][4..64]);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 255)]
    [InlineData(50, 128)]
    [InlineData(-5, 0)]
    [InlineData(150, 255)]
    public void DutyPercentToPwm_ScalesAndClamps(int duty, byte expected) =>
        Assert.Equal(expected, Slv3Protocol.DutyPercentToPwm(duty));

    [Fact]
    public void PwmConstraints_RaiseNonzeroBelowFloor_AndZeroUnusedSlots()
    {
        var result = Slv3Protocol.ApplyPwmConstraints([10, 10, 200, 200], fanCount: 3, isAio: false);

        var floor = (byte)(Slv3Protocol.MinimumDutyPercent / 100.0 * 255.0);
        Assert.Equal(floor, result[0]);
        Assert.Equal(floor, result[1]);
        Assert.Equal(200, result[2]);
        Assert.Equal(0, result[3]); // beyond fan count, not an AIO pump slot
    }

    [Fact]
    public void PwmConstraints_PreserveAioPumpSlot()
    {
        var result = Slv3Protocol.ApplyPwmConstraints([200, 200, 0, 180], fanCount: 2, isAio: true);

        Assert.Equal(0, result[2]);
        Assert.Equal(180, result[3]);
    }

    [Fact]
    public void MasterMacResponse_ParsesMacAndFirmware()
    {
        var response = new byte[64];
        response[0] = 0x11;
        MasterMac.CopyTo(response, 1);
        response[11] = 0x01; // fw 256+2 big-endian
        response[12] = 0x02;

        Assert.True(Slv3Protocol.TryParseMasterMac(response, 64, out var mac, out var firmware));
        Assert.Equal(MasterMac, mac);
        Assert.Equal(258, firmware);
    }

    [Fact]
    public void MasterMacResponse_RejectsZeroMac()
    {
        var response = new byte[64];
        response[0] = 0x11;

        Assert.False(Slv3Protocol.TryParseMasterMac(response, 64, out _, out _));
    }

    private static byte[] BuildDeviceRecord(byte deviceType, byte fanCount, ushort rpm0, byte marker = 0x1C)
    {
        var record = new byte[42];
        DeviceMac.CopyTo(record, 0);
        MasterMac.CopyTo(record, 6);
        record[12] = 8;         // channel
        record[13] = 3;         // rx type
        record[18] = deviceType;
        record[19] = fanCount;
        record[24] = 22;        // fan type byte in the SL V3 range
        record[28] = (byte)(rpm0 >> 8);
        record[29] = (byte)rpm0;
        record[36] = 128;       // current pwm
        record[41] = marker;
        return record;
    }

    [Fact]
    public void DeviceList_ParsesRecordsAndMotherboardPwm()
    {
        var response = new byte[4 + 42 * 2];
        response[0] = 0x10;
        response[1] = 2;
        response[2] = 0x40; // off_time 64, available
        response[3] = 0xC0; // on_time 192 -> pwm 191
        BuildDeviceRecord(0, 4, 1250).CopyTo(response, 4);
        BuildDeviceRecord(0xFF, 0, 0).CopyTo(response, 46); // master record — skipped

        var (devices, moboPwm) = Slv3Protocol.ParseDeviceList(response, response.Length);

        Assert.Single(devices);
        Assert.Equal(191, moboPwm);
        var device = devices[0];
        Assert.Equal(DeviceMac, device.Mac);
        Assert.True(device.IsBoundTo(MasterMac));
        Assert.Equal(4, device.FanCount);
        Assert.Equal(1250, device.FanRpms[0]);
        Assert.Equal(128, device.CurrentPwm[0]);
        Assert.True(device.IsSlv3Family);
    }

    [Fact]
    public void DeviceList_RejectsBadValidationMarker()
    {
        var response = new byte[4 + 42];
        response[0] = 0x10;
        response[1] = 1;
        response[2] = 0x80; // mobo pwm unavailable
        BuildDeviceRecord(0, 4, 1000, marker: 0x00).CopyTo(response, 4);

        var (devices, moboPwm) = Slv3Protocol.ParseDeviceList(response, response.Length);

        Assert.Empty(devices);
        Assert.Null(moboPwm);
    }
}
