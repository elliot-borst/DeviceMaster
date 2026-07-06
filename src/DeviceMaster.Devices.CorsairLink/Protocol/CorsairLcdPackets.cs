using System.Buffers.Binary;

namespace DeviceMaster.Devices.CorsairLink.Protocol;

/// <summary>
/// Pure packet builders for the Corsair pump/res LCD (1B1C:0C43), ported from OpenLinkHub's
/// transferToLcd (MIT). Image data (JPEG) is chunked into 1024-byte HID output reports:
/// [0]=0x02 report id, [1]=0x05, [2]=0x01, [3]=0x01 on the final chunk, [4]=chunk index,
/// [6..8]=chunk length LE, payload from offset 8. No I/O here — fully unit-testable.
/// </summary>
public static class CorsairLcdPackets
{
    public const int ReportSize = 1024;
    public const int HeaderSize = 8;
    public const int MaxChunk = ReportSize - HeaderSize;

    /// <summary>The XD5 Elite LCD panel is 480×480.</summary>
    public const int ScreenWidth = 480;
    public const int ScreenHeight = 480;

    /// <summary>Feature report setting backlight brightness 0 (off) to 100.</summary>
    public static byte[] CreateBrightnessReport(int percent) =>
        [0x03, 0x0B, (byte)Math.Clamp(percent, 0, 100), 0x01];

    /// <summary>Splits a JPEG into ready-to-write 1024-byte output reports.</summary>
    public static List<byte[]> CreateImageReports(ReadOnlySpan<byte> jpeg)
    {
        var reports = new List<byte[]>();
        var offset = 0;
        var index = 0;
        while (offset < jpeg.Length || index == 0)
        {
            var chunk = Math.Min(MaxChunk, jpeg.Length - offset);
            var report = new byte[ReportSize];
            report[0] = 0x02;
            report[1] = 0x05;
            report[2] = 0x01;
            report[3] = (byte)(offset + chunk >= jpeg.Length ? 0x01 : 0x00); // final-chunk marker
            report[4] = (byte)index;
            BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(6, 2), (ushort)chunk);
            jpeg.Slice(offset, chunk).CopyTo(report.AsSpan(HeaderSize));
            reports.Add(report);
            offset += chunk;
            index++;
        }

        return reports;
    }
}
