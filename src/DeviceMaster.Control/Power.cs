using System.Net;
using System.Net.Sockets;

namespace DeviceMaster.Control;

/// <summary>Wake-on-LAN (#49): the magic packet and a LAN broadcast sender.</summary>
public static class WakeOnLan
{
    /// <summary>Parses "aa:bb:cc:dd:ee:ff" / "aa-bb-…" / "aabbccddeeff"; null when it isn't a MAC.</summary>
    public static byte[]? ParseMac(string? text)
    {
        var hex = new string((text ?? "").Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12 || (text ?? "").Count(char.IsLetterOrDigit) != 12)
        {
            return null;
        }

        return Convert.FromHexString(hex);
    }

    /// <summary>6 × 0xFF then the MAC 16 times (102 bytes).</summary>
    public static byte[] MagicPacket(byte[] mac)
    {
        if (mac.Length != 6)
        {
            throw new ArgumentException("a MAC is 6 bytes", nameof(mac));
        }

        var packet = new byte[102];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
        {
            mac.CopyTo(packet, 6 + i * 6);
        }

        return packet;
    }

    /// <summary>Broadcasts the packet on the LAN (UDP 9 and 7, three times). Needs host networking in a container.</summary>
    public static void Send(byte[] mac)
    {
        var packet = MagicPacket(mac);
        using var udp = new UdpClient { EnableBroadcast = true };
        for (var i = 0; i < 3; i++)
        {
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 7));
        }
    }
}

/// <summary>
/// A clean shutdown requested through a single-purpose host helper (#49): the agent (in a
/// container) writes a request file into a directory shared with the host; the helper there
/// watches it and runs the host's own clean shutdown. The container never gets wider host reach.
/// </summary>
public static class ShutdownRequest
{
    public const string FileName = "shutdown-request";

    /// <summary>Writes the request (who, when); the helper deletes it when it acts. Returns false if the folder isn't there.</summary>
    public static bool Write(string directory, string reason)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var path = Path.Combine(directory, FileName);
        File.WriteAllText(path + ".tmp", $"{DateTime.UtcNow:O} {reason}\n");
        File.Move(path + ".tmp", path, overwrite: true);
        return true;
    }

    /// <summary>Withdraws a request the helper hasn't acted on yet.</summary>
    public static bool Cancel(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
