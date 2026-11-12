using DeviceMaster.Devices.AsusAura;
using DeviceMaster.Devices.EneRgb;
using DeviceMaster.Platform.Linux;

namespace DeviceMaster.Core.Tests;

public class EneDramScanTests
{
    /// <summary>A bus of simulated chips: ENE controllers (register-pointer model) and foreign devices.</summary>
    private sealed class SimBus : ISmBus
    {
        private sealed class EneChip
        {
            public ushort Pointer;
            public readonly Dictionary<ushort, byte> Registers = [];
        }

        private readonly Dictionary<byte, EneChip> _ene = [];
        private readonly HashSet<byte> _foreign = [];
        public readonly List<string> Writes = [];

        public string Name => "sim";

        public void AddEne(byte address, string version = "AUDA0-E6K5-0101", byte ledCount = 8)
        {
            var chip = new EneChip();
            for (var i = 0; i < version.Length; i++)
            {
                chip.Registers[(ushort)(0x1000 + i)] = (byte)version[i];
            }

            chip.Registers[0x1C02] = ledCount;
            _ene[address] = chip;
        }

        public void AddForeign(byte address) => _foreign.Add(address);

        public int ReadByte(byte address) => _ene.ContainsKey(address) || _foreign.Contains(address) ? 0 : -1;

        public int ReadByteData(byte address, byte command)
        {
            if (_foreign.Contains(address))
            {
                return 0x5A; // answers, but is not ENE
            }

            if (!_ene.TryGetValue(address, out var chip))
            {
                return -1;
            }

            if (command is >= 0xA0 and <= 0xAF)
            {
                return command - 0xA0;
            }

            return command == 0x81 ? chip.Registers.GetValueOrDefault(chip.Pointer) : 0;
        }

        public int WriteWordData(byte address, byte command, ushort value)
        {
            Writes.Add($"{address:X2}");
            if (!_ene.TryGetValue(address, out var chip))
            {
                return -1;
            }

            chip.Pointer = (ushort)((value << 8) | (value >> 8)); // pointer arrives byte-swapped
            return 0;
        }

        public int WriteByteData(byte address, byte command, byte value)
        {
            Writes.Add($"{address:X2}");
            if (!_ene.TryGetValue(address, out var chip))
            {
                return -1;
            }

            chip.Registers[chip.Pointer] = value;
            if (chip.Pointer == EneRgbDevice.RegI2cAddress)
            {
                _ene.Remove(address); // the chip now answers at its new address
                _ene[(byte)(value >> 1)] = chip;
            }

            return 0;
        }

        public int WriteBlockData(byte address, byte command, byte[] data) => -1;
    }

    [Fact]
    public void FindsEneControllers_AndNeverWritesForeignDevices()
    {
        var bus = new SimBus();
        bus.AddEne(0x71);
        bus.AddEne(0x73, ledCount: 10);
        bus.AddForeign(0x70);
        bus.AddForeign(0x4F);

        var devices = EneDramScan.FindControllers(bus, pause: () => { });

        Assert.Equal([0x71, 0x73], devices.Select(d => d.Address));
        Assert.Equal(10, devices[1].LedCount);
        Assert.DoesNotContain("70", bus.Writes);
        Assert.DoesNotContain("4F", bus.Writes);
    }

    [Fact]
    public void RemapsUnconfiguredController_OffSevenSeven()
    {
        var bus = new SimBus();
        bus.AddEne(0x77);

        var devices = EneDramScan.FindControllers(bus, pause: () => { });

        var device = Assert.Single(devices);
        Assert.Equal(0x70, device.Address); // first free candidate
    }

    [Fact]
    public void LeavesForeignDeviceAtSevenSevenAlone()
    {
        var bus = new SimBus();
        bus.AddForeign(0x77);

        var devices = EneDramScan.FindControllers(bus, pause: () => { });

        Assert.Empty(devices);
        Assert.Empty(bus.Writes);
    }

    [Theory]
    [InlineData("SMBus PIIX4 adapter port 0 at 0b00", true)]
    [InlineData("SMBus PIIX4 adapter port 2 at 0b00", true)]
    [InlineData("SMBus I801 adapter at efa0", true)]
    [InlineData("NVIDIA i2c adapter 1 at 1:00.0", false)]
    [InlineData("AMDGPU DM i2c hw bus 0", false)]
    [InlineData("Synopsys DesignWare I2C adapter", false)]
    public void ChipsetSmbus_IsRecognizedByAdapterName(string name, bool expected) =>
        Assert.Equal(expected, LinuxSmBus.IsChipsetSmbus(name));
}

public class AuraZoneMappingTests
{
    [Fact]
    public void Aula3Firmware_HeadersTakeEffectChannelsFromZero()
    {
        var config = new byte[60];
        config[0x1B] = 4;
        config[0x02] = 3;

        var zones = AuraUsbProtocol.ParseZones(config, "AULA3-AR32-0218");

        Assert.Equal(3, zones[0].EffectChannel); // onboard after the headers
        Assert.Equal([0, 1, 2], zones.Skip(1).Select(z => (int)z.EffectChannel));
    }

    [Fact]
    public void OtherFirmware_OnboardTakesEffectChannelZero()
    {
        var config = new byte[60];
        config[0x1B] = 4;
        config[0x02] = 2;

        var zones = AuraUsbProtocol.ParseZones(config, "AULA4-AR42-0101");

        Assert.Equal([0, 1, 2], zones.Select(z => (int)z.EffectChannel));
    }

    [Fact]
    public void EffectColor_RejectsSlotsBeyondTheSixteenBitMask()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AuraUsbProtocol.BuildEffectColor(15, 2, 1, 2, 3));
        Assert.Equal(65, AuraUsbProtocol.BuildEffectColor(15, 1, 1, 2, 3).Length);
    }

    [Fact]
    public void ConfigTableDescription_NamesTheZoneCounts()
    {
        var config = new byte[60];
        config[0x1B] = 8;
        config[0x1D] = 2;
        config[0x02] = 3;

        Assert.StartsWith("onboard(1B)=8 rgb12v(1D)=2 argb(02)=3 | ", AuraUsbProtocol.DescribeConfigTable(config));
    }
}
