using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DeviceMaster.Core.Devices;
using Microsoft.Win32.SafeHandles;

namespace DeviceMaster.Devices.Turzx;

/// <summary>
/// Bulk I/O to a Turzx 8.8" V1.x panel on Windows through WinUSB (#102). The panel is bound to
/// Microsoft's winusb.inf with device interface GUID {88BAE032-5A81-49F0-BC3D-A4FF138216D6}; this
/// finds that interface, checks the device's product string is "TURZX1.0" (VID 1CBE is shared), and
/// moves data with WinUsb_WritePipe / WinUsb_ReadPipe. Exclusive: one program at a time.
/// </summary>
public sealed class WinUsbTurzxTransport : ITurzxUsbTransport
{
    /// <summary>The interface GUID Windows assigns the panel under winusb.inf.</summary>
    public static readonly Guid InterfaceGuid = new("88BAE032-5A81-49F0-BC3D-A4FF138216D6");

    private const int ErrorSemTimeout = 121;
    private const uint PipeTransferTimeout = 0x03;

    private readonly SafeFileHandle _file;
    private IntPtr _winUsb;
    private uint _writeTimeout, _readTimeout;

    private WinUsbTurzxTransport(SafeFileHandle file, IntPtr winUsb, string name)
    {
        _file = file;
        _winUsb = winUsb;
        Name = name;
    }

    public string Name { get; }

    /// <summary>Device paths of present panels on that interface (VID/PID in the path; product checked on open).</summary>
    public static IReadOnlyList<string> Find()
    {
        if (!OperatingSystem.IsWindows() || !KnownDeviceRegistry.IsWriteAllowed(new UsbId(TurzxUsbProtocol.Vid, TurzxUsbProtocol.Pid)))
        {
            return [];
        }

        var paths = new List<string>();
        var guid = InterfaceGuid;
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1))
        {
            return paths;
        }

        try
        {
            var data = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint index = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data); index++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)
                        && Marshal.PtrToStringUni(detail + 4) is { } path
                        && path.Contains($"vid_{TurzxUsbProtocol.Vid:x4}&pid_{TurzxUsbProtocol.Pid:x4}", StringComparison.OrdinalIgnoreCase))
                    {
                        paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return paths;
    }

    /// <summary>
    /// Opens a panel and confirms its product string is "TURZX1.0"; throws when it can't be opened
    /// (another program holds it) or it isn't the panel.
    /// </summary>
    public static WinUsbTurzxTransport Open(string devicePath)
    {
        // WinUSB only takes a handle opened for overlapped I/O; its synchronous calls still work on it
        var file = CreateFile(devicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (file.IsInvalid)
        {
            throw new IOException($"cannot open the Turzx panel ({new Win32Exception(Marshal.GetLastWin32Error()).Message}); another program may hold it");
        }

        if (!WinUsb_Initialize(file, out var winUsb))
        {
            var error = Marshal.GetLastWin32Error();
            file.Dispose();
            throw new IOException($"WinUSB could not take the Turzx panel ({new Win32Exception(error).Message})");
        }

        var transport = new WinUsbTurzxTransport(file, winUsb, devicePath);
        try
        {
            var product = transport.ProductString();
            if (product != TurzxUsbProtocol.ProductString)
            {
                throw new IOException($"device at {devicePath} is \"{product}\", not a Turzx panel: not opened");
            }

            return transport;
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    public void Write(ReadOnlySpan<byte> data, int timeoutMs)
    {
        SetTimeout(TurzxUsbProtocol.EndpointOut, ref _writeTimeout, timeoutMs);
        unsafe
        {
            fixed (byte* pointer = data)
            {
                if (!WinUsb_WritePipe(_winUsb, TurzxUsbProtocol.EndpointOut, pointer, (uint)data.Length, out var sent, IntPtr.Zero))
                {
                    throw new IOException($"Turzx bulk write failed ({new Win32Exception(Marshal.GetLastWin32Error()).Message})");
                }

                if (sent != data.Length)
                {
                    throw new IOException($"Turzx bulk write was short ({sent} of {data.Length} bytes)");
                }
            }
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        SetTimeout(TurzxUsbProtocol.EndpointIn, ref _readTimeout, timeoutMs);
        unsafe
        {
            fixed (byte* pointer = buffer)
            {
                if (WinUsb_ReadPipe(_winUsb, TurzxUsbProtocol.EndpointIn, pointer, (uint)buffer.Length, out var read, IntPtr.Zero))
                {
                    return (int)read;
                }

                var error = Marshal.GetLastWin32Error();
                return error == ErrorSemTimeout ? 0 : throw new IOException($"Turzx bulk read failed ({new Win32Exception(error).Message})");
            }
        }
    }

    /// <summary>Sets a pipe's transfer timeout only when it changes (it is a per-pipe policy).</summary>
    private void SetTimeout(byte pipe, ref uint current, int timeoutMs)
    {
        var wanted = (uint)Math.Max(1, timeoutMs);
        if (wanted == current)
        {
            return;
        }

        if (!WinUsb_SetPipePolicy(_winUsb, pipe, PipeTransferTimeout, sizeof(uint), ref wanted))
        {
            throw new IOException($"WinUSB pipe timeout could not be set ({new Win32Exception(Marshal.GetLastWin32Error()).Message})");
        }

        current = wanted;
    }

    /// <summary>The device's product string (iProduct), read through WinUSB.</summary>
    private string? ProductString()
    {
        var device = new byte[18];
        if (!WinUsb_GetDescriptor(_winUsb, 1, 0, 0, device, (uint)device.Length, out var length) || length < 18 || device[15] == 0)
        {
            return null;
        }

        var text = new byte[255];
        if (!WinUsb_GetDescriptor(_winUsb, 3, device[15], 0x0409, text, (uint)text.Length, out length) || length < 2)
        {
            return null;
        }

        return Encoding.Unicode.GetString(text, 2, Math.Min(text[0], (int)length) - 2);
    }

    public void Dispose()
    {
        if (_winUsb != IntPtr.Zero)
        {
            WinUsb_Free(_winUsb);
            _winUsb = IntPtr.Zero;
        }

        _file.Dispose();
    }

    // ---- Win32 ----

    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x01;
    private const uint FileShareWrite = 0x02;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid guid, uint index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, uint size, out uint requiredSize, IntPtr deviceInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr handle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(IntPtr handle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern unsafe bool WinUsb_WritePipe(IntPtr handle, byte pipe, byte* buffer, uint length, out uint transferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern unsafe bool WinUsb_ReadPipe(IntPtr handle, byte pipe, byte* buffer, uint length, out uint transferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipe, uint policy, uint length, ref uint value);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_GetDescriptor(IntPtr handle, byte type, byte index, ushort language, byte[] buffer, uint length, out uint transferred);
}
