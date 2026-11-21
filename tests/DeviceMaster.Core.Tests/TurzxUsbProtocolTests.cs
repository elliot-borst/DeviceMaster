using DeviceMaster.Devices.Turzx;

namespace DeviceMaster.Core.Tests;

/// <summary>
/// Turzx 8.8" V1.x USB protocol (#102). The expected block bytes were produced independently with
/// pycryptodome (DES-CBC, key = IV = "slv3tuzx", PKCS7), building the header the way the reference
/// implementations do.
/// </summary>
public class TurzxUsbProtocolTests
{
    [Fact]
    public void DesMatchesTheFipsTestVector() =>
        Assert.Equal(0x85E813540F0AB405UL, DesCbc.EncryptBlock(0x0123456789ABCDEFUL, 0x133457799BBCDFF1UL));

    [Fact]
    public void CbcPadsPkcs7ToTheNextBlock()
    {
        var key = "slv3tuzx"u8.ToArray();
        Assert.Equal(504, DesCbc.Encrypt(new byte[500], key, key).Length);
        Assert.Equal(16, DesCbc.Encrypt(new byte[8], key, key).Length); // a full pad block when already aligned
    }

    [Fact]
    public void SyncBlockMatchesTheReference()
    {
        var block = TurzxUsbProtocol.Sync(12345678);
        Assert.Equal(512, block.Length);
        Assert.Equal("019a3db35a5ba2ad1c6c82e3dc9bdc9a0aae1ba2eacd2bd2", Convert.ToHexString(block, 0, 24).ToLowerInvariant());
        Assert.Equal("a388f08836f40c69000000000000a11a", Convert.ToHexString(block, 496, 16).ToLowerInvariant());
    }

    [Fact]
    public void BrightnessAndJpegBlocksMatchTheReference()
    {
        Assert.Equal("a9badf11799bddd4720dea17d1af7ef5",
            Convert.ToHexString(TurzxUsbProtocol.Brightness(75, 1000), 0, 16).ToLowerInvariant());

        var jpeg = new byte[123456];
        var frame = TurzxUsbProtocol.JpegFrame(jpeg, 42);
        Assert.Equal(512 + 123456, frame.Length);
        Assert.Equal("6edf63cc8e6c3fe43f32428b39cce22c", Convert.ToHexString(frame, 0, 16).ToLowerInvariant());
    }

    [Fact]
    public void HeaderLayout()
    {
        var header = TurzxUsbProtocol.Header(TurzxUsbProtocol.CmdBrightness, 0x01020304, [55]);
        Assert.Equal(500, header.Length);
        Assert.Equal([14, 0, 0x1A, 0x6D, 0x04, 0x03, 0x02, 0x01, 55], header[..9]);
    }

    [Fact]
    public void BrightnessIsClampedTo0To100()
    {
        Assert.Equal(TurzxUsbProtocol.Brightness(100, 7), TurzxUsbProtocol.Brightness(250, 7));
        Assert.Equal(TurzxUsbProtocol.Brightness(0, 7), TurzxUsbProtocol.Brightness(-5, 7));
    }

    [Fact]
    public void FramesAreLimitedToWhatThePanelTakes()
    {
        Assert.Throws<ArgumentException>(() => TurzxUsbProtocol.JpegFrame([], 0));
        Assert.Throws<ArgumentException>(() => TurzxUsbProtocol.JpegFrame(new byte[TurzxUsbProtocol.MaxImageBytes + 1], 0));
    }

    [Fact]
    public void RepliesAndFirmware()
    {
        var reply = new byte[512];
        reply[0] = TurzxUsbProtocol.CmdSync;
        reply[1] = TurzxUsbProtocol.ReplyOk;
        "turzx_0001_0015"u8.CopyTo(reply.AsSpan(8));

        Assert.True(TurzxUsbProtocol.IsOk(reply, TurzxUsbProtocol.CmdSync));
        Assert.False(TurzxUsbProtocol.IsOk(reply, TurzxUsbProtocol.CmdJpeg));
        Assert.Equal("turzx_0001_0015", TurzxUsbProtocol.FirmwareVersion(reply));
    }

    [Fact]
    public void TimestampIsMillisecondsSinceLocalMidnight() =>
        Assert.Equal(3_723_004u, TurzxUsbProtocol.TimestampMs(new DateTime(2026, 11, 20, 1, 2, 3, 4)));
}

/// <summary>Finding a V1.x panel through sysfs (#102): VID/PID and the product string must all match.</summary>
public sealed class UsbfsTurzxFindTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dm-usbfs-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Device(string name, string vid, string pid, string? product, int bus, int dev)
    {
        var dir = Path.Combine(_root, "sys", "bus", "usb", "devices", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "idVendor"), vid + "\n");
        File.WriteAllText(Path.Combine(dir, "idProduct"), pid + "\n");
        if (product is not null)
        {
            File.WriteAllText(Path.Combine(dir, "product"), product + "\n");
        }

        File.WriteAllText(Path.Combine(dir, "busnum"), $"{bus}\n");
        File.WriteAllText(Path.Combine(dir, "devnum"), $"{dev}\n");
    }

    [Fact]
    public void FindsThePanelByIdAndProductString()
    {
        Device("3-3", "1d6b", "0104", "USB Emulation Device", 3, 7);
        Device("3-10", "1cbe", "0088", "TURZX1.0", 3, 8);

        Assert.Equal(Path.Combine(_root, "dev", "bus", "usb", "003", "008"),
            DeviceMaster.Platform.Linux.UsbfsTurzxTransport.Find(Path.Combine(_root, "sys"), Path.Combine(_root, "dev")));
    }

    [Fact]
    public void IgnoresASameIdDeviceWithAnotherName()
    {
        Device("1-4", "1cbe", "0088", "SLV3 FAN", 1, 4); // VID 1CBE is shared: never write to it on the IDs alone
        Device("1-5", "1cbe", "0088", null, 1, 5);

        Assert.Null(DeviceMaster.Platform.Linux.UsbfsTurzxTransport.Find(Path.Combine(_root, "sys"), Path.Combine(_root, "dev")));
    }
}

/// <summary>The V1.x panel's frames (#102): JPEG output and the Windows gaming layout.</summary>
public class TurzxUsbFrameTests
{
    [Fact]
    public void DashboardJpegIsAPortraitJpegWellUnderThePanelLimit()
    {
        var data = new DeviceMaster.Lcd.Skia.TurzxDashboardData { ShowFps = true, Fps = 144, FpsHistory = [120, 140, 144] };
        var jpeg = DeviceMaster.Lcd.Skia.TurzxDashboard.RenderNativeJpeg(data, flip: false);
        Assert.Equal([0xFF, 0xD8], jpeg[..2]);
        Assert.InRange(jpeg.Length, 1, TurzxUsbProtocol.MaxImageBytes);
        using var decoded = SkiaSharp.SKBitmap.Decode(jpeg);
        Assert.Equal((TurzxUsbProtocol.ScreenWidth, TurzxUsbProtocol.ScreenHeight), (decoded.Width, decoded.Height));
    }

    [Fact]
    public void WindowsFeedUsesTheGamingLayoutAndTheCoolerFan()
    {
        var status = new DeviceMaster.Control.ControlStatus
        {
            CpuName = "CPU X", GpuName = "GPU Y", CpuTemperatureC = 55, TargetDutyPercent = 40,
            Devices = [new("corsair-link", "Fan", 900, 40), new("motherboard", "CPU Fan", 1200, 40)],
        };
        var data = DeviceMaster.Control.TurzxDashboardFeed.From(status, 97, [90, 97]);
        Assert.True(data.ShowFps);
        Assert.False(data.HasLlm);
        Assert.Equal(97, data.Fps);
        Assert.Equal(1200, data.FanRpm); // the motherboard header (the CPU cooler) wins
        Assert.Equal("CPU X", data.CpuName);
    }
}

/// <summary>The server layout's text helpers (#104).</summary>
public class TurzxServerLayoutTests
{
    [Theory]
    [InlineData(29_624_558_645_248L, null, "29.6 TB")]
    [InlineData(4_000_795_770_880L, null, "4.00 TB")]
    [InlineData(702_980_059_136L, 4_000_795_770_880L, "0.70")]
    [InlineData(702_980_059_136L, null, "703 GB")]
    public void SizesReadLikeUnraid(long bytes, long? scaleLike, string expected) =>
        Assert.Equal(expected, DeviceMaster.Lcd.Skia.TurzxDashboard.Bytes(bytes, scaleLike));

    [Theory]
    [InlineData(176_700, "2 d 1 h")]
    [InlineData(18_720, "5 h 12 m")]
    [InlineData(840, "14 m")]
    public void UptimeIsShort(double seconds, string expected) =>
        Assert.Equal(expected, DeviceMaster.Lcd.Skia.TurzxDashboard.Uptime(seconds));

    [Fact]
    public void ServerLayoutRenders()
    {
        var data = new DeviceMaster.Lcd.Skia.TurzxDashboardData
        {
            ShowServer = true,
            Storage = [new("Array", 600, 1000)],
            ContainersRunning = 5, ContainersTotal = 6, StoppedContainers = ["a"],
        };
        var jpeg = DeviceMaster.Lcd.Skia.TurzxDashboard.RenderNativeJpeg(data, flip: false);
        Assert.Equal([0xFF, 0xD8], jpeg[..2]);
    }
}

/// <summary>Installed RAM from the SMBIOS table (#106).</summary>
public class SmbiosMemoryTests
{
    private static byte[] MemoryDevice(ushort size, uint extendedMb = 0)
    {
        var s = new byte[0x28 + 2];
        s[0] = 17;
        s[1] = 0x28;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x0C), size);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0x1C), extendedMb);
        return s; // no strings: the formatted part is followed by the double NUL
    }

    [Fact]
    public void SumsPopulatedSlotsInEveryEncoding()
    {
        var bios = new byte[] { 0, 4, 0, 0, (byte)'X', 0, 0 }; // another structure with a string
        var end = new byte[] { 127, 4, 0, 0, 0, 0 };
        var table = bios
            .Concat(MemoryDevice(16384))                 // 16 GB in MB
            .Concat(MemoryDevice(0))                     // empty slot
            .Concat(MemoryDevice(0x7FFF, 32768))         // 32 GB via the extended size
            .Concat(MemoryDevice(0x8000 | 1024))         // 1 MB in KB units
            .Concat(MemoryDevice(0xFFFF))                // unknown
            .Concat(end).ToArray();

        Assert.Equal((16384L + 32768) * 1024 * 1024 + 1024L * 1024, DeviceMaster.Sensors.Linux.SmbiosMemory.InstalledBytes(table));
    }

    [Fact]
    public void NoMemoryDevicesMeansUnknown() =>
        Assert.Null(DeviceMaster.Sensors.Linux.SmbiosMemory.InstalledBytes(new byte[] { 127, 4, 0, 0, 0, 0 }));
}
