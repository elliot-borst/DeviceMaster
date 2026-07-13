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

    /// <summary>Bridging an unchanged gap this small (px) is cheaper than a new 5-byte span header.</summary>
    public const int GapMergePx = 8;

    /// <summary>
    /// Diffs two native <see cref="ScreenWidth"/>×<see cref="ScreenHeight"/> BGRA frames and returns
    /// the changed pixel runs as concatenated <c>[3-byte BE address][2-byte BE width][BGRA run]</c>
    /// tuples for an UPDATE_BITMAP push. Emits MULTIPLE tight runs per row so an unchanged gap
    /// inside a row is not retransmitted — critical here because the landscape dashboard is rotated
    /// onto the portrait panel, so a single native row spans the CPU row, the FPS number and the GPU
    /// row; when only the ends change, a first-to-last span would resend the whole empty middle.
    /// Runs separated by ≤ <see cref="GapMergePx"/> unchanged pixels are merged. Returns null when
    /// more than half the frame changed (caller sends a full frame), or empty when nothing changed.
    /// </summary>
    public static byte[]? BuildPartialSpans(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> cur)
    {
        const int bpp = 4;
        const int stride = ScreenWidth * bpp;
        if (cur.Length != stride * ScreenHeight || prev.Length != cur.Length)
        {
            return null; // unexpected size — heal with a full frame
        }

        var maxChanged = cur.Length / 2;
        var changed = 0;
        var raw = new List<byte>(8192);

        for (var row = 0; row < ScreenHeight; row++)
        {
            var ro = row * stride;
            var curRow = cur.Slice(ro, stride);
            var prevRow = prev.Slice(ro, stride);
            if (curRow.SequenceEqual(prevRow))
            {
                continue;
            }

            var px = 0;
            while (px < ScreenWidth)
            {
                while (px < ScreenWidth && PixelEqual(curRow, prevRow, px))
                {
                    px++;
                }

                if (px >= ScreenWidth)
                {
                    break;
                }

                // extend the run over changed pixels, bridging unchanged gaps ≤ GapMergePx
                var runStart = px;
                var lastChanged = px;
                var gap = 0;
                px++;
                while (px < ScreenWidth)
                {
                    if (!PixelEqual(curRow, prevRow, px))
                    {
                        lastChanged = px;
                        gap = 0;
                    }
                    else if (++gap > GapMergePx)
                    {
                        break;
                    }

                    px++;
                }

                var widthPx = lastChanged - runStart + 1;
                var addr = (row * ScreenWidth) + runStart;
                raw.Add((byte)(addr >> 16));
                raw.Add((byte)(addr >> 8));
                raw.Add((byte)addr);
                raw.Add((byte)(widthPx >> 8));
                raw.Add((byte)widthPx);
                raw.AddRange(cur.Slice(ro + (runStart * bpp), widthPx * bpp));

                changed += widthPx * bpp;
                if (changed > maxChanged)
                {
                    return null;
                }
            }
        }

        return raw.Count == 0 ? [] : raw.ToArray();
    }

    private static bool PixelEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int px)
    {
        var o = px * 4;
        return a[o] == b[o] && a[o + 1] == b[o + 1] && a[o + 2] == b[o + 2] && a[o + 3] == b[o + 3];
    }

    /// <summary>A changed rectangular region in native portrait pixel coords, for one dense UPDATE_BITMAP.</summary>
    public readonly record struct ChangedRect(int Top, int Left, int Width, int Height);

    /// <summary>Consecutive changed rows this close vertically are merged into one band/rectangle.</summary>
    public const int RowBandGap = 24;

    /// <summary>Beyond this many rectangles, or this fraction of the frame changed, a full frame is cheaper.</summary>
    public const int MaxRects = 12;

    /// <summary>
    /// Diffs two native BGRA frames into a SMALL set of DENSE rectangles — the shape the panel
    /// actually paints. Unlike <see cref="BuildPartialSpans"/> (sparse multi-run tuples, which this
    /// rom-1.90 panel ACKs but never renders), each rectangle here is a contiguous block of rows of
    /// uniform width, exactly matching the reference's <c>_generate_update_image</c> (one rectangle
    /// per UPDATE_BITMAP). Changed rows within <see cref="RowBandGap"/> of each other are grouped into
    /// one band spanning min→max changed column. Returns null when too much changed / too many bands
    /// (caller heals with a full frame), or empty when nothing changed.
    /// </summary>
    public static IReadOnlyList<ChangedRect>? FindChangedRects(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> cur)
    {
        const int bpp = 4;
        const int stride = ScreenWidth * bpp;
        if (cur.Length != stride * ScreenHeight || prev.Length != cur.Length)
        {
            return null; // unexpected size — heal with a full frame
        }

        var rects = new List<ChangedRect>();
        // open band: [top, bottom] rows, [minCol, maxCol] changed columns
        var haveBand = false;
        int bandTop = 0, bandBottom = 0, bandMinCol = 0, bandMaxCol = 0;
        long totalArea = 0;

        void Flush()
        {
            if (!haveBand)
            {
                return;
            }

            rects.Add(new ChangedRect(bandTop, bandMinCol, bandMaxCol - bandMinCol + 1, bandBottom - bandTop + 1));
            totalArea += (long)(bandMaxCol - bandMinCol + 1) * (bandBottom - bandTop + 1);
            haveBand = false;
        }

        for (var row = 0; row < ScreenHeight; row++)
        {
            var ro = row * stride;
            var curRow = cur.Slice(ro, stride);
            var prevRow = prev.Slice(ro, stride);
            if (curRow.SequenceEqual(prevRow))
            {
                continue;
            }

            var minCol = -1;
            var maxCol = -1;
            for (var px = 0; px < ScreenWidth; px++)
            {
                if (!PixelEqual(curRow, prevRow, px))
                {
                    if (minCol < 0)
                    {
                        minCol = px;
                    }

                    maxCol = px;
                }
            }

            if (minCol < 0)
            {
                continue; // row differs only in ignored bytes — treat as unchanged
            }

            if (haveBand && row - bandBottom <= RowBandGap)
            {
                bandBottom = row;
                if (minCol < bandMinCol) bandMinCol = minCol;
                if (maxCol > bandMaxCol) bandMaxCol = maxCol;
            }
            else
            {
                Flush();
                haveBand = true;
                bandTop = bandBottom = row;
                bandMinCol = minCol;
                bandMaxCol = maxCol;
            }
        }

        Flush();

        if (rects.Count == 0)
        {
            return [];
        }

        if (rects.Count > MaxRects || totalArea > (long)ScreenWidth * ScreenHeight / 2)
        {
            return null; // too fragmented / too much changed — a full frame is cheaper and cleaner
        }

        return rects;
    }

    /// <summary>
    /// Builds the raw span stream for ONE dense rectangle: for every row of the rectangle, a single
    /// run <c>[3-byte BE addr = row*ScreenWidth + Left][2-byte BE Width][Width×4 BGRA]</c>. Feed this
    /// to <see cref="BuildUpdateHeader"/> / <see cref="BuildUpdatePixels"/> exactly like a partial —
    /// it is the reference's uniform-width rectangle, not our sparse multi-run.
    /// </summary>
    public static byte[] BuildRectangleSpans(ReadOnlySpan<byte> cur, ChangedRect rect)
    {
        const int bpp = 4;
        const int stride = ScreenWidth * bpp;
        var rowBytes = rect.Width * bpp;
        var raw = new byte[rect.Height * (5 + rowBytes)];
        var o = 0;
        for (var r = 0; r < rect.Height; r++)
        {
            var row = rect.Top + r;
            var addr = (row * ScreenWidth) + rect.Left;
            raw[o++] = (byte)(addr >> 16);
            raw[o++] = (byte)(addr >> 8);
            raw[o++] = (byte)addr;
            raw[o++] = (byte)(rect.Width >> 8);
            raw[o++] = (byte)rect.Width;
            cur.Slice((row * stride) + (rect.Left * bpp), rowBytes).CopyTo(raw.AsSpan(o));
            o += rowBytes;
        }

        return raw;
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
