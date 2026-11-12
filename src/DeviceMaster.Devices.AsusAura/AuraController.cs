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
    public byte[] ConfigTable { get; private set; } = [];

    /// <summary>Per-zone errors from the last apply (empty = every zone accepted its packets).</summary>
    public IReadOnlyList<string> LastZoneErrors { get; private set; } = [];
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
        ConfigTable = AuraUsbProtocol.ParseConfigTable(config);
        Zones = AuraUsbProtocol.ParseZones(ConfigTable, FirmwareName);
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
            var errors = new List<string>();
            var startLed = 0;
            foreach (var zone in Zones)
            {
                // each zone on its own: one zone the packet format can't address must not
                // leave the remaining headers unpainted
                try
                {
                    Write(AuraUsbProtocol.BuildEffect(zone.EffectChannel, AuraUsbProtocol.ModeStatic));
                    Write(AuraUsbProtocol.BuildEffectColor(startLed, zone.LedCount, r, g, b));
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    errors.Add($"{ZoneName(zone)}: {ex.Message}");
                }

                startLed += zone.LedCount;
            }

            LastZoneErrors = errors;
            if (errors.Count == Zones.Count)
            {
                throw new InvalidOperationException($"no Aura zone accepted the color: {string.Join("; ", errors)}");
            }
        }
    }

    /// <summary>Every zone on the controller's own rainbow effect (it animates by itself).</summary>
    public void ApplyRainbow()
    {
        lock (_ioLock)
        {
            foreach (var zone in Zones)
            {
                Write(AuraUsbProtocol.BuildEffect(zone.EffectChannel, AuraUsbProtocol.ModeRainbow));
            }

            LastZoneErrors = [];
        }
    }

    /// <summary>One-line description for diagnostics: firmware, config table, zone map.</summary>
    public string Describe()
    {
        var zones = string.Join(", ", Zones.Select(z => $"{ZoneName(z)}[eff {z.EffectChannel}, dir 0x{z.DirectChannel:X2}, {z.LedCount} slot(s)]"));
        var text = $"Aura '{FirmwareName}': {AuraUsbProtocol.DescribeConfigTable(ConfigTable)}; zones: {zones}";
        return LastZoneErrors.Count == 0 ? text : $"{text}; zone errors: {string.Join("; ", LastZoneErrors)}";
    }

    private static string ZoneName(AuraZone zone) => zone.IsAddressable ? $"ARGB header {zone.DirectChannel + 1}" : "onboard";

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
