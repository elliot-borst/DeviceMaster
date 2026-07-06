using System.Security.Cryptography;
using DeviceMaster.Devices.CorsairLink.Protocol;
using DeviceMaster.Devices.LianLi;

namespace DeviceMaster.Core.Tests;

public class CorsairLcdPacketsTests
{
    [Fact]
    public void CreateImageReports_ChunksWithHeaderAndFinalMarker()
    {
        var jpeg = Enumerable.Range(0, CorsairLcdPackets.MaxChunk + 100).Select(i => (byte)i).ToArray();

        var reports = CorsairLcdPackets.CreateImageReports(jpeg);

        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.Equal(CorsairLcdPackets.ReportSize, r.Length));
        Assert.Equal(0x02, reports[0][0]);
        Assert.Equal(0x05, reports[0][1]);
        Assert.Equal(0x01, reports[0][2]);
        Assert.Equal(0x00, reports[0][3]); // not final
        Assert.Equal(0x01, reports[1][3]); // final marker
        Assert.Equal(0, reports[0][4]);
        Assert.Equal(1, reports[1][4]);
        Assert.Equal(CorsairLcdPackets.MaxChunk, reports[0][6] | (reports[0][7] << 8));
        Assert.Equal(100, reports[1][6] | (reports[1][7] << 8));
        Assert.Equal(jpeg[0], reports[0][CorsairLcdPackets.HeaderSize]);
        Assert.Equal(jpeg[CorsairLcdPackets.MaxChunk], reports[1][CorsairLcdPackets.HeaderSize]);
    }

    [Fact]
    public void CreateImageReports_ExactMultipleStillEndsWithFinalMarker()
    {
        var jpeg = new byte[CorsairLcdPackets.MaxChunk * 2];

        var reports = CorsairLcdPackets.CreateImageReports(jpeg);

        Assert.Equal(2, reports.Count);
        Assert.Equal(0x01, reports[^1][3]);
    }

    [Fact]
    public void CreateBrightnessReport_ClampsToPercent()
    {
        Assert.Equal(new byte[] { 0x03, 0x0B, 100, 0x01 }, CorsairLcdPackets.CreateBrightnessReport(250));
        Assert.Equal(new byte[] { 0x03, 0x0B, 0, 0x01 }, CorsairLcdPackets.CreateBrightnessReport(-5));
    }
}

public class Slv3LcdProtocolTests
{
    private static byte[] Decrypt(byte[] header)
    {
        using var des = DES.Create();
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.PKCS7;
        des.Key = "slv3tuzx"u8.ToArray();
        des.IV = "slv3tuzx"u8.ToArray();
        using var decryptor = des.CreateDecryptor();
        return decryptor.TransformFinalBlock(header, 0, header.Length);
    }

    [Fact]
    public void BuildHeader_Is512BytesAndRoundTrips()
    {
        var header = Slv3LcdProtocol.BuildJpegHeader(0x01020304, timestampMs: 0xAABBCCDD);

        Assert.Equal(Slv3LcdProtocol.HeaderSize, header.Length);

        var plain = Decrypt(header);
        Assert.Equal(504, plain.Length);
        Assert.Equal(Slv3LcdProtocol.CmdPushJpeg, plain[0]);
        Assert.Equal(0x1A, plain[2]);
        Assert.Equal(0x6D, plain[3]);
        Assert.Equal(0xDD, plain[4]); // timestamp little-endian
        Assert.Equal(0xAA, plain[7]);
        Assert.Equal(0x01, plain[8]); // payload size big-endian
        Assert.Equal(0x04, plain[11]);
    }

    [Fact]
    public void BuildBrightnessHeader_CarriesClampedValue()
    {
        var plain = Decrypt(Slv3LcdProtocol.BuildBrightnessHeader(250, 1));

        Assert.Equal(Slv3LcdProtocol.CmdBrightness, plain[0]);
        Assert.Equal(100, plain[8]);
    }

    [Fact]
    public void BuildFrameTransfer_PadsToFixedSize()
    {
        var header = Slv3LcdProtocol.BuildInitHeader(1);
        var jpeg = new byte[] { 1, 2, 3 };

        var transfer = Slv3LcdProtocol.BuildFrameTransfer(header, jpeg);

        Assert.Equal(Slv3LcdProtocol.FrameTransferSize, transfer.Length);
        Assert.Equal(header, transfer[..Slv3LcdProtocol.HeaderSize]);
        Assert.Equal(jpeg, transfer[Slv3LcdProtocol.HeaderSize..(Slv3LcdProtocol.HeaderSize + 3)]);
        Assert.Equal(0, transfer[^1]);
    }
}
