using DeviceMaster.Core.Devices;
using HidSharp;

namespace DeviceMaster.Devices.Vkb;

/// <summary>
/// One VKB device's LED override channel (feature report 0x59). Overrides are volatile — the device
/// returns to its own profile when power-cycled — so nothing here can leave a stick misconfigured.
/// Safety: refuses devices the registry doesn't allow writes to, and interfaces without the 0x59 report.
/// </summary>
public sealed class VkbController : IDisposable
{
    public const ushort VkbVid = 0x231D;

    private readonly HidStream _stream;
    private readonly object _io = new();

    private VkbController(HidDevice device, HidStream stream)
    {
        _stream = stream;
        UsbId = new UsbId((ushort)device.VendorID, (ushort)device.ProductID);
        Name = SafeName(device);
    }

    public UsbId UsbId { get; }

    public string Name { get; }

    /// <summary>VKB interfaces with the LED feature report.</summary>
    public static IReadOnlyList<HidDevice> FindDevices() =>
        DeviceList.Local.GetHidDevices(VkbVid)
            .Where(d => KnownDeviceRegistry.Identify(new UsbId((ushort)d.VendorID, (ushort)d.ProductID)) is not null)
            .Where(HasLedReport)
            .ToList();

    private static bool HasLedReport(HidDevice device)
    {
        try
        {
            return device.GetReportDescriptor().FeatureReports.Any(r => r.ReportID == VkbLedProtocol.ReportId && r.Length >= VkbLedProtocol.ReportLength - 1);
        }
        catch
        {
            return false;
        }
    }

    public static VkbController Open(HidDevice device)
    {
        var id = new UsbId((ushort)device.VendorID, (ushort)device.ProductID);
        if (!KnownDeviceRegistry.IsWriteAllowed(id))
        {
            throw new InvalidOperationException($"Device {id} is not registry-approved for writes.");
        }

        if (!HasLedReport(device))
        {
            throw new InvalidOperationException($"Device {id} has no VKB LED report (0x59).");
        }

        if (!device.TryOpen(out var stream))
        {
            throw new InvalidOperationException($"Could not open VKB device {id}.");
        }

        stream.ReadTimeout = 1000;
        stream.WriteTimeout = 1000;
        return new VkbController(device, stream);
    }

    /// <summary>The LED overrides currently set (empty until something has been set since power-on).</summary>
    public IReadOnlyList<VkbLedConfig> ReadOverrides()
    {
        lock (_io)
        {
            var buffer = new byte[VkbLedProtocol.ReportLength];
            buffer[0] = VkbLedProtocol.ReportId;
            _stream.GetFeature(buffer);
            return VkbLedProtocol.ParseGet(buffer);
        }
    }

    /// <summary>Overrides up to 12 LEDs per report (more are sent in further reports).</summary>
    public void SetLeds(IReadOnlyList<VkbLedConfig> configs)
    {
        lock (_io)
        {
            foreach (var chunk in configs.Chunk(VkbLedProtocol.MaxConfigs))
            {
                _stream.SetFeature(VkbLedProtocol.BuildSet(chunk, (ushort)Random.Shared.Next(0, 0x10000)));
            }
        }
    }

    /// <summary>Every given LED one steady colour (black = off).</summary>
    public void SetStatic(IEnumerable<byte> leds, byte r, byte g, byte b)
    {
        var color = VkbColor.FromRgb(r, g, b);
        var mode = color is { R: 0, G: 0, B: 0 } ? VkbLedMode.Off : VkbLedMode.Constant;
        SetLeds(leds.Select(id => new VkbLedConfig(id, VkbColorMode.Color1, mode, color, color)).ToList());
    }

    private static string SafeName(HidDevice device)
    {
        try
        {
            return device.GetProductName();
        }
        catch
        {
            return $"VKB {device.ProductID:X4}";
        }
    }

    public void Dispose() => _stream.Dispose();
}
