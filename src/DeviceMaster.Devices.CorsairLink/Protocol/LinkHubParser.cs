using System.Buffers.Binary;
using System.Text;

namespace DeviceMaster.Devices.CorsairLink.Protocol;

public sealed record LinkSubDevice(int Channel, string Id, byte Model, byte Variant);

public sealed record LinkSpeedReading(int Channel, bool IsAvailable, int? Rpm);

public sealed record LinkTemperatureReading(int Channel, bool IsAvailable, float? TemperatureCelsius);

/// <summary>
/// Pure parsers for iCUE LINK hub responses. All methods take the response as returned by the
/// transport layer (raw packet with the first byte already stripped), matching the offsets in
/// EvanMulawski/FanControl.CorsairLink LinkHubDataReader (MIT). No I/O — fully unit-testable.
/// </summary>
public static class LinkHubParser
{
    /// <summary>Parses the ReadFirmwareVersion response into "major.minor.patch".</summary>
    public static string GetFirmwareVersion(ReadOnlySpan<byte> packet)
    {
        var major = (int)packet[4];
        var minor = (int)packet[5];
        var patch = BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(6, 2));
        return $"{major}.{minor}.{patch}";
    }

    /// <summary>
    /// Parses the sub-device (Link chain) enumeration. Payload layout after headers:
    /// [0]=last channel index, then per channel an 8-byte header ([2]=model, [3]=variant,
    /// [7]=id length; all-zero header = empty channel) followed by the ASCII device id.
    /// Channels start at 1.
    /// </summary>
    public static IReadOnlyList<LinkSubDevice> ParseSubDevices(
        ReadOnlySpan<byte> packet, ReadOnlySpan<byte> continuationPacket = default)
    {
        var part1 = packet.Slice(6);
        var part2 = continuationPacket.Length > 4 ? continuationPacket.Slice(4) : [];

        var payload = new byte[part1.Length + part2.Length];
        part1.CopyTo(payload);
        part2.CopyTo(payload.AsSpan(part1.Length));

        return ParseSubDevicePayload(payload);
    }

    private static List<LinkSubDevice> ParseSubDevicePayload(ReadOnlySpan<byte> payload)
    {
        var devices = new List<LinkSubDevice>();
        if (payload.Length == 0)
        {
            return devices;
        }

        var lastChannel = payload[0];
        var d = payload.Slice(1);
        var i = 0;

        for (var channel = 1; channel <= lastChannel; channel++)
        {
            int idLength = d[i + 7];
            if (idLength == 0)
            {
                i += 8;
                continue;
            }

            var header = d.Slice(i, 8);
            var idStart = i + 8;
            var isPacketEnd = idStart + idLength > d.Length;

            var idSpan = d.Slice(idStart, Math.Min(idLength, d.Length - idStart));
            var zeroIndex = idSpan.IndexOf((byte)0);
            var idBytes = zeroIndex >= 0 ? idSpan.Slice(0, zeroIndex) : idSpan;
            var id = Encoding.ASCII.GetString(idBytes);

            devices.Add(new LinkSubDevice(channel, id, Model: header[2], Variant: header[3]));

            if (isPacketEnd)
            {
                break;
            }

            i += 8 + id.Length;
        }

        return devices;
    }

    /// <summary>Parses the GetSpeeds payload: [6]=sensor count, then per sensor [status, rpm int16le].</summary>
    public static IReadOnlyList<LinkSpeedReading> ParseSpeeds(ReadOnlySpan<byte> packet)
    {
        var count = packet[6];
        var data = packet.Slice(7);
        var readings = new List<LinkSpeedReading>(count);

        for (int i = 0, offset = 0; i < count; i++, offset += 3)
        {
            var sensor = data.Slice(offset, 3);
            var available = sensor[0] == 0x00;
            int? rpm = available ? BinaryPrimitives.ReadInt16LittleEndian(sensor.Slice(1, 2)) : null;
            readings.Add(new LinkSpeedReading(i, available, rpm));
        }

        return readings;
    }

    /// <summary>
    /// Parses the GetLeds payload: [6]=channel count, then per channel (1-based, 4 bytes each
    /// starting at [7]+i*4): [status u16le (2 = connected), led count u16le]. Returns
    /// channel → LED count for connected channels (OpenLinkHub getLedDevices).
    /// </summary>
    public static IReadOnlyDictionary<int, int> ParseLedCounts(ReadOnlySpan<byte> packet)
    {
        var counts = new Dictionary<int, int>();
        var channels = packet[6];
        var data = packet.Slice(7);
        for (var i = 1; i <= channels; i++)
        {
            if (i * 4 + 4 > data.Length)
            {
                break;
            }

            var connected = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 4, 2)) == 2;
            if (connected)
            {
                var leds = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 4 + 2, 2));
                counts[i] = Math.Min((int)leds, 50); // reference caps at 50 per device
            }
        }

        return counts;
    }

    /// <summary>
    /// Parses the hub's LED registry (endpoint 0x1E via the color handle): [6]=slot count
    /// (max channel + 1), then per channel — 0-based, channel 0 always empty — either
    /// 0x00 (no LED device) or 0x01 followed by the device's LED command code.
    /// This table is persisted in hub flash and only rewritten by vendor software; it goes
    /// stale when the chain is re-arranged, leaving LEDs mapped to phantom channels.
    /// </summary>
    public static IReadOnlyDictionary<int, byte> ParseLedRegistry(ReadOnlySpan<byte> packet)
    {
        var registry = new Dictionary<int, byte>();
        int slots = packet[6];
        var offset = 7;
        for (var channel = 0; channel < slots && offset < packet.Length; channel++)
        {
            if (packet[offset] == 0x01 && offset + 1 < packet.Length)
            {
                registry[channel] = packet[offset + 1];
                offset += 2;
            }
            else
            {
                offset += 1;
            }
        }

        return registry;
    }

    /// <summary>Parses the GetTemperatures payload: [6]=sensor count, then per sensor [status, temp*10 int16le].</summary>
    public static IReadOnlyList<LinkTemperatureReading> ParseTemperatures(ReadOnlySpan<byte> packet)
    {
        var count = packet[6];
        var data = packet.Slice(7);
        var readings = new List<LinkTemperatureReading>(count);

        for (int i = 0, offset = 0; i < count; i++, offset += 3)
        {
            var sensor = data.Slice(offset, 3);
            var available = sensor[0] == 0x00;
            float? temperature = available
                ? BinaryPrimitives.ReadInt16LittleEndian(sensor.Slice(1, 2)) / 10f
                : null;
            readings.Add(new LinkTemperatureReading(i, available, temperature));
        }

        return readings;
    }
}
