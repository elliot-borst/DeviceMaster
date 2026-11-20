namespace DeviceMaster.Devices.Turzx;

/// <summary>Raw bulk I/O to a Turzx V1.x panel (Linux usbfs, Windows WinUSB).</summary>
public interface ITurzxUsbTransport : IDisposable
{
    /// <summary>Where the panel is (for logs): a usbfs node or a WinUSB device path.</summary>
    string Name { get; }

    /// <summary>One bulk write to EP 0x01; throws on error or timeout.</summary>
    void Write(ReadOnlySpan<byte> data, int timeoutMs);

    /// <summary>One bulk read from EP 0x81; returns the byte count, 0 on timeout.</summary>
    int Read(Span<byte> buffer, int timeoutMs);
}

/// <summary>
/// A session with a Turzx 8.8" V1.x USB panel (#102): handshake, brightness and full-screen JPEG
/// frames. Every command is answered with one 512-byte reply, which is always read before the next
/// command is sent (the panel hangs when it is flooded). Never resets the device: a USB reset can
/// stall the whole host controller (ninharp/turzx-linux). After an error the caller disposes the
/// session and opens a new one later.
/// </summary>
public sealed class TurzxUsbScreen : IDisposable
{
    private const int CommandTimeoutMs = 2_000;

    private readonly ITurzxUsbTransport _transport;
    private readonly Func<DateTime> _now;
    private readonly byte[] _reply = new byte[1024]; // 512 + room for the zero-length packet that ends it

    private TurzxUsbScreen(ITurzxUsbTransport transport, Func<DateTime> now)
    {
        _transport = transport;
        _now = now;
    }

    public string Name => _transport.Name;

    /// <summary>Firmware version from the handshake, e.g. "turzx_0001_0015".</summary>
    public string? Firmware { get; private set; }

    /// <summary>Mounted the other way up: landscape content is turned 270° instead of 90°.</summary>
    public bool FlipLandscape { get; set; }

    /// <summary>
    /// Opens a session: drains anything left in the reply pipe, handshakes, and stops any video or
    /// slideshow the panel is playing from its own storage. Throws when the panel doesn't answer.
    /// The session owns <paramref name="transport"/> from here on.
    /// </summary>
    public static TurzxUsbScreen Open(ITurzxUsbTransport transport, Func<DateTime>? now = null)
    {
        var screen = new TurzxUsbScreen(transport, now ?? (() => DateTime.Now));
        try
        {
            screen.Drain();
            var reply = screen.Command(TurzxUsbProtocol.Sync(screen.Timestamp), TurzxUsbProtocol.CmdSync);
            screen.Firmware = TurzxUsbProtocol.FirmwareVersion(reply);
            screen.Command(TurzxUsbProtocol.StopVideo(screen.Timestamp), TurzxUsbProtocol.CmdStopVideo);
            return screen;
        }
        catch
        {
            screen.Dispose();
            throw;
        }
    }

    /// <summary>Backlight 0–100 % (0 = dark; the panel has no separate off command).</summary>
    public void SetBrightness(int percent) =>
        Command(TurzxUsbProtocol.Brightness(percent, Timestamp), TurzxUsbProtocol.CmdBrightness);

    /// <summary>Shows a full-screen JPEG of the native 480×1920 portrait frame.</summary>
    public void SendJpeg(byte[] portraitJpeg) =>
        Command(TurzxUsbProtocol.JpegFrame(portraitJpeg, Timestamp), TurzxUsbProtocol.CmdJpeg);

    private uint Timestamp => TurzxUsbProtocol.TimestampMs(_now());

    /// <summary>Sends one command and returns its reply; throws when the panel doesn't echo it.</summary>
    private ReadOnlySpan<byte> Command(byte[] write, byte command)
    {
        // a big frame needs longer on the wire than a bare command
        _transport.Write(write, CommandTimeoutMs + write.Length / 20_000);
        var read = _transport.Read(_reply, CommandTimeoutMs);
        if (read == 0)
        {
            read = _transport.Read(_reply, CommandTimeoutMs); // the zero-length packet came first
        }

        if (read < 2 || _reply[0] != command)
        {
            throw new IOException(read == 0
                ? $"Turzx panel did not answer command {command}"
                : $"Turzx panel answered command {command} with {_reply[0]} ({read} bytes)");
        }

        return _reply.AsSpan(0, read);
    }

    /// <summary>Throws away replies a previous session left unread.</summary>
    private void Drain()
    {
        for (var i = 0; i < 8 && _transport.Read(_reply, 20) > 0; i++)
        {
        }
    }

    /// <summary>Releases the device; the panel keeps showing the last frame.</summary>
    public void Dispose() => _transport.Dispose();
}
