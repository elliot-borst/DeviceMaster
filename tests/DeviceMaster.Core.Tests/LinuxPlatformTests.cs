using System.IO;
using DeviceMaster.Platform.Linux;
using DeviceMaster.Sensors.Linux;
using Xunit;

namespace DeviceMaster.Core.Tests;

/// <summary>Pure-logic tests for the Linux platform/sensor layer (no kernel access in tests).</summary>
public class LinuxPlatformTests : IDisposable
{
    private readonly string _fixtureRoot = Path.Combine(Path.GetTempPath(), "dm-linux-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_fixtureRoot, recursive: true);
        }
        catch
        {
        }
    }

    private string MakeDir(string relative)
    {
        var path = Path.Combine(_fixtureRoot, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private void Write(string relative, string content) =>
        File.WriteAllText(Path.Combine(_fixtureRoot, relative), content);

    // ---- nvidia-smi line parsing (pure) ----

    [Fact]
    public void NvmSmi_ParseLine_FullRow()
    {
        var reading = NvidiaSmi.ParseLine("NVIDIA GeForce RTX 4090, 52, 1, 245.10, 2048, 24564");
        Assert.NotNull(reading);
        Assert.Equal("NVIDIA GeForce RTX 4090", reading!.Name);
        Assert.Equal(52.0, reading.TemperatureC);
        Assert.Equal(1.0, reading.UtilizationPercent);
        Assert.InRange(reading.PowerW!.Value, 245.09, 245.11);
        Assert.Equal(2048.0, reading.MemoryUsedMb);
        Assert.Equal(24564.0, reading.MemoryTotalMb);
    }

    [Fact]
    public void NvmSmi_ParseLine_ShortRow_StillReadsTemp()
    {
        var reading = NvidiaSmi.ParseLine("NVIDIA GeForce RTX 4090, 52");
        Assert.NotNull(reading);
        Assert.Equal(52.0, reading!.TemperatureC);
        Assert.Null(reading.UtilizationPercent);
    }

    [Fact]
    public void NvmSmi_ParseLine_Junk_ReturnsNull()
    {
        Assert.Null(NvidiaSmi.ParseLine(""));
        Assert.Null(NvidiaSmi.ParseLine("NVIDIA-SMI has failed"));
    }

    // ---- hwmon sysfs scan (fixture tree) ----

    [Fact]
    public void Hwmon_ReadTemperatures_WalksFixtureTree()
    {
        MakeDir("class/hwmon/hwmon4");
        Write("class/hwmon/hwmon4/name", "k10temp");
        Write("class/hwmon/hwmon4/temp1_input", "58600");
        Write("class/hwmon/hwmon4/temp1_label", "Tctl");
        Write("class/hwmon/hwmon4/temp3_input", "61000");
        Write("class/hwmon/hwmon4/temp3_label", "Tdie");
        MakeDir("class/hwmon/hwmon0");
        Write("class/hwmon/hwmon0/name", "coretemp");
        Write("class/hwmon/hwmon0/temp1_input", "55000");

        var temps = Hwmon.ReadTemperatures(_fixtureRoot);
        Assert.Contains(temps, t => t.Sensor == "k10temp" && t.Key == "Tctl" && Math.Abs(t.ValueC - 58.6) < 0.001);
        Assert.Contains(temps, t => t.Sensor == "k10temp" && t.Key == "Tdie" && Math.Abs(t.ValueC - 61.0) < 0.001);
        Assert.Contains(temps, t => t.Sensor == "coretemp" && Math.Abs(t.ValueC - 55.0) < 0.001);
    }

    [Fact]
    public void Hwmon_CpuTemperature_PrefersK10tempTctl()
    {
        MakeDir("class/hwmon/hwmon0");
        Write("class/hwmon/hwmon0/name", "coretemp");
        Write("class/hwmon/hwmon0/temp1_input", "55000");
        MakeDir("class/hwmon/hwmon4");
        Write("class/hwmon/hwmon4/name", "k10temp");
        Write("class/hwmon/hwmon4/temp1_input", "58600");
        Write("class/hwmon/hwmon4/temp1_label", "Tctl");
        Write("class/hwmon/hwmon4/temp3_input", "61000");
        Write("class/hwmon/hwmon4/temp3_label", "Tdie");

        var cpu = Hwmon.CpuTemperatureC(Hwmon.ReadTemperatures(_fixtureRoot));
        Assert.NotNull(cpu);
        Assert.InRange(cpu!.Value, 58.59, 58.61); // Tctl wins over the earlier coretemp entry
    }

    [Fact]
    public void Hwmon_CpuTemperature_EmptyRoot_ReturnsNull()
    {
        Assert.Null(Hwmon.CpuTemperatureC(Hwmon.ReadTemperatures(_fixtureRoot)));
    }

    [Fact]
    public void Hwmon_CpuTemperature_SupermicroBoard_UsesNct67087CpuChannel()
    {
        // the Unraid server: Supermicro X11 board, nct67087 — no k10temp/coretemp
        MakeDir("class/hwmon/hwmon0");
        Write("class/hwmon/hwmon0/name", "nct67087");
        Write("class/hwmon/hwmon0/temp1_input", "58000");
        Write("class/hwmon/hwmon0/temp1_label", "CPU");
        Write("class/hwmon/hwmon0/temp2_input", "44000");
        Write("class/hwmon/hwmon0/temp2_label", "System");

        var cpu = Hwmon.CpuTemperatureC(Hwmon.ReadTemperatures(_fixtureRoot));
        Assert.InRange(cpu!.Value, 57.99, 58.01);
    }

    // ---- GPU i2c adapter location (fixture sysfs) ----

    [Fact]
    public void GpuI2cLocator_FindAll_ReadsNvidiaAdaptersOnly()
    {
        MakeDir("bus/i2c/devices/i2c-10");
        Write("bus/i2c/devices/i2c-10/name", "NVIDIA i2c adapter 1 at 1:00.0");
        MakeDir("bus/i2c/devices/i2c-0");
        Write("bus/i2c/devices/i2c-0/name", "i2c-0");

        var matches = GpuI2cLocator.FindAll(_fixtureRoot);
        Assert.Single(matches);
        Assert.Equal("/dev/i2c-10", matches[0].DevicePath);
        Assert.Equal("1:00.0", matches[0].PciAddress);
    }

    [Fact]
    public void GpuI2cLocator_Find_PciMatch_IsWidthInsensitive()
    {
        MakeDir("bus/i2c/devices/i2c-10");
        Write("bus/i2c/devices/i2c-10/name", "NVIDIA i2c adapter 1 at 1:00.0");
        MakeDir("bus/i2c/devices/i2c-2");
        Write("bus/i2c/devices/i2c-2/name", "NVIDIA i2c adapter 1 at 15:00.0");

        var match = GpuI2cLocator.Find("01:00.0", _fixtureRoot);
        Assert.NotNull(match);
        Assert.Equal("/dev/i2c-10", match!.DevicePath);

        Assert.Null(GpuI2cLocator.Find("99:00.0", _fixtureRoot));
    }

    [Fact]
    public void GpuI2cLocator_SamePci_NumericHexCompare()
    {
        Assert.True(GpuI2cLocator.SamePci("1:00.0", "01:00.0"));
        Assert.True(GpuI2cLocator.SamePci("A:01.1", "0A:1.1"));
        Assert.False(GpuI2cLocator.SamePci("1:00.0", "1:01.0"));
        Assert.False(GpuI2cLocator.SamePci("garbage", "1:00.0"));
    }
}
