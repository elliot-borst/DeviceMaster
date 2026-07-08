namespace DeviceMaster.Devices.Turzx;

/// <summary>
/// Pure packet builders for the Turzx / Turing 8.8" smart screen (VID 0x1A86:0xCA88, USB
/// serial over the <c>usbser</c> CDC driver). Ported from mathoudebine/turing-smart-screen-python
/// (lcd_comm_rev_c.py) — the CT88INCH panel is the REV_8INCH sub-revision. Every command is a
/// byte sequence padded up to a multiple of 250 bytes; a full frame is BGRA pixel data with a
/// single 0x00 byte inserted after every 249 payload bytes. No I/O here — fully unit-testable.
/// </summary>
public static class TurzxProtocol
{
    /// <summary>Native panel resolution (portrait). Landscape content is rotated onto it before sending.</summary>
    public const int ScreenWidth = 480;
    public const int ScreenHeight = 1920;

    /// <summary>Every command message is zero/marker-padded up to a multiple of this many bytes.</summary>
    public const int Block = 250;

    /// <summary>Payload bytes are grouped into runs of this length, joined by a single NULL byte.</summary>
    public const int PayloadChunk = 249;

    public const byte PaddingNull = 0x00;

    /// <summary>The bare START_DISPLAY_BITMAP command byte (also its own padding filler).</summary>
    public const byte StartDisplayBitmapByte = 0x2c;

    // Command prefixes, verbatim from the reference's Command enum.
    public static readonly byte[] Hello = [0x01, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xc5, 0xd3];
    public static readonly byte[] SetBrightnessCmd = [0x7b, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];
    public static readonly byte[] StopVideo = [0x79, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01];
    public static readonly byte[] StopMedia = [0x96, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01];
    public static readonly byte[] QueryStatus = [0xcf, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01];
    public static readonly byte[] PreUpdateBitmap = [0x86, 0xef, 0x69, 0x00, 0x00, 0x00, 0x01];
    public static readonly byte[] DisplayBitmap8Inch = [0xc8, 0xef, 0x69, 0x00, 0x38, 0x40];

    /// <summary>message = command + payload, then zero/marker-padded up to a multiple of 250 bytes.</summary>
    public static byte[] BuildCommand(ReadOnlySpan<byte> command, ReadOnlySpan<byte> payload = default, byte padding = PaddingNull)
    {
        var size = command.Length + payload.Length;
        var padded = size == 0 || size % Block == 0 ? size : ((size / Block) + 1) * Block;
        var message = new byte[padded];
        command.CopyTo(message);
        payload.CopyTo(message.AsSpan(command.Length));
        if (padding != PaddingNull)
        {
            for (var i = size; i < padded; i++)
            {
                message[i] = padding;
            }
        }

        return message;
    }

    /// <summary>Backlight brightness 0 (off) to 100, scaled onto the panel's 0–255 range.</summary>
    public static byte[] BuildBrightness(int percent)
    {
        var level = (byte)(Math.Clamp(percent, 0, 100) * 255 / 100);
        return BuildCommand(SetBrightnessCmd, [level]);
    }

    /// <summary>START_DISPLAY_BITMAP: the 0x2c byte, padded to 250 bytes of 0x2c.</summary>
    public static byte[] BuildStartDisplayBitmap() =>
        BuildCommand([StartDisplayBitmapByte], default, StartDisplayBitmapByte);

    /// <summary>DISPLAY_BITMAP_8INCH with the reference's width² / 64 size field (480² / 64 = 3600).</summary>
    public static byte[] BuildDisplayBitmap8Inch()
    {
        var size = ScreenWidth * ScreenWidth / 64; // 3600 = 0x0E10
        return BuildCommand(DisplayBitmap8Inch, [(byte)(size >> 8), (byte)size]);
    }

    /// <summary>The SEND_PAYLOAD frame: raw body (no command prefix), padded to a multiple of 250.</summary>
    public static byte[] BuildSendPayload(ReadOnlySpan<byte> body) => BuildCommand(default, body);

    /// <summary>UPDATE_BITMAP command prefix (partial-region refresh).</summary>
    public static readonly byte[] UpdateBitmap = [0xcc, 0xef, 0x69, 0x00];

    /// <summary>
    /// UPDATE_BITMAP header payload: <c>cc ef 69 00</c> + (rawSpanLen+2) as 3-byte BE + <c>00 00 00</c>
    /// + <paramref name="count"/> as 4-byte BE. The +2 accounts for the trailing <c>ef 69</c> that
    /// <see cref="BuildUpdatePixels"/> appends. Send this as a SEND_PAYLOAD frame.
    /// </summary>
    public static byte[] BuildUpdateHeader(int rawSpanLength, int count)
    {
        var size = rawSpanLength + 2;
        return
        [
            UpdateBitmap[0], UpdateBitmap[1], UpdateBitmap[2], UpdateBitmap[3],
            (byte)(size >> 16), (byte)(size >> 8), (byte)size,
            0x00, 0x00, 0x00,
            (byte)(count >> 24), (byte)(count >> 16), (byte)(count >> 8), (byte)count,
        ];
    }

    /// <summary>
    /// Wraps the raw span bytes (each span = 3-byte BE native address + 2-byte BE pixel width +
    /// that run's BGRA) into the UPDATE_BITMAP pixel payload: a single NULL byte after every 249
    /// bytes, then a trailing <c>ef 69</c>. Send as a SEND_PAYLOAD frame.
    /// </summary>
    public static byte[] BuildUpdatePixels(ReadOnlySpan<byte> rawSpans)
    {
        var joined = EncodeFullFrameBody(rawSpans); // NULL-join every 249 bytes (no-op if ≤ 249)
        var result = new byte[joined.Length + 2];
        joined.CopyTo(result, 0);
        result[^2] = 0xef;
        result[^1] = 0x69;
        return result;
    }

    /// <summary>
    /// Interleaves a single NULL byte after every 249 bytes of BGRA data — the reference's
    /// <c>b'\x00'.join(chunked(bgra_data, 249))</c> framing for the full-frame payload.
    /// </summary>
    public static byte[] EncodeFullFrameBody(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length <= PayloadChunk)
        {
            return bgra.ToArray();
        }

        var chunks = (bgra.Length + PayloadChunk - 1) / PayloadChunk;
        var body = new byte[bgra.Length + (chunks - 1)];
        int src = 0, dst = 0;
        while (src < bgra.Length)
        {
            var len = Math.Min(PayloadChunk, bgra.Length - src);
            bgra.Slice(src, len).CopyTo(body.AsSpan(dst));
            src += len;
            dst += len;
            if (src < bgra.Length)
            {
                body[dst++] = PaddingNull; // separator between 249-byte runs
            }
        }

        return body;
    }
}
