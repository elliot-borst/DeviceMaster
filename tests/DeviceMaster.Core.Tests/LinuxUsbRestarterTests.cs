using DeviceMaster.Core.Discovery;
using Xunit;

namespace DeviceMaster.Core.Tests;

/// <summary>
/// Path-resolution half of the Linux sysfs replug (#211 follow-up), pinned to the REAL sysfs
/// topology of the Unraid deployment: hub B lives at usb3/3-1, the XD5 LCD child at
/// 3-1:1.0/0003:1B1C:0C43.0001/hidraw/hidraw0, and root hubs ("usb1") must never resolve.
/// </summary>
public class LinuxUsbRestarterTests
{
    [Theory]
    [InlineData("/sys/devices/pci0000:00/0000:00:02.1/0000:02:00.0/0000:03:0c.0/0000:12:00.0/usb3/3-1/3-1:1.0/0003:1B1C:0C43.0001/hidraw/hidraw0", "3-1")]
    [InlineData("/sys/devices/platform/usb2/2-4/2-4:1.2/hidraw/hidraw7", "2-4")]
    [InlineData("/sys/devices/pci0000:00/usb3/3-11/3-11:1.0/hidraw/hidraw3", "3-11")]
    [InlineData("/sys/bus/usb/devices/3-1", "3-1")]
    public void ExtractsBusPortFromAnyPathThroughTheDevice(string path, string expected)
    {
        Assert.Equal(expected, LinuxUsbRestarter.TryExtractUsbDeviceName(path));
    }

    [Theory]
    [InlineData("/sys/devices/pci0000:00/0000:00:14.0/usb1")]            // root hub only
    [InlineData("/dev/hidraw0")]                                          // devtmpfs name carries no topology
    [InlineData("/sys/class/hidraw/hidraw0/device")]                      // symlink path, unresolved
    [InlineData("/sys/devices/virtual/misc/hidraw")]
    public void RejectsPathsWithoutABusPortDevice(string path)
    {
        Assert.Null(LinuxUsbRestarter.TryExtractUsbDeviceName(path));
    }

    [Theory]
    [InlineData("usb1")]
    [InlineData("usb3")]
    [InlineData("3-1:1.0")]         // interface, not the device node itself
    [InlineData("0003:1B1C:0C43.0001")]
    [InlineData("3")]
    public void ReauthorizeRefusesNonDeviceDirectories(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            LinuxUsbRestarter.Reauthorize(name));
    }

    [Fact]
    public void DevHidrawPathResolvesToNullWhenSysfsAbsent()
    {
        // /dev/hidrawNNN with no matching /sys/class/hidraw entry must fail closed, never guess
        Assert.Null(LinuxUsbRestarter.ResolveUsbDeviceDir("/dev/hidraw9999"));
    }
}
