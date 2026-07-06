using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeviceMaster.Core.Discovery;

/// <summary>
/// Restarts a USB device node (disable, settle, enable — SetupDi property change, the same
/// operation as Device Manager's "Disable/Enable device"). This is the software equivalent of
/// unplugging and reseating the device, for firmware that wedges beyond in-band resets.
/// Requires elevation; callers must positively identify the device before restarting it.
/// </summary>
public static class UsbDeviceRestarter
{
    /// <summary>
    /// Converts a device interface path to its PnP device instance id, e.g.
    /// <c>\\?\usb#vid_0416&amp;pid_8041#6&amp;abc&amp;0&amp;2#{guid}</c> → <c>usb\vid_0416&amp;pid_8041\6&amp;abc&amp;0&amp;2</c>.
    /// </summary>
    public static string? InstanceIdFromInterfacePath(string interfacePath)
    {
        var trimmed = interfacePath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? interfacePath[4..]
            : interfacePath;
        var parts = trimmed.Split('#');
        return parts.Length >= 3 ? $@"{parts[0]}\{parts[1]}\{parts[2]}" : null;
    }

    /// <summary>
    /// The PnP instance id of the device's parent (usually the USB hub it is plugged into),
    /// or null. Restarting the parent re-enumerates the port — the software equivalent of
    /// physically replugging a device that has fallen OFF the bus entirely.
    /// </summary>
    public static string? TryGetParentInstanceId(string pnpInstanceId)
    {
        if (CM_Locate_DevNodeW(out var node, pnpInstanceId, 0) != 0
            || CM_Get_Parent(out var parent, node, 0) != 0)
        {
            return null;
        }

        var buffer = new char[500];
        return CM_Get_Device_IDW(parent, buffer, buffer.Length, 0) == 0
            ? new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end && end >= 0 ? end : buffer.Length)
            : null;
    }

    /// <summary>Disables the device node, waits, and re-enables it. Throws on failure.</summary>
    public static void Restart(string pnpInstanceId, int settleMs = 2000)
    {
        var deviceInfoSet = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (deviceInfoSet == INVALID_HANDLE_VALUE)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfoList failed");
        }

        try
        {
            var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfo(deviceInfoSet, pnpInstanceId, IntPtr.Zero, 0, ref devInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetupDiOpenDeviceInfo failed for {pnpInstanceId}");
            }

            ChangeState(deviceInfoSet, ref devInfo, DICS_DISABLE);
            Thread.Sleep(settleMs);
            ChangeState(deviceInfoSet, ref devInfo, DICS_ENABLE);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static void ChangeState(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfo, uint stateChange)
    {
        // devcon parity: enable/disable use the config-specific scope
        var parameters = new SP_PROPCHANGE_PARAMS
        {
            ClassInstallHeader = new SP_CLASSINSTALL_HEADER
            {
                cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                InstallFunction = DIF_PROPERTYCHANGE,
            },
            StateChange = stateChange,
            Scope = DICS_FLAG_CONFIGSPECIFIC,
            HwProfile = 0,
        };

        if (!SetupDiSetClassInstallParams(deviceInfoSet, ref devInfo, ref parameters, (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>())
            || !SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, deviceInfoSet, ref devInfo))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"USB device state change (0x{stateChange:X}) failed — is the app elevated?");
        }
    }

    private const uint DIF_PROPERTYCHANGE = 0x12;
    private const uint DICS_ENABLE = 0x01;
    private const uint DICS_DISABLE = 0x02;
    private const uint DICS_FLAG_CONFIGSPECIFIC = 0x02;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiOpenDeviceInfo(IntPtr deviceInfoSet, string deviceInstanceId,
        IntPtr hwndParent, uint openFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_PROPCHANGE_PARAMS classInstallParams, uint classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, int bufferLength, uint flags);
}
