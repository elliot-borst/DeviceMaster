using DeviceMaster.Core.Devices;
using HidSharp;

namespace DeviceMaster.Devices.AsusAura;

/// <summary>
/// Session to one ASUS Aura USB mainboard LED controller (0B05:19AF family). Protocol ported
/// from OpenRGB's AuraMainboardController. Static color drives every zone (onboard LEDs and
/// ARGB headers) through the controller's own effect engine, then commits so it persists.
/// Safety: construction refuses devices that are not registry-approved for writes.
/// </summary>
public sealed class AuraController : IDisposable
{
    public const ushort AsusVid = 0x0B05;
    private const int IoTimeoutMs = 500;

    private readonly HidStream _stream;
    private readonly object _ioLock = new();

    private AuraController(HidDevice device, HidStream stream)
    {
        _stream = stream;
        DevicePath = device.DevicePath;
        UsbId = new UsbId((ushort)device.VendorID, (ushort)device.ProductID);
    }

    public string DevicePath { get; }
    public UsbId UsbId { get; }
    public string FirmwareName { get; private set; } = "?";
    public IReadOnlyList<AuraZone> Zones { get; private set; } = [];
    public int TotalEffectLeds => Zones.Sum(z => z.LedCount);

    /// <summary>All Aura mainboard controllers present (65-byte report interface).</summary>
    public static IReadOnlyList<HidDevice> FindDevices() =>
        DeviceList.Local.GetHidDevices(AsusVid)
            .Where(d => KnownDeviceRegistry.Identify(new UsbId((ushort)d.VendorID, (ushort)d.ProductID))?.Kind
                    == DeviceKind.MotherboardRgbController)
            .Where(d =>
            {
                try { return d.GetMaxOutputReportLength() >= AuraUsbProtocol.PacketSize; }
                catch { return false; }
            })
            .ToList();

    public static AuraController Open(HidDevice device)
    {
        var usbId = new UsbId((ushort)device.VendorID, (ushort)device.ProductID);
        if (!KnownDeviceRegistry.IsWriteAllowed(usbId))
        {
            throw new InvalidOperationException($"Device {usbId} is not registry-approved for writes.");
        }

        if (!device.TryOpen(out var stream))
        {
            throw new InvalidOperationException(
                $"Could not open the Aura controller — is vendor software (Armoury Crate/Aura Sync) holding it?");
        }

        stream.ReadTimeout = IoTimeoutMs;
        stream.WriteTimeout = IoTimeoutMs;
        var controller = new AuraController(device, stream);
        try
        {
            controller.Initialize();
            return controller;
        }
        catch
        {
            controller.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        var firmware = Request(AuraUsbProtocol.BuildFirmwareRequest(), replyMarker: 0x02);
        FirmwareName = AuraUsbProtocol.ParseFirmwareName(firmware);

        var config = Request(AuraUsbProtocol.BuildConfigTableRequest(), replyMarker: 0x30);
        Zones = AuraUsbProtocol.ParseZones(AuraUsbProtocol.ParseConfigTable(config));
        if (Zones.Count == 0)
        {
            throw new InvalidOperationException("Aura controller reports no LED zones in its config table.");
        }

        Write(AuraUsbProtocol.BuildInit());
    }

    /// <summary>
    /// Sets every zone to one static color via the effect engine (mode 0x01). ARGB header
    /// strips are driven whole by the controller. Call <see cref="Commit"/> once the color
    /// has settled to persist it (the commit writes controller flash — don't spam it).
    /// </summary>
    public void ApplyStaticColor(byte r, byte g, byte b)
    {
        lock (_ioLock)
        {
            var startLed = 0;
            foreach (var zone in Zones)
            {
                Write(AuraUsbProtocol.BuildEffect(zone.EffectChannel, AuraUsbProtocol.ModeStatic));
                Write(AuraUsbProtocol.BuildEffectColor(startLed, zone.LedCount, r, g, b));
                startLed += zone.LedCount;
            }
        }
    }

    /// <summary>Persists the currently applied configuration across reboots (0x3F 0x55).</summary>
    public void Commit()
    {
        lock (_ioLock)
        {
            Write(AuraUsbProtocol.BuildCommit());
        }
    }

    private byte[] Request(byte[] packet, byte replyMarker)
    {
        lock (_ioLock)
        {
            Write(packet);
            var response = new byte[AuraUsbProtocol.PacketSize];
            _ = _stream.Read(response, 0, response.Length);
            if (!AuraUsbProtocol.IsReply(response, replyMarker))
            {
                throw new InvalidOperationException(
                    $"Aura controller answered 0x{response[1]:X2} where 0x{replyMarker:X2} was expected.");
            }

            return response;
        }
    }

    private void Write(byte[] packet) => _stream.Write(packet, 0, packet.Length);

    public void Dispose() => _stream.Dispose();
}
