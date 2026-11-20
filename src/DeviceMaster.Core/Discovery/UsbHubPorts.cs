using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeviceMaster.Core.Discovery;

/// <summary>What Windows' hub driver says is on one hub port (USB_CONNECTION_STATUS).</summary>
public enum UsbPortStatus
{
    NoDeviceConnected = 0,
    DeviceConnected = 1,
    DeviceFailedEnumeration = 2,
    DeviceGeneralFailure = 3,
    DeviceCausedOvercurrent = 4,
    DeviceNotEnoughPower = 5,
    DeviceNotEnoughBandwidth = 6,
    DeviceHubNestedTooDeeply = 7,
    DeviceInLegacyHub = 8,
    DeviceEnumerating = 9,
    DeviceReset = 10,
}

/// <summary>One port of one USB hub, as the hub driver reports it.</summary>
public sealed record UsbHubPort(string HubInstanceId, int Port, UsbPortStatus Status, ushort Vid, ushort Pid)
{
    /// <summary>
    /// A port whose device never came up: "Device Descriptor Request Failed" and friends. Windows
    /// removes the failed device node, so the hub port is the only place this stays visible.
    /// </summary>
    public bool Failed => Status is UsbPortStatus.DeviceFailedEnumeration or UsbPortStatus.DeviceGeneralFailure;
}

/// <summary>
/// Reads USB hub port states and cycles a hub port (#101) — the software equivalent of pulling a
/// cable and plugging it back in. Windows only; reading works unelevated, cycling needs admin.
/// </summary>
public static class UsbHubPorts
{
    /// <summary>Every port of every present USB hub (root hubs included). Unreadable hubs are skipped.</summary>
    public static IReadOnlyList<UsbHubPort> ReadAll()
    {
        var result = new List<UsbHubPort>();
        foreach (var path in HubInterfacePaths(null))
        {
            if (UsbDeviceRestarter.InstanceIdFromInterfacePath(path) is not { } hubId)
            {
                continue;
            }

            try
            {
                using var hub = Open(path);
                var ports = PortCount(hub);
                for (var port = 1; port <= ports; port++)
                {
                    if (ConnectionInfo(hub, port) is { } info)
                    {
                        result.Add(new UsbHubPort(hubId, port, info.Status, info.Vid, info.Pid));
                    }
                }
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            {
                // a hub that is going away or refuses to open: nothing to learn from it this round
            }
        }

        return result;
    }

    /// <summary>
    /// The port to cycle so everything behind <paramref name="hubInstanceId"/> re-enumerates as if
    /// the cable were replugged: the root-hub port of the outermost external hub in its chain, or,
    /// for a root hub, null (cycle the failed port itself instead).
    /// </summary>
    public static (string HubInstanceId, int Port, string TopHubInstanceId)? UpstreamPortOf(string hubInstanceId)
    {
        if (IsRootHub(hubInstanceId))
        {
            return null;
        }

        var top = hubInstanceId;
        while (UsbDeviceRestarter.TryGetParentInstanceId(top) is { } parent && !IsRootHub(parent))
        {
            if (!parent.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
            {
                return null; // not under a USB root hub (shouldn't happen)
            }

            top = parent;
        }

        if (UsbDeviceRestarter.TryGetParentInstanceId(top) is not { } root || PortNumber(top) is not { } port)
        {
            return null;
        }

        return (root, port, top);
    }

    /// <summary>Power-cycles (re-enumerates) one port of a hub. Needs elevation. Throws on failure.</summary>
    public static void CyclePort(string hubInstanceId, int port)
    {
        var path = HubInterfacePaths(hubInstanceId).FirstOrDefault()
                   ?? throw new IOException($"hub {hubInstanceId} has no device interface");
        using var hub = Open(path);
        var buffer = new byte[8];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), (uint)port);
        if (!DeviceIoControl(hub, IOCTL_USB_HUB_CYCLE_PORT, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"cycling port {port} of {hubInstanceId} failed — is the app elevated?");
        }
    }

    public static bool IsRootHub(string instanceId) =>
        instanceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase);

    private static SafeFileHandle Open(string path)
    {
        var handle = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"opening hub {path} failed");
        }

        return handle;
    }

    private static int PortCount(SafeFileHandle hub)
    {
        // USB_NODE_INFORMATION: NodeType (4), then USB_HUB_DESCRIPTOR: bDescriptorLength, bDescriptorType, bNumberOfPorts
        var buffer = new byte[128];
        return DeviceIoControl(hub, IOCTL_USB_GET_NODE_INFORMATION, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero)
            ? buffer[6]
            : 0;
    }

    private static (UsbPortStatus Status, ushort Vid, ushort Pid)? ConnectionInfo(SafeFileHandle hub, int port)
    {
        var buffer = new byte[512];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), (uint)port);
        if (!DeviceIoControl(hub, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, buffer, buffer.Length, buffer, buffer.Length, out var returned, IntPtr.Zero)
            || returned < ConnectionStatusOffset + 4)
        {
            return null;
        }

        return ParseConnectionInfo(buffer);
    }

    // USB_NODE_CONNECTION_INFORMATION_EX is byte-packed (usbioctl.h, pshpack1):
    // ConnectionIndex(4) DeviceDescriptor(18: idVendor at +8, idProduct at +10) CurrentConfigurationValue(1)
    // Speed(1) DeviceIsHub(1) DeviceAddress(2) NumberOfOpenPipes(4) ConnectionStatus(4)
    private const int ConnectionStatusOffset = 4 + 18 + 1 + 1 + 1 + 2 + 4;

    /// <summary>Decodes a USB_NODE_CONNECTION_INFORMATION_EX buffer (pure; exposed for tests).</summary>
    public static (UsbPortStatus Status, ushort Vid, ushort Pid) ParseConnectionInfo(ReadOnlySpan<byte> buffer) =>
        ((UsbPortStatus)BitConverter.ToUInt32(buffer[ConnectionStatusOffset..]),
         BitConverter.ToUInt16(buffer[(4 + 8)..]),
         BitConverter.ToUInt16(buffer[(4 + 10)..]));

    private static int? PortNumber(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var node, instanceId, 0) != 0)
        {
            return null;
        }

        uint value = 0;
        uint size = 4;
        return CM_Get_DevNode_Registry_PropertyW(node, CM_DRP_ADDRESS, out _, ref value, ref size, 0) == 0 && value > 0
            ? (int)value
            : null;
    }

    private static List<string> HubInterfacePaths(string? deviceInstanceId)
    {
        var guid = GuidDevinterfaceUsbHub;
        if (CM_Get_Device_Interface_List_SizeW(out var length, ref guid, deviceInstanceId, 0) != 0 || length < 2)
        {
            return [];
        }

        var buffer = new char[length];
        if (CM_Get_Device_Interface_ListW(ref guid, deviceInstanceId, buffer, length, 0) != 0)
        {
            return [];
        }

        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static readonly Guid GuidDevinterfaceUsbHub = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
    private const uint IOCTL_USB_GET_NODE_INFORMATION = 0x220408;
    private const uint IOCTL_USB_HUB_CYCLE_PORT = 0x220444;
    private const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x220448;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint CM_DRP_ADDRESS = 0x1D;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize,
        byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property, out uint regType,
        ref uint buffer, ref uint length, uint flags);
}
