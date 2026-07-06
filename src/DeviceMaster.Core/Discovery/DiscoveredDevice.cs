using DeviceMaster.Core.Devices;

namespace DeviceMaster.Core.Discovery;

public enum DeviceTransport { Usb, Hid, Serial }

/// <summary>A device found during a scan. Discovery never performs I/O on the device itself.</summary>
public sealed record DiscoveredDevice
{
    public required DeviceTransport Transport { get; init; }
    public required string Name { get; init; }

    /// <summary>HID device path, COM port name, or PnP instance id depending on transport.</summary>
    public required string Path { get; init; }

    public UsbId? UsbId { get; init; }
    public string? VendorName { get; init; }
    public string? SerialNumber { get; init; }
    public KnownDevice? Identification { get; init; }

    public int? MaxInputReportLength { get; init; }
    public int? MaxOutputReportLength { get; init; }
    public int? MaxFeatureReportLength { get; init; }

    public DeviceKind Kind => Identification?.Kind ?? DeviceKind.Unknown;
}
