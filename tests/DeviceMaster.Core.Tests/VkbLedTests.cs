using System.Text;
using DeviceMaster.Core.Devices;
using DeviceMaster.Devices.Vkb;

namespace DeviceMaster.Core.Tests;

public class VkbLedTests
{
    [Fact]
    public void TheChecksumIsCrc16Modbus()
    {
        // VKB's checksum = CRC-16/MODBUS (init 0xFFFF, reflected 0xA001) over (count+1)*3 bytes;
        // the standard check value of "123456789" is 0x4B37 — count 2 covers exactly those 9 bytes
        Assert.Equal(0x4B37, VkbLedProtocol.Checksum(2, Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void AConfigPacksItsFieldsLikeVkbDoes()
    {
        // clm=1 lem=1, color2 = (r1,g2,b3), color1 = (r4,g5,b6):
        // bits = 001 001 011 010 001 110 101 100 = 0x25A3AC → bytes reversed
        var c = new VkbLedConfig(10, VkbColorMode.Color2, VkbLedMode.Constant, new VkbColor(4, 5, 6), new VkbColor(1, 2, 3));
        Assert.Equal([10, 0xAC, 0xA3, 0x25], VkbLedProtocol.EncodeConfig(c));
        Assert.Equal(c, VkbLedProtocol.DecodeConfig(VkbLedProtocol.EncodeConfig(c)));
    }

    [Fact]
    public void TheSetReportIsLaidOutAndReadsBack()
    {
        var leds = new[]
        {
            new VkbLedConfig(0, VkbColorMode.Color1, VkbLedMode.Constant, new VkbColor(7, 0, 7), default),
            new VkbLedConfig(11, VkbColorMode.Color1, VkbLedMode.Off, default, default),
        };
        var report = VkbLedProtocol.BuildSet(leds, nonce: 0x3412);

        Assert.Equal(129, report.Length);
        Assert.Equal([0x59, 0xA5, 0x0A], report[..3]);
        Assert.Equal([0x12, 0x34, 2], report[5..8]);                         // nonce, count
        var chk = VkbLedProtocol.Checksum(2, report.AsSpan(5));
        Assert.Equal([(byte)chk, (byte)(chk >> 8)], report[3..5]);            // checksum, little-endian
        Assert.All(report[16..], b => Assert.Equal(0, b));                    // zero padding

        Assert.Equal(leds, VkbLedProtocol.ParseGet(report));                  // a GET returns the same layout
    }

    [Fact]
    public void ANeverSetDeviceReadsAsNoOverrides() =>
        Assert.Empty(VkbLedProtocol.ParseGet(new byte[129] { 0x59, 0, 0, 0, 0, 0, 0, 3, 1, 2, 3, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));

    [Theory]
    [InlineData(255, 0, 0, 7, 0, 0)]
    [InlineData(255, 70, 0, 7, 2, 0)]   // fleet orange
    [InlineData(150, 0, 255, 4, 0, 7)]  // fleet purple
    [InlineData(0, 0, 0, 0, 0, 0)]
    public void FleetColoursScaleToVkbLevels(byte r, byte g, byte b, byte vr, byte vg, byte vb) =>
        Assert.Equal(new VkbColor(vr, vg, vb), VkbColor.FromRgb(r, g, b));

    [Fact]
    public void AtMostTwelveLedsPerReport() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VkbLedProtocol.BuildSet(
            Enumerable.Range(0, 13).Select(i => new VkbLedConfig((byte)i, VkbColorMode.Color1, VkbLedMode.Off, default, default)).ToList(), 0));

    [Theory]
    [InlineData(0x0200)]
    [InlineData(0x0201)]
    public void BothSticksAreRegisteredForWrites(ushort pid) =>
        Assert.True(KnownDeviceRegistry.IsWriteAllowed(new UsbId(0x231D, pid)));
}
