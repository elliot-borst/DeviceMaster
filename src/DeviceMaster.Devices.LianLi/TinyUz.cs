namespace DeviceMaster.Devices.LianLi;

/// <summary>
/// Literal-only TinyUZ encoder, ported from phstudy/uni-wireless-sync tinyuz.py (MIT).
/// The SL V3 fan firmware expects RGB payloads in TinyUZ format; emitting every byte as a
/// literal (no dictionary matches) produces slightly larger streams the firmware still accepts.
/// Stream: 4-byte little-endian dictionary size, then interleaved type-bit bytes + literals,
/// terminated by control code 3 (stream end) and a zero dictionary position.
/// </summary>
public static class TinyUz
{
    private const int DictSize = 4096;

    public static byte[] Compress(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            throw new ArgumentException("payload cannot be empty");
        }

        var code = new List<byte>(payload.Length + payload.Length / 8 + 16);
        for (var shift = 0; shift < 4; shift++)
        {
            code.Add((byte)((DictSize >> (8 * shift)) & 0xFF));
        }

        var typeCount = 0;
        var typesIndex = -1;

        void OutType(int bit)
        {
            if (typeCount == 0)
            {
                typesIndex = code.Count;
                code.Add(0);
            }

            code[typesIndex] |= (byte)((bit & 1) << typeCount);
            typeCount = (typeCount + 1) % 8;
        }

        void OutLen(int value, int packBit)
        {
            // split the value into packBit-sized chunks with continuation bits (tinyuz varint)
            var count = 1;
            var v = value;
            while (true)
            {
                var threshold = 1 << (count * packBit);
                if (v < threshold)
                {
                    break;
                }

                v -= threshold;
                count++;
            }

            for (var idx = count - 1; idx >= 0; idx--)
            {
                for (var bitIndex = 0; bitIndex < packBit; bitIndex++)
                {
                    var shift = idx * packBit + bitIndex;
                    OutType((v >> shift) & 1);
                }

                OutType(idx > 0 ? 1 : 0);
            }
        }

        // literals
        foreach (var b in payload)
        {
            OutType(1); // data
            code.Add(b);
        }

        // stream end: ctrl code 3
        OutType(0); // dict
        OutLen(3, packBit: 1);
        OutType(0); // is_have_data_back was set by the literals
        code.Add(0x00); // dictionary position 0

        return [.. code];
    }
}
