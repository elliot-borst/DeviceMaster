using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Ports;
using System.Runtime.InteropServices;
using DeviceMaster.Core.Devices;
using DeviceMaster.Core.Discovery;

namespace DeviceMaster.Devices.Turzx;

/// <summary>
/// The Turzx / Turing 8.8" smart screen (1A86:CA88) over its USB serial (usbser CDC) port.
/// Opens the COM port, performs the hello handshake, and pushes full BGRA frames. The baud
/// rate is nominal — a CDC port transfers at USB speed regardless. Content is rendered in
/// landscape (1920×480) and rotated onto the panel's native 480×1920 portrait framebuffer,
/// exactly as turing-smart-screen-python does. Writes are gated on <see cref="KnownDeviceRegistry"/>.
/// </summary>
public sealed class TurzxScreen : IDisposable
{
    private const ushort ScreenVid = 0x1A86;
    private const ushort ScreenPid = 0xCA88;
    private const int BaudRate = 115200;

    private readonly SerialPort _port;
    private bool _initialized;

    private TurzxScreen(SerialPort port, string comPort, string? serial)
    {
        _port = port;
        ComPort = comPort;
        Serial = string.IsNullOrEmpty(serial) ? comPort : serial;
    }

    public string ComPort { get; }
    public string Serial { get; }
    public int? RomVersion { get; private set; }

    /// <summary>True mounts the ultrawide bar the other way up (reverse-landscape).</summary>
    public bool FlipLandscape { get; set; }

    /// <summary>Turzx screens currently present (COM port + serial hint), write-gated.</summary>
    public static IReadOnlyList<(string ComPort, string? Serial)> Find()
    {
        var found = new List<(string, string?)>();
        foreach (var port in DeviceScanner.ScanSerialPorts())
        {
            if (port.Identification?.Kind == DeviceKind.TurzxScreen
                && port.UsbId is { } id && KnownDeviceRegistry.IsWriteAllowed(id))
            {
                found.Add((port.ComPort, port.SerialHint));
            }
        }

        return found;
    }

    public static TurzxScreen Open(string comPort, string? serial = null)
    {
        // safety gate: never open a port we are not positively allowed to write to
        if (!KnownDeviceRegistry.IsWriteAllowed(new UsbId(ScreenVid, ScreenPid)))
        {
            throw new InvalidOperationException("Turzx screen writes are not permitted by the device registry.");
        }

        // No hardware flow control: this panel is a usbser CDC port that never asserts CTS, so
        // RTS/CTS handshaking (the reference's rtscts=True) makes every write block until it
        // fails with "the semaphore timeout period has expired". Assert DTR+RTS instead, the
        // usual "terminal present" signalling for a CDC virtual COM port.
        var port = new SerialPort(comPort, BaudRate)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 1000,
            WriteTimeout = 15_000,
            ReadBufferSize = 1 << 16,
            WriteBufferSize = 1 << 20,
        };
        port.Open();
        return new TurzxScreen(port, comPort, serial);
    }

    /// <summary>Backlight brightness 0 (off) to 100.</summary>
    public void SetBrightness(int percent)
    {
        EnsureInitialized();
        Write(TurzxProtocol.BuildBrightness(percent));
    }

    /// <summary>
    /// Pushes one full frame. <paramref name="landscapeJpeg"/> is a 1920×480 landscape image;
    /// it is rotated onto the native portrait panel and sent as BGRA.
    /// </summary>
    public void SendJpegFrame(byte[] landscapeJpeg)
    {
        EnsureInitialized();
        var bgra = LandscapeJpegToPanelBgra(landscapeJpeg, FlipLandscape);
        var body = TurzxProtocol.EncodeFullFrameBody(bgra);

        Write(TurzxProtocol.BuildCommand(TurzxProtocol.PreUpdateBitmap));
        Write(TurzxProtocol.BuildStartDisplayBitmap());
        Write(TurzxProtocol.BuildDisplayBitmap8Inch());
        Write(TurzxProtocol.BuildSendPayload(body));
        Write(TurzxProtocol.BuildCommand(TurzxProtocol.QueryStatus));
        DrainInput();
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        Hello();
        Write(TurzxProtocol.BuildCommand(TurzxProtocol.StopVideo));
        Write(TurzxProtocol.BuildCommand(TurzxProtocol.StopMedia));
        _initialized = true;
    }

    /// <summary>Handshake: read the panel's ID ("chs_88inch.dev1_rom1.90") and its ROM version.</summary>
    private void Hello()
    {
        try { _port.DiscardInBuffer(); } catch { /* best effort */ }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Write(TurzxProtocol.BuildCommand(TurzxProtocol.Hello));
            var response = ReadPrintable(23);
            if (response.StartsWith("chs_", StringComparison.Ordinal))
            {
                var parts = response.Split('.');
                if (parts.Length >= 3 && int.TryParse(parts[2], out var rom) && rom is >= 80 and <= 100)
                {
                    RomVersion = rom;
                }

                return;
            }
        }

        // best effort: the full-frame path works without a valid ID; proceed regardless
    }

    private string ReadPrintable(int count)
    {
        var buffer = new byte[count];
        var read = 0;
        try
        {
            while (read < count)
            {
                var n = _port.Read(buffer, read, count - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }
        }
        catch (TimeoutException)
        {
            // whatever arrived is enough to parse the ID
        }

        var chars = new char[read];
        var kept = 0;
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] is >= 0x20 and < 0x7f)
            {
                chars[kept++] = (char)buffer[i];
            }
        }

        return new string(chars, 0, kept);
    }

    private void Write(byte[] data) => _port.Write(data, 0, data.Length);

    /// <summary>Drain the panel's acknowledgement bytes so they don't back up the read buffer.</summary>
    private void DrainInput()
    {
        try
        {
            var deadline = Environment.TickCount64 + 200;
            var scratch = new byte[1024];
            while (Environment.TickCount64 < deadline && _port.BytesToRead > 0)
            {
                _ = _port.Read(scratch, 0, Math.Min(scratch.Length, _port.BytesToRead));
            }
        }
        catch
        {
            // acknowledgements are advisory
        }
    }

    /// <summary>Decode a landscape JPEG, rotate it onto the native portrait panel, return BGRA bytes.</summary>
    private static byte[] LandscapeJpegToPanelBgra(byte[] jpeg, bool flip)
    {
        using var stream = new MemoryStream(jpeg);
        using var decoded = new Bitmap(stream);
        using var rotated = (Bitmap)decoded.Clone();

        // landscape → native portrait: 90° CW normally, 270° CW when mounted the other way
        rotated.RotateFlip(flip ? RotateFlipType.Rotate270FlipNone : RotateFlipType.Rotate90FlipNone);

        using var panel = new Bitmap(TurzxProtocol.ScreenWidth, TurzxProtocol.ScreenHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(panel))
        {
            graphics.DrawImage(rotated, new Rectangle(0, 0, panel.Width, panel.Height));
        }

        var rect = new Rectangle(0, 0, panel.Width, panel.Height);
        var bits = panel.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            // 32bppArgb in memory is little-endian ARGB == byte order B, G, R, A — exactly what the panel wants
            var bytesPerRow = panel.Width * 4;
            var result = new byte[bytesPerRow * panel.Height];
            for (var y = 0; y < panel.Height; y++)
            {
                Marshal.Copy(bits.Scan0 + (y * bits.Stride), result, y * bytesPerRow, bytesPerRow);
            }

            return result;
        }
        finally
        {
            panel.UnlockBits(bits);
        }
    }

    public void Dispose() => _port.Dispose();
}
