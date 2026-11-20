namespace DeviceMaster.Devices.Turzx;

/// <summary>
/// Single-DES encryption (FIPS 46-3) in CBC mode with PKCS7 padding: the cipher the Turzx 8.8" V1.x
/// USB panel uses for its 500-byte command headers (#102). Managed on purpose: .NET's DES goes
/// through OpenSSL on Linux, and OpenSSL 3 only offers DES in its legacy provider, which a minimal
/// container image doesn't load. Encrypt-only; DES is a wire format here, not a security measure.
/// </summary>
public static class DesCbc
{
    /// <summary>Encrypts <paramref name="plain"/> with PKCS7 padding (always 1–8 pad bytes).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != 8 || iv.Length != 8)
        {
            throw new ArgumentException("DES key and IV are 8 bytes");
        }

        var pad = 8 - (plain.Length % 8);
        var data = new byte[plain.Length + pad];
        plain.CopyTo(data);
        data.AsSpan(plain.Length).Fill((byte)pad);

        var subkeys = Subkeys(Load(key));
        var chain = Load(iv);
        for (var i = 0; i < data.Length; i += 8)
        {
            chain = EncryptBlock(Load(data.AsSpan(i, 8)) ^ chain, subkeys);
            Store(chain, data.AsSpan(i, 8));
        }

        return data;
    }

    /// <summary>One 64-bit block through the 16 Feistel rounds (exposed for the FIPS test vector).</summary>
    public static ulong EncryptBlock(ulong block, ulong key) => EncryptBlock(block, Subkeys(key));

    private static ulong EncryptBlock(ulong block, ulong[] subkeys)
    {
        var permuted = Permute(block, 64, Ip);
        var left = (uint)(permuted >> 32);
        var right = (uint)permuted;
        for (var round = 0; round < 16; round++)
        {
            var next = left ^ Feistel(right, subkeys[round]);
            left = right;
            right = next;
        }

        return Permute(((ulong)right << 32) | left, 64, Fp);
    }

    private static uint Feistel(uint half, ulong subkey)
    {
        var expanded = Permute(half, 32, E) ^ subkey; // 48 bits
        uint output = 0;
        for (var box = 0; box < 8; box++)
        {
            var six = (int)((expanded >> (42 - 6 * box)) & 0x3F);
            var row = ((six & 0x20) >> 4) | (six & 1);
            var column = (six >> 1) & 0xF;
            output = (output << 4) | S[box][row * 16 + column];
        }

        return (uint)Permute(output, 32, P);
    }

    private static ulong[] Subkeys(ulong key)
    {
        var cd = Permute(key, 64, Pc1); // 56 bits
        var c = (uint)(cd >> 28) & 0x0FFFFFFF;
        var d = (uint)cd & 0x0FFFFFFF;
        var keys = new ulong[16];
        for (var round = 0; round < 16; round++)
        {
            c = Rotate28(c, Shifts[round]);
            d = Rotate28(d, Shifts[round]);
            keys[round] = Permute(((ulong)c << 28) | d, 56, Pc2);
        }

        return keys;
    }

    private static uint Rotate28(uint value, int by) => ((value << by) | (value >> (28 - by))) & 0x0FFFFFFF;

    /// <summary>Bit permutation: table entries are 1-based positions counted from the MSB of an <paramref name="width"/>-bit input.</summary>
    private static ulong Permute(ulong input, int width, byte[] table)
    {
        ulong output = 0;
        foreach (var position in table)
        {
            output = (output << 1) | ((input >> (width - position)) & 1);
        }

        return output;
    }

    private static ulong Load(ReadOnlySpan<byte> bytes) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes);

    private static void Store(ulong value, Span<byte> bytes) => System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, value);

    // ---- FIPS 46-3 tables ----

    private static readonly byte[] Ip =
    [
        58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4,
        62, 54, 46, 38, 30, 22, 14, 6, 64, 56, 48, 40, 32, 24, 16, 8,
        57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3,
        61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7,
    ];

    private static readonly byte[] Fp =
    [
        40, 8, 48, 16, 56, 24, 64, 32, 39, 7, 47, 15, 55, 23, 63, 31,
        38, 6, 46, 14, 54, 22, 62, 30, 37, 5, 45, 13, 53, 21, 61, 29,
        36, 4, 44, 12, 52, 20, 60, 28, 35, 3, 43, 11, 51, 19, 59, 27,
        34, 2, 42, 10, 50, 18, 58, 26, 33, 1, 41, 9, 49, 17, 57, 25,
    ];

    private static readonly byte[] E =
    [
        32, 1, 2, 3, 4, 5, 4, 5, 6, 7, 8, 9, 8, 9, 10, 11, 12, 13, 12, 13, 14, 15, 16, 17,
        16, 17, 18, 19, 20, 21, 20, 21, 22, 23, 24, 25, 24, 25, 26, 27, 28, 29, 28, 29, 30, 31, 32, 1,
    ];

    private static readonly byte[] P =
    [
        16, 7, 20, 21, 29, 12, 28, 17, 1, 15, 23, 26, 5, 18, 31, 10,
        2, 8, 24, 14, 32, 27, 3, 9, 19, 13, 30, 6, 22, 11, 4, 25,
    ];

    private static readonly byte[] Pc1 =
    [
        57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18,
        10, 2, 59, 51, 43, 35, 27, 19, 11, 3, 60, 52, 44, 36,
        63, 55, 47, 39, 31, 23, 15, 7, 62, 54, 46, 38, 30, 22,
        14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 28, 20, 12, 4,
    ];

    private static readonly byte[] Pc2 =
    [
        14, 17, 11, 24, 1, 5, 3, 28, 15, 6, 21, 10, 23, 19, 12, 4,
        26, 8, 16, 7, 27, 20, 13, 2, 41, 52, 31, 37, 47, 55, 30, 40,
        51, 45, 33, 48, 44, 49, 39, 56, 34, 53, 46, 42, 50, 36, 29, 32,
    ];

    private static readonly int[] Shifts = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];

    private static readonly byte[][] S =
    [
        [14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7, 0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
         4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0, 15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13],
        [15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10, 3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5,
         0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15, 13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9],
        [10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8, 13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
         13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7, 1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12],
        [7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15, 13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
         10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4, 3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14],
        [2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9, 14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
         4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14, 11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3],
        [12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11, 10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
         9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6, 4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13],
        [4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1, 13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
         1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2, 6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12],
        [13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7, 1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
         7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8, 2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11],
    ];
}
