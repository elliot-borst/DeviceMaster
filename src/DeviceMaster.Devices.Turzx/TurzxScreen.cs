using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Threading;
using DeviceMaster.Core.Devices;
using DeviceMaster.Core.Discovery;

namespace DeviceMaster.Devices.Turzx;

/// <summary>
/// The Turzx / Turing 8.8" smart screen over its USB serial port.
///
/// The panel exposes TWO serial ports and this was the whole reason our handshake used to fail:
/// the <c>1A86:CA88</c> "CT88INCH" port is only a standby/control endpoint — it never answers
/// the protocol and stalls on a frame write — while a second Linux gadget-serial port (the SoC
/// re-enumerated as <c>g_serial</c>) is the DATA endpoint that actually speaks the Turing rev_c
/// protocol (it replies to HELLO with e.g. "chs_88inch.dev1_rom1.90"). We drive the DATA port.
///
/// Content is rendered in landscape (1920×480) and rotated onto the panel's native 480×1920
/// portrait framebuffer as full BGRA frames, exactly as turing-smart-screen-python's REV_8INCH
/// path does. Writes are gated on <see cref="KnownDeviceRegistry"/> (see <see cref="Find"/>).
/// </summary>
public sealed class TurzxScreen : IDisposable
{
    private const ushort ScreenVid = 0x1A86;
    private const ushort ScreenPid = 0xCA88;
    private const int BaudRate = 115200;

    /// <summary>
    /// Identities of the panel's writable DATA port. These are stock Linux <c>g_serial</c> gadget
    /// IDs (and the panel's "20080411" serial), so they must NOT be treated as write-allowed on
    /// their own — we only ever open the data port when the panel's own <c>1A86:CA88</c> control
    /// port is also present, which is the positive Turzx identification that authorises the write.
    /// </summary>
    private static readonly UsbId[] DataPortIds =
    [
        new(0x0525, 0xA4A7), // NetChip / Linux g_serial "Gadget Serial" (CDC-ACM)
        new(0x1D6B, 0x0121),
        new(0x1D6B, 0x0106),
    ];
    private const string DataPortSerial = "20080411";

    /// <summary>
    /// A full frame is written in paced ~24.9 KB chunks, never as one multi-MB blast: a single
    /// large write stalls the CDC endpoint ("the semaphore timeout period has expired"). This
    /// matches the vendor app exactly (it sleeps 1 ms every 24900 bytes).
    /// </summary>
    private const int FramePayloadChunkBytes = 24_900;

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

    /// <summary>
    /// The Turzx panel's writable DATA port (COM + serial hint), or empty if no panel is present.
    /// A panel is present iff its <c>1A86:CA88</c> control port is enumerated and write-allowed;
    /// the port returned is the panel's gadget-serial DATA port — the one that speaks the protocol.
    /// If only the control port is present, the panel is nudged awake (open/close) and we wait for
    /// the data port to enumerate.
    /// </summary>
    public static IReadOnlyList<(string ComPort, string? Serial)> Find()
    {
        var ports = DeviceScanner.ScanSerialPorts();
        var control = ports.FirstOrDefault(p =>
            p.Identification?.Kind == DeviceKind.TurzxScreen
            && p.UsbId is { } id && KnownDeviceRegistry.IsWriteAllowed(id));
        if (control is null)
        {
            return [];
        }

        var data = FindDataPort(ports) ?? WakeAndFindDataPort(control.ComPort);
        return data is null ? [] : [(data.ComPort, data.SerialHint)];
    }

    private static SerialPortInfo? FindDataPort(IReadOnlyList<SerialPortInfo> ports) =>
        ports.FirstOrDefault(p =>
            (p.UsbId is { } id && DataPortIds.Contains(id))
            || string.Equals(p.SerialHint, DataPortSerial, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Nudge a standby-only panel awake: opening then closing the control port makes the SoC
    /// re-enumerate its gadget-serial data port. Polls briefly for that port to appear. On this
    /// dev rig both ports are always present, so this path is a robustness fallback for panels
    /// that sleep the data interface until the control port is touched.
    /// </summary>
    private static SerialPortInfo? WakeAndFindDataPort(string controlCom)
    {
        try
        {
            using var wake = new SerialPort(controlCom, BaudRate)
            {
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = 500,
                WriteTimeout = 500,
            };
            wake.Open();
            Thread.Sleep(120);
        }
        catch
        {
            // the open/close edge is the wake signal; a failure to open is non-fatal here
        }

        var deadline = Environment.TickCount64 + 8_000;
        while (Environment.TickCount64 < deadline)
        {
            if (FindDataPort(DeviceScanner.ScanSerialPorts()) is { } data)
            {
                return data;
            }

            Thread.Sleep(500);
        }

        return null;
    }

    /// <summary>
    /// Opens the panel's DATA port (get <paramref name="comPort"/> from <see cref="Find"/>).
    /// Writes are permitted only when the Turzx panel is positively identified in the registry
    /// (its <c>1A86:CA88</c> control port) — <see cref="Find"/> is what pairs that identification
    /// with the gadget-serial data port this opens.
    /// </summary>
    public static TurzxScreen Open(string comPort, string? serial = null)
    {
        // safety gate: writes to the Turzx panel are only permitted if its control-port identity
        // is registry-allowed. Find() guarantees comPort is that panel's data port.
        if (!KnownDeviceRegistry.IsWriteAllowed(new UsbId(ScreenVid, ScreenPid)))
        {
            throw new InvalidOperationException("Turzx screen writes are not permitted by the device registry.");
        }

        // No hardware flow control, and DO NOT enlarge the driver buffers: this is a CDC port and
        // a big WriteBufferSize makes every write fail with "the semaphore timeout period has
        // expired" (proven with `turzx probe`). Assert DTR+RTS, the usual "terminal present"
        // signalling for a CDC virtual COM port. Frames are paced in chunks (see WriteFramePayload),
        // so a modest per-write timeout is right — matches the vendor's 5 s.
        var port = new SerialPort(comPort, BaudRate)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 1000,
            WriteTimeout = 5000,
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
        WriteFramePayload(TurzxProtocol.BuildSendPayload(body));
        Write(TurzxProtocol.BuildCommand(TurzxProtocol.QueryStatus));
        DrainInput();
    }

    /// <summary>
    /// Writes a full-frame payload (~3.7 MB) in paced <see cref="FramePayloadChunkBytes"/> chunks.
    /// A single write of the whole frame stalls the CDC endpoint; the vendor paces identically
    /// (1 ms every 24900 bytes), so we do too.
    /// </summary>
    private void WriteFramePayload(byte[] data)
    {
        for (var offset = 0; offset < data.Length; offset += FramePayloadChunkBytes)
        {
            var count = Math.Min(FramePayloadChunkBytes, data.Length - offset);
            _port.Write(data, offset, count);
            if (offset + count < data.Length)
            {
                Thread.Sleep(1);
            }
        }
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

    /// <summary>
    /// Handshake: read the panel's ID ("chs_88inch.dev1_rom1.90") and its ROM version. Throws
    /// <see cref="TurzxUnsupportedException"/> if the panel never answers — this Turing protocol
    /// is NOT what it speaks (e.g. a newer 8.8" revision). Gating on this reply is what stops us
    /// blasting a ~3.7 MB frame at a panel that won't drain it (which stalls the port for 30 s
    /// and can knock it off the USB bus).
    /// </summary>
    private void Hello()
    {
        try { _port.DiscardInBuffer(); } catch { /* best effort */ }

        for (var attempt = 0; attempt < 4; attempt++)
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

        throw new TurzxUnsupportedException(
            "The panel did not answer the Turing HELLO handshake — it is likely a newer 8.8\" revision "
            + "that uses a different serial protocol than the one implemented here.");
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

/// <summary>
/// Thrown when a panel opens on the serial port but does not speak the implemented Turing
/// protocol (no HELLO reply). Signals the control loop to stop trying rather than repeatedly
/// stalling the port with frame writes it will never accept.
/// </summary>
public sealed class TurzxUnsupportedException(string message) : Exception(message);
