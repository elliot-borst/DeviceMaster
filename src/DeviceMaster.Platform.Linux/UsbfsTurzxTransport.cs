using System.Runtime.InteropServices;
using DeviceMaster.Core.Devices;
using DeviceMaster.Devices.Turzx;

namespace DeviceMaster.Platform.Linux;

/// <summary>
/// Bulk I/O to a Turzx 8.8" V1.x panel through Linux usbfs (<c>/dev/bus/usb/BBB/DDD</c>), with no
/// libusb (#102). Claims interface 0 (no kernel driver binds to it) and moves data with
/// <c>USBDEVFS_BULK</c>. Needs write access to the device node (root, or a udev rule; a privileged
/// container with <c>/dev</c> mounted).
/// </summary>
public sealed class UsbfsTurzxTransport : ITurzxUsbTransport
{
    private const int O_RDWR = 0x0002;
    private const int ETIMEDOUT = 110;
    private const uint UsbdevfsBulk = 0xC0185502;            // _IOWR('U', 2, struct usbdevfs_bulktransfer) on 64-bit
    private const uint UsbdevfsClaimInterface = 0x8004550F;  // _IOR('U', 15, unsigned int)
    private const uint UsbdevfsReleaseInterface = 0x80045510; // _IOR('U', 16, unsigned int)

    private int _fd;

    private UsbfsTurzxTransport(int fd, string name)
    {
        _fd = fd;
        Name = name;
    }

    public string Name { get; }

    /// <summary>
    /// The usbfs node of a connected panel, or null. Matches VID/PID <b>and</b> the product string
    /// ("TURZX1.0"): vendor 1CBE is shared with other devices, and only a positively identified,
    /// write-allowed panel is ever returned. Pure sysfs reads; <paramref name="sysRoot"/> is for tests.
    /// </summary>
    public static string? Find(string sysRoot = "/sys", string devRoot = "/dev")
    {
        var id = new UsbId(TurzxUsbProtocol.Vid, TurzxUsbProtocol.Pid);
        var devices = Path.Combine(sysRoot, "bus", "usb", "devices");
        if (!KnownDeviceRegistry.IsWriteAllowed(id) || !Directory.Exists(devices))
        {
            return null;
        }

        foreach (var dir in Directory.GetDirectories(devices).Order(StringComparer.Ordinal))
        {
            if (Read(dir, "idVendor") is not { } vid || Read(dir, "idProduct") is not { } pid
                || Convert.ToUInt16(vid, 16) != id.Vid || Convert.ToUInt16(pid, 16) != id.Pid
                || Read(dir, "product") != TurzxUsbProtocol.ProductString
                || !int.TryParse(Read(dir, "busnum"), out var bus) || !int.TryParse(Read(dir, "devnum"), out var dev))
            {
                continue;
            }

            return Path.Combine(devRoot, "bus", "usb", bus.ToString("000"), dev.ToString("000"));
        }

        return null;
    }

    /// <summary>Opens the node and claims interface 0; throws when either fails.</summary>
    public static UsbfsTurzxTransport Open(string node)
    {
        var fd = OpenFile(node, O_RDWR);
        if (fd < 0)
        {
            throw new IOException($"cannot open {node} (errno {Marshal.GetLastPInvokeError()}): needs root or a udev rule");
        }

        uint iface = 0;
        if (IoctlUint(fd, UsbdevfsClaimInterface, ref iface) < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            CloseFile(fd);
            throw new IOException($"cannot claim the Turzx panel's interface on {node} (errno {errno}; another program may hold it)");
        }

        return new UsbfsTurzxTransport(fd, node);
    }

    public void Write(ReadOnlySpan<byte> data, int timeoutMs)
    {
        var sent = Bulk(TurzxUsbProtocol.EndpointOut, data, timeoutMs, out var errno);
        if (sent != data.Length)
        {
            throw new IOException(sent < 0
                ? $"Turzx bulk write failed (errno {errno})"
                : $"Turzx bulk write was short ({sent} of {data.Length} bytes)");
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        var read = Bulk(TurzxUsbProtocol.EndpointIn, buffer, timeoutMs, out var errno);
        if (read >= 0)
        {
            return read;
        }

        return errno == ETIMEDOUT ? 0 : throw new IOException($"Turzx bulk read failed (errno {errno})");
    }

    private unsafe int Bulk(byte endpoint, ReadOnlySpan<byte> data, int timeoutMs, out int errno)
    {
        fixed (byte* pointer = data)
        {
            var transfer = new BulkTransfer
            {
                Endpoint = endpoint,
                Length = (uint)data.Length,
                TimeoutMs = (uint)Math.Max(1, timeoutMs),
                Data = (IntPtr)pointer,
            };
            var result = IoctlBulk(_fd, UsbdevfsBulk, ref transfer);
            errno = result < 0 ? Marshal.GetLastPInvokeError() : 0;
            return result;
        }
    }

    public void Dispose()
    {
        if (_fd < 0)
        {
            return;
        }

        uint iface = 0;
        IoctlUint(_fd, UsbdevfsReleaseInterface, ref iface);
        CloseFile(_fd);
        _fd = -1;
    }

    private static string? Read(string dir, string file)
    {
        try
        {
            var path = Path.Combine(dir, file);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>struct usbdevfs_bulktransfer (64-bit): three u32s, padding, then the data pointer.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct BulkTransfer
    {
        [FieldOffset(0)] public uint Endpoint;
        [FieldOffset(4)] public uint Length;
        [FieldOffset(8)] public uint TimeoutMs;
        [FieldOffset(16)] public IntPtr Data;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFile(string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseFile(int fd);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlBulk(int fd, uint request, ref BulkTransfer transfer);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlUint(int fd, uint request, ref uint value);
}
