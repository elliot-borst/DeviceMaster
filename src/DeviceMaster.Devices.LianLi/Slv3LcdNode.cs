using DeviceMaster.Core.Devices;

namespace DeviceMaster.Devices.LianLi;

/// <summary>
/// One SL V3 fan's LCD node (1CBE:0005, WinUSB bulk). Each of the wireless LCD fans exposes
/// its own USB device for screen streaming only — fan speed/RGB go over the RF dongles.
/// Ported from sgtaziz/lian-li-linux slv3_lcd.rs (MIT).
/// </summary>
public sealed class Slv3LcdNode : IDisposable
{
    /// <summary>Microsoft's stock WinUSB device interface GUID, which these nodes register.</summary>
    public static readonly Guid WinUsbStockGuid = new("88BAE032-5A81-49F0-BC3D-A4FF138216D6");

    private const ushort NodeVid = 0x1CBE;
    private const ushort NodePid = 0x0005;
    private const int AckReadTimeoutMs = 500;

    private readonly WinUsbDevice _device;
    private readonly long _openedAtTick;
    private uint _lastTimestamp;
    private bool _initialized;

    private Slv3LcdNode(WinUsbDevice device, string serial)
    {
        _device = device;
        Serial = serial;
        _openedAtTick = Environment.TickCount64;
    }

    public string Serial { get; }

    /// <summary>All SL V3 fan LCD nodes currently on the bus (each fan carries one).</summary>
    public static IReadOnlyList<(string Path, string Serial)> FindNodes()
    {
        var nodes = new List<(string, string)>();
        foreach (var (path, usbId) in WinUsbDevice.Enumerate(WinUsbStockGuid))
        {
            if (usbId.Vid != NodeVid || usbId.Pid != NodePid || !KnownDeviceRegistry.IsWriteAllowed(usbId))
            {
                continue;
            }

            // interface path: \\?\usb#vid_1cbe&pid_0005#SERIAL#{guid}
            var parts = path.Split('#');
            nodes.Add((path, parts.Length >= 3 ? parts[2].ToUpperInvariant() : path));
        }

        return nodes;
    }

    public static Slv3LcdNode Open(string path, string serial)
    {
        var device = WinUsbDevice.Open(path, new UsbId(NodeVid, NodePid));
        return new Slv3LcdNode(device, serial);
    }

    /// <summary>Strictly increasing per-session millisecond timestamp (firmware requirement).</summary>
    private uint NextTimestamp()
    {
        var raw = (uint)(Environment.TickCount64 - _openedAtTick);
        _lastTimestamp = raw <= _lastTimestamp ? _lastTimestamp + 1 : raw;
        return _lastTimestamp;
    }

    private void SendHeader(byte[] header)
    {
        _device.Write(header);
        var ack = new byte[511];
        _ = _device.Read(ack, AckReadTimeoutMs); // best effort — the reference ignores it too
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        SendHeader(Slv3LcdProtocol.BuildInitHeader(NextTimestamp()));
        _initialized = true;
    }

    /// <summary>Pushes one JPEG frame (400×400); the panel keeps showing it.</summary>
    public void SendJpegFrame(byte[] jpeg)
    {
        EnsureInitialized();
        var header = Slv3LcdProtocol.BuildJpegHeader(jpeg.Length, NextTimestamp());
        _device.Write(Slv3LcdProtocol.BuildFrameTransfer(header, jpeg));
        var ack = new byte[511];
        _ = _device.Read(ack, AckReadTimeoutMs);
    }

    /// <summary>Backlight brightness 0 (off) to 100.</summary>
    public void SetBrightness(int percent) =>
        SendHeader(Slv3LcdProtocol.BuildBrightnessHeader(percent, NextTimestamp()));

    public void Dispose() => _device.Dispose();
}
