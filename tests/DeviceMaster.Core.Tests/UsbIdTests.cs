using DeviceMaster.Core.Devices;

namespace DeviceMaster.Core.Tests;

public class UsbIdTests
{
    [Fact]
    public void ToString_FormatsUpperHex() =>
        Assert.Equal("1B1C:0C3F", new UsbId(0x1B1C, 0x0C3F).ToString());

    [Theory]
    [InlineData(@"USB\VID_1B1C&PID_0C3F&MI_00\C&26FBC76C&0&0000", 0x1B1C, 0x0C3F)]
    [InlineData(@"USB\VID_1A86&PID_CA88\CT88INCH", 0x1A86, 0xCA88)]
    [InlineData(@"USB\VID_1CBE&PID_0005\0B913822D5160A66", 0x1CBE, 0x0005)]
    [InlineData(@"HID\VID_0B05&PID_19AF&MI_02\A&D29D7F0&0&0000", 0x0B05, 0x19AF)]
    public void TryFromPnpInstanceId_ParsesRealInstanceIds(string instanceId, int vid, int pid)
    {
        Assert.True(UsbId.TryFromPnpInstanceId(instanceId, out var id));
        Assert.Equal((ushort)vid, id.Vid);
        Assert.Equal((ushort)pid, id.Pid);
    }

    [Fact]
    public void TryFromPnpInstanceId_RejectsNonUsbIds() =>
        Assert.False(UsbId.TryFromPnpInstanceId(@"ACPI\PNP0501\0", out _));

    [Fact]
    public void Parse_RoundTripsThroughToString()
    {
        var id = new UsbId(0x1B1C, 0x0C3F);
        Assert.Equal(id, UsbId.Parse(id.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-id")]
    [InlineData("1B1C")]
    [InlineData("1B1C:XYZW")]
    public void TryParse_RejectsGarbage(string? text) =>
        Assert.False(UsbId.TryParse(text, out _));
}
