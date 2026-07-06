using System.ComponentModel;
using System.Runtime.InteropServices;
using DeviceMaster.Core.Devices;
using Microsoft.Win32.SafeHandles;

namespace DeviceMaster.Devices.LianLi;

/// <summary>
/// Minimal WinUSB wrapper (no native dependencies) for the SL V3 dongles, which Windows binds
/// to WINUSB with device interface GUID {1D4B2365-4749-48EA-B38A-7C6FDDDD7E26}. Pipes are
/// discovered from the interface descriptor (first IN + first OUT endpoint).
/// </summary>
public sealed class WinUsbDevice : IDisposable
{
    /// <summary>DeviceInterfaceGUIDs value registered for both SL V3 dongles (verified in registry).</summary>
    public static readonly Guid Slv3InterfaceGuid = new("1D4B2365-4749-48EA-B38A-7C6FDDDD7E26");

    private readonly SafeFileHandle _fileHandle;
    private readonly IntPtr _winUsbHandle;

    public string DevicePath { get; }
    public UsbId UsbId { get; }
    public byte InPipe { get; }
    public byte OutPipe { get; }

    private WinUsbDevice(string devicePath, UsbId usbId, SafeFileHandle fileHandle, IntPtr winUsbHandle, byte inPipe, byte outPipe)
    {
        DevicePath = devicePath;
        UsbId = usbId;
        _fileHandle = fileHandle;
        _winUsbHandle = winUsbHandle;
        InPipe = inPipe;
        OutPipe = outPipe;
    }

    /// <summary>Device interface paths (with parsed VID/PID) registered under the given GUID.</summary>
    public static IReadOnlyList<(string Path, UsbId UsbId)> Enumerate(Guid interfaceGuid)
    {
        var results = new List<(string, UsbId)>();
        var deviceInfoSet = SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (deviceInfoSet == INVALID_HANDLE_VALUE)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed");
        }

        try
        {
            var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint index = 0; SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref interfaceGuid, index, ref interfaceData); index++)
            {
                SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
                var buffer = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA: 8 on x64, 6 (4+2) on x86.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, buffer, requiredSize, out _, IntPtr.Zero))
                    {
                        var path = Marshal.PtrToStringUni(buffer + 4);
                        if (path is not null && UsbId.TryFromPnpInstanceId(path, out var usbId))
                        {
                            results.Add((path, usbId));
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return results;
    }

    public static WinUsbDevice Open(string devicePath, UsbId usbId, int timeoutMs = 5000)
    {
        var fileHandle = CreateFile(devicePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);
        if (fileHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile failed for {devicePath}");
        }

        if (!WinUsb_Initialize(fileHandle, out var winUsbHandle))
        {
            var error = Marshal.GetLastWin32Error();
            fileHandle.Dispose();
            throw new Win32Exception(error, "WinUsb_Initialize failed");
        }

        try
        {
            var (inPipe, outPipe) = DiscoverPipes(winUsbHandle);
            var timeout = (uint)timeoutMs;
            WinUsb_SetPipePolicy(winUsbHandle, inPipe, PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref timeout);
            WinUsb_SetPipePolicy(winUsbHandle, outPipe, PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref timeout);

            // clear any halt/stall left by a previous session or a device replug — a stalled
            // OUT pipe makes every WritePipe fail with 0 bytes transferred (best effort)
            _ = WinUsb_ResetPipe(winUsbHandle, inPipe);
            _ = WinUsb_ResetPipe(winUsbHandle, outPipe);
            return new WinUsbDevice(devicePath, usbId, fileHandle, winUsbHandle, inPipe, outPipe);
        }
        catch
        {
            WinUsb_Free(winUsbHandle);
            fileHandle.Dispose();
            throw;
        }
    }

    private static (byte InPipe, byte OutPipe) DiscoverPipes(IntPtr winUsbHandle)
    {
        if (!WinUsb_QueryInterfaceSettings(winUsbHandle, 0, out var interfaceDescriptor))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_QueryInterfaceSettings failed");
        }

        byte? inPipe = null, outPipe = null;
        for (byte i = 0; i < interfaceDescriptor.bNumEndpoints; i++)
        {
            if (!WinUsb_QueryPipe(winUsbHandle, 0, i, out var pipeInfo))
            {
                continue;
            }

            if ((pipeInfo.PipeId & 0x80) != 0)
            {
                inPipe ??= pipeInfo.PipeId;
            }
            else
            {
                outPipe ??= pipeInfo.PipeId;
            }
        }

        if (inPipe is null || outPipe is null)
        {
            throw new InvalidOperationException("Device does not expose both an IN and an OUT pipe.");
        }

        return (inPipe.Value, outPipe.Value);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var buffer = data.ToArray();
        if (!WinUsb_WritePipe(_winUsbHandle, OutPipe, buffer, (uint)buffer.Length, out var transferred, IntPtr.Zero)
            || transferred != buffer.Length)
        {
            var error = Marshal.GetLastWin32Error();

            // a stalled pipe rejects every transfer until reset — clear it so the NEXT
            // attempt can succeed, then surface this failure to the caller
            _ = WinUsb_ResetPipe(_winUsbHandle, OutPipe);
            throw new Win32Exception(error, $"WinUsb_WritePipe failed (error {error}, {transferred}/{buffer.Length} bytes)");
        }
    }

    /// <summary>Reads one transfer into <paramref name="buffer"/>; returns bytes read, or -1 on timeout.</summary>
    public int Read(byte[] buffer, int timeoutMs)
    {
        var timeout = (uint)timeoutMs;
        WinUsb_SetPipePolicy(_winUsbHandle, InPipe, PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref timeout);

        if (WinUsb_ReadPipe(_winUsbHandle, InPipe, buffer, (uint)buffer.Length, out var transferred, IntPtr.Zero))
        {
            return (int)transferred;
        }

        var error = Marshal.GetLastWin32Error();
        if (error is ERROR_SEM_TIMEOUT or ERROR_TIMEOUT)
        {
            return -1;
        }

        throw new Win32Exception(error, "WinUsb_ReadPipe failed");
    }

    /// <summary>Discard any stale queued input.</summary>
    public void FlushInput()
    {
        var scratch = new byte[512];
        while (Read(scratch, 5) > 0)
        {
        }
    }

    public void Dispose()
    {
        WinUsb_Free(_winUsbHandle);
        _fileHandle.Dispose();
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint DIGCF_PRESENT = 0x2;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint PIPE_TRANSFER_TIMEOUT = 0x03;
    private const int ERROR_SEM_TIMEOUT = 121;
    private const int ERROR_TIMEOUT = 1460;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINUSB_PIPE_INFORMATION
    {
        public uint PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryInterfaceSettings(IntPtr interfaceHandle, byte alternateSettingNumber,
        out USB_INTERFACE_DESCRIPTOR interfaceDescriptor);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryPipe(IntPtr interfaceHandle, byte alternateInterfaceNumber,
        byte pipeIndex, out WINUSB_PIPE_INFORMATION pipeInformation);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr interfaceHandle, byte pipeId, uint policyType,
        uint valueLength, ref uint value);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ResetPipe(IntPtr interfaceHandle, byte pipeId);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(IntPtr interfaceHandle, byte pipeId, byte[] buffer,
        uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(IntPtr interfaceHandle, byte pipeId, byte[] buffer,
        uint bufferLength, out uint lengthTransferred, IntPtr overlapped);
}
