using System.Buffers.Binary;
using System.Text;

namespace DeviceMaster.Devices.Turzx;

/// <summary>
/// Pure packet builders for the Turzx / Turing 8.8" smart screen hardware revision V1.x (#102):
/// USB <c>1CBE:0088</c>, product string "TURZX1.0", one vendor-specific interface with bulk
/// endpoints 0x01 OUT / 0x81 IN. Ported from phstudy/turing-smart-screen-cli (MIT, transport.py /
/// operations.py), cross-checked against mathoudebine/turing-smart-screen-python
/// (lcd_comm_turing_usb.py) and ninharp/turzx-linux (docs/PROTOCOL.md).
/// <list type="bullet">
/// <item>Every command is one 512-byte block: a 500-byte header (cmd, 0, 0x1A, 0x6D, little-endian
///   ms timestamp, payload from byte 8) DES-CBC encrypted with key = IV = "slv3tuzx" and PKCS7
///   padding (504 bytes, as the vendor app's DESCryptoServiceProvider), then 6 zero bytes, then
///   0xA1 0x1A in clear.</item>
/// <item>Data commands (JPEG, PNG) put a big-endian uint32 length at header bytes 8..11 and append
///   the data, unencrypted, right after the block in the same bulk write.</item>
/// <item>The panel answers every command with one 512-byte reply: byte 0 echoes the command,
///   byte 1 is 200 when it succeeded.</item>
/// </list>
/// No I/O here: fully unit-testable.
/// </summary>
public static class TurzxUsbProtocol
{
    public const ushort Vid = 0x1CBE;
    public const ushort Pid = 0x0088;

    /// <summary>
    /// The product string that tells this panel apart: VID 1CBE (TI Luminary) is also used by other
    /// vendors' parts (Lian Li SL V3 fans), so the PID alone is not enough to write to a device.
    /// </summary>
    public const string ProductString = "TURZX1.0";

    public const byte EndpointOut = 0x01;
    public const byte EndpointIn = 0x81;

    /// <summary>Native panel resolution (portrait); landscape content is rotated onto it.</summary>
    public const int ScreenWidth = 480;
    public const int ScreenHeight = 1920;

    public const int BlockSize = 512;
    public const int HeaderSize = 500;

    /// <summary>Payload bytes that fit in a header after the 8-byte prefix.</summary>
    public const int MaxPayload = HeaderSize - 8;

    /// <summary>The panel rejects (or times out on) bigger images.</summary>
    public const int MaxImageBytes = 1_000_000;

    /// <summary>Reply byte 1 when a command succeeded.</summary>
    public const byte ReplyOk = 200;

    public const byte CmdSync = 10;
    public const byte CmdRotation = 13;
    public const byte CmdBrightness = 14;
    public const byte CmdJpeg = 101;
    public const byte CmdPng = 102;
    public const byte CmdStopVideo = 111;
    public const byte CmdStopMedia = 123;

    private static readonly byte[] Key = "slv3tuzx"u8.ToArray();

    /// <summary>The plaintext 500-byte header for a command (exposed for tests).</summary>
    public static byte[] Header(byte command, uint timestampMs, ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentException($"a command payload holds at most {MaxPayload} bytes", nameof(payload));
        }

        var header = new byte[HeaderSize];
        header[0] = command;
        header[2] = 0x1A;
        header[3] = 0x6D;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), timestampMs);
        payload.CopyTo(header.AsSpan(8));
        return header;
    }

    /// <summary>The 512-byte block for a command: encrypted header, zeros, then 0xA1 0x1A.</summary>
    public static byte[] Block(byte command, uint timestampMs, ReadOnlySpan<byte> payload = default)
    {
        var encrypted = DesCbc.Encrypt(Header(command, timestampMs, payload), Key, Key); // 504 bytes
        var block = new byte[BlockSize];
        encrypted.CopyTo(block, 0);
        block[510] = 0xA1;
        block[511] = 0x1A;
        return block;
    }

    /// <summary>SYNC: the handshake; the reply carries the firmware version string.</summary>
    public static byte[] Sync(uint timestampMs) => Block(CmdSync, timestampMs);

    /// <summary>Backlight brightness 0–100 (the vendor app maps its 0–255 slider onto 0–100).</summary>
    public static byte[] Brightness(int percent, uint timestampMs) =>
        Block(CmdBrightness, timestampMs, [(byte)Math.Clamp(percent, 0, 100)]);

    /// <summary>Stops a video or slideshow the panel may be playing from its own storage.</summary>
    public static byte[] StopVideo(uint timestampMs) => Block(CmdStopVideo, timestampMs);

    public static byte[] StopMedia(uint timestampMs) => Block(CmdStopMedia, timestampMs);

    /// <summary>
    /// A full-screen JPEG (480×1920 portrait) ready for one bulk write: the command block with the
    /// image length, followed by the image bytes.
    /// </summary>
    public static byte[] JpegFrame(ReadOnlySpan<byte> jpeg, uint timestampMs)
    {
        if (jpeg.Length == 0 || jpeg.Length > MaxImageBytes)
        {
            throw new ArgumentException($"a frame must be 1..{MaxImageBytes} bytes (was {jpeg.Length})", nameof(jpeg));
        }

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)jpeg.Length);
        var block = Block(CmdJpeg, timestampMs, length);
        var write = new byte[BlockSize + jpeg.Length];
        block.CopyTo(write, 0);
        jpeg.CopyTo(write.AsSpan(BlockSize));
        return write;
    }

    /// <summary>Whether a reply acknowledges <paramref name="command"/> as done.</summary>
    public static bool IsOk(ReadOnlySpan<byte> reply, byte command) =>
        reply.Length >= 2 && reply[0] == command && reply[1] == ReplyOk;

    /// <summary>The firmware version from a SYNC reply (bytes 8..39, NUL-terminated), e.g. "turzx_0001_0015".</summary>
    public static string? FirmwareVersion(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 40 || reply[0] != CmdSync)
        {
            return null;
        }

        var text = reply.Slice(8, 32);
        var end = text.IndexOf((byte)0);
        var version = Encoding.ASCII.GetString(end >= 0 ? text[..end] : text).Trim();
        return version.Length > 0 ? version : null;
    }

    /// <summary>Milliseconds since local midnight: the timestamp the reference implementations send.</summary>
    public static uint TimestampMs(DateTime localNow) => (uint)(localNow - localNow.Date).TotalMilliseconds;
}
