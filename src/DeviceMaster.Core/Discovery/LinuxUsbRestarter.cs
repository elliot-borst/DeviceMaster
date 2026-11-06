// Linux counterpart of UsbDeviceRestarter (ServerStack #211 follow-up): the headless container
// runs on Linux with --privileged -v /dev:/dev, so the pump hub's software replug is a sysfs
// authorized toggle — the kernel equivalent of unplugging and reseating the device.

using System.Text.RegularExpressions;

namespace DeviceMaster.Core.Discovery;

/// <summary>
/// Restarts a USB device on Linux by toggling its <c>authorized</c> attribute
/// (0, settle, 1) under /sys/bus/usb/devices. Requires write access to sysfs (privileged
/// container or root). Root hubs (names without a bus-port dash, e.g. "usb1") are never
/// touched — deauthorizing one would drop every device on that controller.
/// </summary>
public static partial class LinuxUsbRestarter
{
    private const string UsbDevicesRoot = "/sys/bus/usb/devices";

    /// <summary>
    /// Pure string part: the bus-port device directory name (e.g. "3-1") from any sysfs path
    /// that runs through one — hidraw device dirs ("…/usb3/3-1/3-1:1.0/0003:1B1C:0C43.0001/hidraw/hidraw0")
    /// included. Null when no component qualifies (or only a root hub like "usb1" appears).
    /// </summary>
    public static string? TryExtractUsbDeviceName(string sysfsPath)
    {
        var parts = sysfsPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // components that ARE usb devices look like "<bus>-<port>[.interface]": 3-1, 3-1:1.0.
        // Walk right-to-left: the deepest usb component's bus-port root is the device node; the
        // interface suffix (:1.0) and vendor-class child (0003:…) both belong to the same node.
        foreach (var part in parts.Reverse())
        {
            var match = UsbDeviceNameRegex().Match(part);
            if (match.Success)
            {
                return match.Groups["node"].Value;
            }
        }

        return null; // nothing usb-shaped; note "usbN" root hubs never match (no dash)
    }

    [GeneratedRegex(@"^(?<node>\d+-\d+)(:\d+\.\d+|(:|\.)\d+(\.\d+)*)*$")]
    private static partial Regex UsbDeviceNameRegex();

    [GeneratedRegex(@"^\d+-\d+$")]
    private static partial Regex FullNodeNameRegex();

    /// <summary>
    /// Resolves a HidSharp device path (Linux forms: "/dev/hidrawN", or a "/sys/…" path ending in
    /// hidraw/hidrawN) to the USB device directory under <value>UsbDevicesRoot</value>. Null when
    /// it cannot be resolved or would resolve to a root hub.
    /// </summary>
    public static string? ResolveUsbDeviceDir(string hidDevicePath)
    {
        string? sysfsPath = null;
        if (hidDevicePath.StartsWith("/sys/", StringComparison.Ordinal))
        {
            sysfsPath = hidDevicePath;
        }
        else if (Regex.IsMatch(hidDevicePath, @"^/dev/hidraw\d+$"))
        {
            // /sys/class/hidraw/hidrawN/device is a symlink to the HID device dir under the USB tree
            var link = Path.Combine("/sys/class/hidraw", Path.GetFileName(hidDevicePath), "device");
            try
            {
                sysfsPath = File.ResolveLinkTarget(link, true)?.FullName;
            }
            catch
            {
                return null;
            }
        }

        var name = sysfsPath is null ? null : TryExtractUsbDeviceName(sysfsPath);
        return name is not null && File.Exists(Path.Combine(UsbDevicesRoot, name, "authorized"))
            ? Path.Combine(UsbDevicesRoot, name)
            : null;
    }

    /// <summary>
    /// Deauthorize + reauthorize one USB device directory: the software equivalent of replugging
    /// it. Throws if the authorized files are not writable (container not privileged).
    /// </summary>
    public static void Reauthorize(string usbDeviceDir, int settleMs = 2000)
    {
        if (!FullNodeNameRegex().IsMatch(Path.GetFileName(usbDeviceDir.TrimEnd('/'))))
        {
            throw new ArgumentException($"'{usbDeviceDir}' is not a usb bus-port device directory", nameof(usbDeviceDir));
        }

        var authorized = Path.Combine(usbDeviceDir, "authorized");
        File.WriteAllText(authorized, "0");
        Thread.Sleep(settleMs);
        try
        {
            File.WriteAllText(authorized, "1");
        }
        catch
        {
            // never leave a device deauthorized on the way out
            try { File.WriteAllText(authorized, "1"); } catch { /* nothing else we can do */ }
            throw;
        }
    }

    /// <summary>
    /// One-call helper for callers holding only a HidSharp path: resolve + replug. Returns the
    /// device directory name on success, null when unresolvable (caller logs the manual fallback).
    /// </summary>
    public static string? ReplugFromHidPath(string hidDevicePath, Action<string>? log = null)
    {
        var dir = ResolveUsbDeviceDir(hidDevicePath);
        if (dir is null)
        {
            return null;
        }

        var name = Path.GetFileName(dir);
        log?.Invoke($"restarting USB device node {name} (sysfs authorized toggle — the software equivalent of replugging it)");
        try
        {
            Reauthorize(dir);
        }
        catch (Exception ex)
        {
            log?.Invoke($"sysfs authorized toggle for {name} failed: {ex.Message} (needs a privileged container)");
            return null;
        }

        return name;
    }
}
