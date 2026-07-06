using DeviceMaster.Core.Devices;

namespace DeviceMaster.Core.Tests;

public class KnownDeviceRegistryTests
{
    [Fact]
    public void Identifies_CorsairLinkHub()
    {
        var device = KnownDeviceRegistry.Identify(new UsbId(0x1B1C, 0x0C3F));
        Assert.NotNull(device);
        Assert.Equal(DeviceKind.CorsairLinkHub, device.Kind);
        Assert.True(device.SupportPlanned);
    }

    [Fact]
    public void Identifies_TurzxScreen()
    {
        var device = KnownDeviceRegistry.Identify(new UsbId(0x1A86, 0xCA88));
        Assert.NotNull(device);
        Assert.Equal(DeviceKind.TurzxScreen, device.Kind);
        Assert.True(device.SupportPlanned);
    }

    [Theory]
    [InlineData(0x0416, 0x8040, DeviceKind.LianLiSlv3Controller)]
    [InlineData(0x0416, 0x8041, DeviceKind.LianLiSlv3Controller)]
    [InlineData(0x1CBE, 0x0005, DeviceKind.LianLiSlv3FanNode)]
    public void Identifies_LianLiSlv3Ecosystem(int vid, int pid, DeviceKind expected)
    {
        var device = KnownDeviceRegistry.Identify(new UsbId((ushort)vid, (ushort)pid));
        Assert.NotNull(device);
        Assert.Equal(expected, device.Kind);
        Assert.True(device.SupportPlanned);
    }

    [Fact]
    public void UnknownDevice_ReturnsNull() =>
        Assert.Null(KnownDeviceRegistry.Identify(new UsbId(0xDEAD, 0xBEEF)));

    [Fact]
    public void WriteGate_DeniesUnknownDevices() =>
        Assert.False(KnownDeviceRegistry.IsWriteAllowed(new UsbId(0xDEAD, 0xBEEF)));

    [Fact]
    public void WriteGate_DeniesRecognizedButOutOfScopeDevices() =>
        Assert.False(KnownDeviceRegistry.IsWriteAllowed(new UsbId(0x0B05, 0x19AF)));

    [Fact]
    public void WriteGate_AllowsIdentifiedTargets() =>
        Assert.True(KnownDeviceRegistry.IsWriteAllowed(new UsbId(0x1B1C, 0x0C3F)));
}
