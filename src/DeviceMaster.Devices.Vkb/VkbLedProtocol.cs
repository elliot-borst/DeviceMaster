namespace DeviceMaster.Devices.Vkb;

/// <summary>How an LED mixes its two colours (VKB "colour mode").</summary>
public enum VkbColorMode
{
    Color1 = 0,
    Color2 = 1,
    Color1Then2 = 2,
    Color2Then1 = 3,
    Color1Plus2 = 4,
}

/// <summary>On, off or blinking (VKB "LED mode").</summary>
public enum VkbLedMode
{
    Off = 0,
    Constant = 1,
    SlowBlink = 2,
    FastBlink = 3,
    UltraBlink = 4,
}

/// <summary>A VKB colour: 0–7 per channel (the LEDs' own resolution).</summary>
public readonly record struct VkbColor(byte R, byte G, byte B)
{
    /// <summary>A 0–255 colour scaled to VKB's 0–7 levels (rounded, as VKB's own tool does).</summary>
    public static VkbColor FromRgb(byte r, byte g, byte b) => new(Level(r), Level(g), Level(b));

    private static byte Level(byte v) => (byte)Math.Round(v / 255.0 * 7, MidpointRounding.AwayFromZero);
}

/// <summary>One LED's override.</summary>
public sealed record VkbLedConfig(byte Led, VkbColorMode ColorMode, VkbLedMode LedMode, VkbColor Color1, VkbColor Color2);

/// <summary>
/// VKB LED override protocol (Gladiator NXT EVO and other VKB devices, VID 231D) — pure packet
/// building/parsing. Byte-level facts from pyvkb's <c>vkb/led.py</c> (Ventorvar, MIT), which uses
/// the same channel as VKB's own DevCfg "test LED" tool:
/// <list type="bullet">
/// <item>HID feature report <c>0x59</c>, 129 bytes including the id: <c>59 A5 0A</c> opcode, 2-byte
/// checksum (little-endian), 2 random bytes, the config count (≤ 12), then 4 bytes per LED.</item>
/// <item>Per LED: the LED id, then 24 bits of eight 3-bit fields — colour mode, LED mode, b2, g2, r2,
/// b1, g1, r1 — packed big-endian, with the 3 bytes then reversed.</item>
/// <item>Checksum: CRC-16 (init 0xFFFF, reflected poly 0xA001) over the first (count+1)×3 bytes
/// starting at the random bytes (VKB's choice; replicated exactly).</item>
/// <item>The override lasts until the device is power-cycled; other LEDs keep their profile.
/// Reading the report back returns the overrides currently set (none until one is).</item>
/// </list>
/// </summary>
public static class VkbLedProtocol
{
    public const byte ReportId = 0x59;
    public const int ReportLength = 129;
    public const int MaxConfigs = 12;
    public static readonly byte[] SetOpCode = [0x59, 0xA5, 0x0A];

    /// <summary>The 4 bytes of one LED config.</summary>
    public static byte[] EncodeConfig(VkbLedConfig c)
    {
        static uint F(int v) => (uint)(v & 7);
        var bits = (F((int)c.ColorMode) << 21) | (F((int)c.LedMode) << 18)
            | (F(c.Color2.B) << 15) | (F(c.Color2.G) << 12) | (F(c.Color2.R) << 9)
            | (F(c.Color1.B) << 6) | (F(c.Color1.G) << 3) | F(c.Color1.R);

        // big-endian 3 bytes, then reversed = little-endian
        return [c.Led, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16)];
    }

    public static VkbLedConfig DecodeConfig(ReadOnlySpan<byte> four)
    {
        var bits = four[1] | (uint)four[2] << 8 | (uint)four[3] << 16;
        byte F(int shift) => (byte)((bits >> shift) & 7);
        return new VkbLedConfig(four[0], (VkbColorMode)F(21), (VkbLedMode)F(18),
            Color1: new VkbColor(F(0), F(3), F(6)),
            Color2: new VkbColor(F(9), F(12), F(15)));
    }

    /// <summary>VKB's checksum over the first (count+1)×3 bytes of the body (which starts at the random bytes).</summary>
    public static ushort Checksum(int count, ReadOnlySpan<byte> body)
    {
        var chk = 0xFFFF;
        for (var i = 0; i < (count + 1) * 3 && i < body.Length; i++)
        {
            chk ^= body[i];
            for (var bit = 0; bit < 8; bit++)
            {
                var lsb = chk & 1;
                chk >>= 1;
                if (lsb != 0)
                {
                    chk ^= 0xA001;
                }
            }
        }

        return (ushort)chk;
    }

    /// <summary>The full 129-byte feature report setting up to 12 LEDs. <paramref name="nonce"/> = the 2 random bytes.</summary>
    public static byte[] BuildSet(IReadOnlyList<VkbLedConfig> configs, ushort nonce)
    {
        if (configs.Count is 0 or > MaxConfigs)
        {
            throw new ArgumentOutOfRangeException(nameof(configs), $"1–{MaxConfigs} LED configs per report");
        }

        var body = new List<byte> { (byte)nonce, (byte)(nonce >> 8), (byte)configs.Count };
        foreach (var c in configs)
        {
            body.AddRange(EncodeConfig(c));
        }

        var chk = Checksum(configs.Count, body.ToArray());
        var report = new byte[ReportLength];
        SetOpCode.CopyTo(report, 0);
        report[3] = (byte)chk;
        report[4] = (byte)(chk >> 8);
        body.ToArray().CopyTo(report, 5);
        return report;
    }

    /// <summary>The overrides a GET_FEATURE 0x59 returned (empty when none has been set since power-on).</summary>
    public static IReadOnlyList<VkbLedConfig> ParseGet(ReadOnlySpan<byte> report)
    {
        if (report.Length < 8 || !report[..3].SequenceEqual(SetOpCode))
        {
            return [];
        }

        var count = Math.Min((int)report[7], (report.Length - 8) / 4);
        var list = new List<VkbLedConfig>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(DecodeConfig(report.Slice(8 + i * 4, 4)));
        }

        return list;
    }
}
