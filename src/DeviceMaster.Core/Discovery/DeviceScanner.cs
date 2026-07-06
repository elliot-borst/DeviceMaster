using System.Management;
using System.Text.RegularExpressions;
using DeviceMaster.Core.Devices;
using HidSharp;

namespace DeviceMaster.Core.Discovery;

/// <summary>A node in the Windows PnP USB tree (composite parents and their interface children).</summary>
public sealed record UsbTreeNode(string PnpInstanceId, string Name, UsbId? UsbId, string? DriverService)
{
    /// <summary>True for physical device entries (interface children carry an &amp;MI_ suffix).</summary>
    public bool IsPhysicalDevice => !PnpInstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase);
}

public sealed record SerialPortInfo(
    string ComPort, string Name, string PnpInstanceId, UsbId? UsbId, string? SerialHint, KnownDevice? Identification);

public sealed record DeviceScanResult(
    IReadOnlyList<UsbTreeNode> UsbTree,
    IReadOnlyList<DiscoveredDevice> HidDevices,
    IReadOnlyList<SerialPortInfo> SerialPorts);

/// <summary>
/// Read-only enumeration of USB, HID and serial devices. Never opens a device for I/O —
/// discovery must stay side-effect free (safety rule: no writes to anything not positively
/// identified). The PnP USB tree is the authoritative presence list; it also covers WinUSB
/// devices (Lian Li SL V3) that neither HidSharp nor the serial scan can see.
/// </summary>
public static partial class DeviceScanner
{
    public static DeviceScanResult ScanAll() => new(ScanUsbTree(), ScanHidDevices(), ScanSerialPorts());

    public static IReadOnlyList<UsbTreeNode> ScanUsbTree()
    {
        var nodes = new List<UsbTreeNode>();
        using var searcher = new ManagementObjectSearcher(
            @"SELECT Name, PNPDeviceID, Service FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\VID%'");
        foreach (var obj in searcher.Get())
        {
            using var entity = (ManagementObject)obj;
            var pnpId = entity["PNPDeviceID"] as string ?? string.Empty;
            var name = entity["Name"] as string ?? "(unnamed)";
            var service = entity["Service"] as string;
            UsbId? usbId = Devices.UsbId.TryFromPnpInstanceId(pnpId, out var id) ? id : null;
            nodes.Add(new UsbTreeNode(pnpId, name, usbId, service));
        }

        return nodes;
    }

    public static IReadOnlyList<DiscoveredDevice> ScanHidDevices()
    {
        var devices = new List<DiscoveredDevice>();
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            var id = new UsbId((ushort)device.VendorID, (ushort)device.ProductID);
            devices.Add(new DiscoveredDevice
            {
                Transport = DeviceTransport.Hid,
                UsbId = id,
                Name = TryGet(device.GetProductName) ?? TryGet(device.GetFriendlyName) ?? "(unknown HID device)",
                SerialNumber = TryGet(device.GetSerialNumber),
                Path = device.DevicePath,
                Identification = KnownDeviceRegistry.Identify(id),
                VendorName = KnownDeviceRegistry.VendorName(id.Vid),
                MaxInputReportLength = TryGetLength(device.GetMaxInputReportLength),
                MaxOutputReportLength = TryGetLength(device.GetMaxOutputReportLength),
                MaxFeatureReportLength = TryGetLength(device.GetMaxFeatureReportLength),
            });
        }

        return devices;

        static string? TryGet(Func<string> get)
        {
            try { return get(); } catch { return null; }
        }

        static int? TryGetLength(Func<int> get)
        {
            try { return get(); } catch { return null; }
        }
    }

    public static IReadOnlyList<SerialPortInfo> ScanSerialPorts()
    {
        var ports = new List<SerialPortInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
        foreach (var obj in searcher.Get())
        {
            using var entity = (ManagementObject)obj;
            var name = entity["Name"] as string ?? string.Empty;
            var pnpId = entity["PNPDeviceID"] as string ?? string.Empty;
            var match = ComPortPattern().Match(name);
            if (!match.Success) continue;

            UsbId? usbId = Devices.UsbId.TryFromPnpInstanceId(pnpId, out var id) ? id : null;
            var serialHint = usbId is null ? null : pnpId.Split('\\').LastOrDefault();
            ports.Add(new SerialPortInfo(
                match.Groups[1].Value, name, pnpId, usbId, serialHint,
                usbId is { } u ? KnownDeviceRegistry.Identify(u) : null));
        }

        return ports.OrderBy(p => p.ComPort.Length).ThenBy(p => p.ComPort, StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex(@"\((COM\d+)\)")]
    private static partial Regex ComPortPattern();
}
