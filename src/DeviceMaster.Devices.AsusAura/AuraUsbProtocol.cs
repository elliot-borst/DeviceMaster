namespace DeviceMaster.Devices.AsusAura;

/// <summary>One logical LED zone on the Aura controller (onboard block or one ARGB header).</summary>
/// <param name="EffectChannel">Sequential channel index used by the 0x35 effect command.</param>
/// <param name="DirectChannel">Channel index used by the 0x40 direct command (onboard = 0x04, headers = raw index).</param>
/// <param name="LedCount">LED slots in effect addressing (onboard = real count, ARGB headers = 1).</param>
public sealed record AuraZone(byte EffectChannel, byte DirectChannel, int LedCount, bool IsAddressable);

/// <summary>
/// Pure packet builders/parsers for the ASUS Aura USB mainboard controller (0B05:19AF family),
/// ported from OpenRGB's AuraMainboardController (GPLv2 → clean-room byte spec). Every packet
/// is 65 bytes with report id 0xEC at [0]. No I/O — fully unit-testable.
/// </summary>
public static class AuraUsbProtocol
{
    public const int PacketSize = 65;
    public const byte ReportId = 0xEC;

    public const byte ModeStatic = 0x01;
    public const byte ModeDirect = 0xFF;

    /// <summary>Direct (0x40) packets carry at most 20 LEDs (60 color bytes).</summary>
    public const int LedsPerDirectPacket = 20;

    public static byte[] BuildFirmwareRequest() => Packet(0x82);

    public static byte[] BuildConfigTableRequest() => Packet(0xB0);

    /// <summary>Gen1-compat init the reference sends once after connect: EC 52 53 00 01.</summary>
    public static byte[] BuildInit()
    {
        var packet = Packet(0x52);
        packet[2] = 0x53;
        packet[3] = 0x00;
        packet[4] = 0x01;
        return packet;
    }

    /// <summary>True when a response packet matches the expected reply marker at [1].</summary>
    public static bool IsReply(ReadOnlySpan<byte> response, byte marker) =>
        response.Length >= 2 && response[1] == marker;

    /// <summary>Firmware reply (marker 0x02): 16-byte name string at [2..18].</summary>
    public static string ParseFirmwareName(ReadOnlySpan<byte> response)
    {
        var raw = response.Slice(2, 16);
        var zero = raw.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(zero >= 0 ? raw[..zero] : raw).Trim();
    }

    /// <summary>Config reply (marker 0x30): the 60-byte config table lives at [4..64].</summary>
    public static byte[] ParseConfigTable(ReadOnlySpan<byte> response) => response.Slice(4, 60).ToArray();

    /// <summary>
    /// Builds the zone list from the config table: an onboard fixed zone when
    /// config[0x1B] &gt; 0 (effect channel 0, direct channel 0x04), then one zone per
    /// addressable header (config[0x02] of them, direct channel = header index).
    /// </summary>
    public static IReadOnlyList<AuraZone> ParseZones(ReadOnlySpan<byte> configTable)
    {
        var zones = new List<AuraZone>();
        int onboardLeds = configTable[0x1B];
        int addressableHeaders = configTable[0x02];

        byte effectChannel = 0;
        if (onboardLeds > 0)
        {
            zones.Add(new AuraZone(effectChannel++, DirectChannel: 0x04, onboardLeds, IsAddressable: false));
        }

        for (byte i = 0; i < addressableHeaders; i++)
        {
            zones.Add(new AuraZone(effectChannel++, DirectChannel: i, LedCount: 1, IsAddressable: true));
        }

        return zones;
    }

    /// <summary>Effect select (0x35): EC 35 &lt;effectChannel&gt; 00 &lt;shutdown&gt; &lt;mode&gt;.</summary>
    public static byte[] BuildEffect(byte effectChannel, byte mode, bool shutdownEffect = false)
    {
        var packet = Packet(0x35);
        packet[2] = effectChannel;
        packet[4] = shutdownEffect ? (byte)0x01 : (byte)0x00;
        packet[5] = mode;
        return packet;
    }

    /// <summary>
    /// Effect color (0x36): 16-bit LED mask at [2..4] (big-endian), color triplets at
    /// [5 + 3*startLed]. <paramref name="startLed"/> is the sum of LED counts of all zones
    /// before this one; the mask covers this zone's slots.
    /// </summary>
    public static byte[] BuildEffectColor(int startLed, int ledCount, byte r, byte g, byte b, bool shutdownEffect = false)
    {
        var mask = ((1 << ledCount) - 1) << startLed;
        var packet = Packet(0x36);
        packet[2] = (byte)(mask >> 8);
        packet[3] = (byte)(mask & 0xFF);
        packet[4] = shutdownEffect ? (byte)0x01 : (byte)0x00;
        for (var i = 0; i < ledCount; i++)
        {
            var offset = 5 + 3 * (startLed + i);
            packet[offset] = r;
            packet[offset + 1] = g;
            packet[offset + 2] = b;
        }

        return packet;
    }

    /// <summary>
    /// Direct per-LED colors (0x40) for one channel, chunked at 20 LEDs; the final chunk
    /// carries the 0x80 apply bit. Colors are (r,g,b) per LED.
    /// </summary>
    public static IReadOnlyList<byte[]> BuildDirect(byte directChannel, IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        var packets = new List<byte[]>();
        for (var offset = 0; offset < colors.Count; offset += LedsPerDirectPacket)
        {
            var count = Math.Min(LedsPerDirectPacket, colors.Count - offset);
            var apply = offset + count == colors.Count;
            var packet = Packet(0x40);
            packet[2] = (byte)((apply ? 0x80 : 0x00) | directChannel);
            packet[3] = (byte)offset;
            packet[4] = (byte)count;
            for (var i = 0; i < count; i++)
            {
                var (r, g, b) = colors[offset + i];
                packet[5 + 3 * i] = r;
                packet[6 + 3 * i] = g;
                packet[7 + 3 * i] = b;
            }

            packets.Add(packet);
        }

        return packets;
    }

    /// <summary>Commit (0x3F 0x55): persists the current effect configuration to the controller.</summary>
    public static byte[] BuildCommit()
    {
        var packet = Packet(0x3F);
        packet[2] = 0x55;
        return packet;
    }

    private static byte[] Packet(byte command)
    {
        var packet = new byte[PacketSize];
        packet[0] = ReportId;
        packet[1] = command;
        return packet;
    }
}
