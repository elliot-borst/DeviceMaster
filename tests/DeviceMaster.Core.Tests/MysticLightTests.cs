using DeviceMaster.Core.Devices;
using DeviceMaster.Devices.MsiMysticLight;

namespace DeviceMaster.Core.Tests;

public class MysticLightTests
{
    [Fact]
    public void SetupReportMatchesWhatOpenRgbSends()
    {
        var p = MysticLight761Protocol.BuildSetup();

        Assert.Equal(290, p.Length);
        Assert.Equal(0x50, p[0]);
        byte[] argbRow = [0x09, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x03, 0x15, 0x78];
        for (var row = 0; row < 4; row++)
        {
            Assert.Equal(argbRow, p.AsSpan(1 + row * 16, 16).ToArray()); // JARGB 1-3 + JAF in direct mode
        }

        for (var row = 4; row < 17; row++)
        {
            var bytes = p.AsSpan(1 + row * 16, 16).ToArray();
            Assert.All(bytes[..15], b => Assert.Equal(0, b)); // JPIPE / JRGB / onboard cleared
            Assert.Equal(0x1E, bytes[15]);
        }

        Assert.Equal([0x09, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x03, 0x95, 0x1E],
            p.AsSpan(1 + 17 * 16, 16).ToArray());
        Assert.Equal(0, p[289]);
    }

    [Theory]
    [InlineData(MysticZone.Jargb1, 0x04, 0x00)]
    [InlineData(MysticZone.Jargb2, 0x04, 0x01)]
    [InlineData(MysticZone.Jargb3, 0x04, 0x02)]
    [InlineData(MysticZone.Jaf, 0x08, 0x00)]
    public void StaticColourFillsEveryLedOfTheHeader(MysticZone zone, byte hdr0, byte hdr1)
    {
        var p = MysticLight761Protocol.BuildStatic(zone, 255, 0, 110);

        Assert.Equal(727, p.Length);
        Assert.Equal([0x51, 0x09, hdr0, hdr1, 0x00, 0x00, 240], p[..7]);
        for (var led = 0; led < 240; led++)
        {
            Assert.Equal([255, 0, 110], p.AsSpan(7 + led * 3, 3).ToArray());
        }
    }

    [Fact]
    public void AShortPatternRepeatsAlongTheStrip()
    {
        var p = MysticLight761Protocol.BuildColors(MysticZone.Jargb1, [1, 2, 3, 4, 5, 6]);
        Assert.Equal([1, 2, 3, 4, 5, 6, 1, 2, 3], p[7..16]);
        Assert.Throws<ArgumentException>(() => MysticLight761Protocol.BuildColors(MysticZone.Jaf, [1, 2]));
    }

    [Theory]
    [InlineData("MAG X870E TOMAHAWK WIFI (MS-7E59)", true)]
    [InlineData("MSI MAG X870E TOMAHAWK WIFI (MS-7E59)", true)]
    [InlineData("  mag x870e tomahawk wifi (ms-7e59)\n", true)]
    [InlineData("MAG B650 TOMAHAWK WIFI (MS-7D75)", false)] // older board: other protocol, never driven
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTestedBoardsAreDriven(string? board, bool expected) =>
        Assert.Equal(expected, MysticLight761Protocol.IsSupportedBoard(board));

    [Fact]
    public void TheReportDescriptorDecidesTheProtocol()
    {
        Assert.True(MysticLight761Protocol.LooksLike761([(0x50, 290), (0x51, 727)]));
        Assert.True(MysticLight761Protocol.LooksLike761([(0x50, 290), (0x51, 761), (0x52, 64)]));
        Assert.False(MysticLight761Protocol.LooksLike761([(0x50, 290), (0x51, 727), (0x52, 185)])); // a 185-byte board
        Assert.False(MysticLight761Protocol.LooksLike761([(0x52, 162)]));
        Assert.False(MysticLight761Protocol.LooksLike761([(0x51, 64)]));
    }

    [Fact]
    public void TheControllerIsRegisteredForWrites()
    {
        var id = new UsbId(0x0DB0, 0x0076);
        Assert.Equal(DeviceKind.MotherboardRgbController, KnownDeviceRegistry.Identify(id)!.Kind);
        Assert.True(KnownDeviceRegistry.IsWriteAllowed(id));
    }
}
