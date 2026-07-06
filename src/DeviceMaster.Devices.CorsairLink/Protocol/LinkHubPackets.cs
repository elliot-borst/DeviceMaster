using System.Buffers.Binary;

namespace DeviceMaster.Devices.CorsairLink.Protocol;

/// <summary>
/// Pure packet builders for the iCUE LINK System Hub. Ported from
/// EvanMulawski/FanControl.CorsairLink LinkHubDataWriter (MIT). No I/O here — fully unit-testable.
/// </summary>
public static class LinkHubPackets
{
    /// <summary>
    /// Builds a full outgoing HID packet:
    /// [0]=0x00 report id, [1]=0x00, [2]=0x01, [3..]=command, then data. Zero-padded to 513 bytes.
    /// </summary>
    public static byte[] CreateCommandPacket(ReadOnlySpan<byte> command, ReadOnlySpan<byte> data = default)
    {
        const int headerLength = 3;

        var packet = new byte[LinkHubProtocol.PacketSizeOut];
        packet[2] = 0x01;
        command.CopyTo(packet.AsSpan(headerLength));
        if (data.Length > 0)
        {
            data.CopyTo(packet.AsSpan(headerLength + command.Length));
        }

        return packet;
    }

    /// <summary>
    /// Builds the payload for a Write command:
    /// [0,1]=little-endian payload length (data + 2), [2,3]=0x00, [4,5]=data type, [6..]=data.
    /// </summary>
    public static byte[] CreateWriteData(ReadOnlySpan<byte> dataType, ReadOnlySpan<byte> data)
    {
        const int headerLength = 4;

        var buffer = new byte[dataType.Length + data.Length + headerLength];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), (short)(data.Length + 2));
        dataType.CopyTo(buffer.AsSpan(headerLength));
        data.CopyTo(buffer.AsSpan(headerLength + dataType.Length));

        return buffer;
    }

    /// <summary>
    /// Builds the fixed-percent speed data block:
    /// [0]=channel count, then per channel [id, 0x00, percent, 0x00].
    /// Duties must already be safety-clamped by the caller — this is a dumb serializer.
    /// </summary>
    public static byte[] CreateSoftwareSpeedFixedPercentData(IReadOnlyDictionary<int, byte> channelDuties)
    {
        var data = new byte[channelDuties.Count * 4 + 1];
        data[0] = (byte)channelDuties.Count;
        var i = 1;

        foreach (var (channel, duty) in channelDuties)
        {
            data[i] = (byte)channel;
            data[i + 2] = duty;
            i += 4;
        }

        return data;
    }
}
