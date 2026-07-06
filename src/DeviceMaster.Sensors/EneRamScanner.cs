using DeviceMaster.Devices.EneRgb;
using LibreHardwareMonitor.Hardware;
using RAMSPDToolkit.I2CSMBus;
using RAMSPDToolkit.SPD;

namespace DeviceMaster.Sensors;

/// <summary>One detected memory module (SPD identity + optional ENE RGB controller).</summary>
public sealed record RamStick(string BusName, byte SpdAddress, string PartNumber, string Manufacturer);

/// <summary>
/// Finds ENE RGB controllers on RGB DRAM (and the sticks' SPD identities) over the AMD FCH
/// SMBus, using RAMSPDToolkit's transactions on top of LibreHardwareMonitor's PawnIO driver.
/// Ported from OpenRGB's ENESMBusControllerDetect with one deliberate hardening: a device
/// answering at 0x77 is READ-fingerprinted as ENE before the remap registers are written —
/// the reference writes blindly to whatever sits at 0x77.
/// Requires administrator + PawnIO. All transactions hold the system SMBus mutex.
/// </summary>
public sealed class EneRamScanner : IDisposable
{
    /// <summary>Candidate ENE DRAM addresses, in the reference probe order.</summary>
    private static readonly byte[] CandidateAddresses =
    [
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77,
        0x4F, 0x66, 0x67, 0x39, 0x3A, 0x3B, 0x3C, 0x3D,
    ];

    private const byte UnconfiguredAddress = 0x77;
    private const ushort AmdVendor = 0x1022;

    /// <summary>Cross-process SMBus arbitration mutex shared with BIOS/ACPI and other tools.</summary>
    private static readonly Mutex SmbusMutex = new(initiallyOwned: false, "Global\\Access_SMBUS.HTP.Method");

    private readonly Computer _computer;
    private readonly Action<string>? _log;

    public EneRamScanner(Action<string>? log = null)
    {
        if (!LhmFanController.IsElevated)
        {
            throw new InvalidOperationException("RAM RGB needs administrator rights (SMBus via the PawnIO driver).");
        }

        _log = log;

        // Opening the Memory group registers LHM's PawnIO-backed driver with RAMSPDToolkit
        // and detects the SMBuses — that driver session is what our raw transactions ride on.
        _computer = new Computer { IsMemoryEnabled = true };
        _computer.Open();

        if (SMBusManager.RegisteredSMBuses.Count == 0)
        {
            SMBusManager.DetectSMBuses();
        }
    }

    private static IEnumerable<SMBusInterface> AmdBuses() =>
        SMBusManager.RegisteredSMBuses.Where(b => b.PCIVendor == AmdVendor);

    /// <summary>SPD identity of every populated DIMM slot (0x50–0x57 across both FCH ports).</summary>
    public IReadOnlyList<RamStick> ReadSticks()
    {
        var sticks = new List<RamStick>();
        RunLocked(() =>
        {
            foreach (var bus in AmdBuses())
            {
                for (byte address = 0x50; address <= 0x57; address++)
                {
                    try
                    {
                        var detector = new SPDDetector(bus, address);
                        if (detector.IsValid && detector.Accessor is { } spd)
                        {
                            sticks.Add(new RamStick(
                                BusName(bus),
                                address,
                                Printable(spd.ModulePartNumber()),
                                Printable(spd.GetModuleManufacturerString())));
                        }
                    }
                    catch
                    {
                        // empty slot or unreadable SPD — skip
                    }
                }
            }
        });

        return sticks;
    }

    /// <summary>
    /// Finds every ENE RGB controller: remaps unconfigured controllers off 0x77 (fingerprint
    /// first), then probes the candidate list on both FCH SMBus ports.
    /// </summary>
    public IReadOnlyList<EneRgbDevice> FindRgbControllers()
    {
        var devices = new List<EneRgbDevice>();
        RunLocked(() =>
        {
            foreach (var bus in AmdBuses())
            {
                var smbus = new RamSpdSmBus(bus);
                RemapUnconfigured(smbus);

                foreach (var address in CandidateAddresses)
                {
                    if (smbus.ReadByte(address) < 0 && smbus.ReadByteData(address, 0x00) < 0)
                    {
                        continue; // nothing there
                    }

                    if (!EneRgbDevice.Fingerprint(smbus, address))
                    {
                        continue; // some other SMBus device — never touch it
                    }

                    var device = new EneRgbDevice(smbus, address);
                    if (device.Initialize())
                    {
                        _log?.Invoke($"ENE RGB controller at 0x{address:X2} on {smbus.Name}: '{device.Version}', {device.LedCount} LEDs");
                        devices.Add(device);
                    }

                    Thread.Sleep(1);
                }
            }
        });

        return devices;
    }

    private void RemapUnconfigured(RamSpdSmBus smbus)
    {
        for (byte slot = 0; slot < 8; slot++)
        {
            if (smbus.ReadByte(UnconfiguredAddress) < 0)
            {
                return; // nothing (left) at 0x77
            }

            if (!EneRgbDevice.Fingerprint(smbus, UnconfiguredAddress))
            {
                _log?.Invoke($"device at 0x77 on {smbus.Name} is not an ENE controller — leaving it alone");
                return;
            }

            var free = CandidateAddresses.FirstOrDefault(
                a => a != UnconfiguredAddress && smbus.ReadByte(a) < 0);
            if (free == 0)
            {
                return; // no free address left
            }

            _log?.Invoke($"remapping ENE controller at 0x77 (slot {slot}) to 0x{free:X2} on {smbus.Name}");
            new EneRgbDevice(smbus, UnconfiguredAddress).RemapAddress(slot, free);
            Thread.Sleep(1);
        }
    }

    private static void RunLocked(Action action)
    {
        var acquired = false;
        try
        {
            acquired = SmbusMutex.WaitOne(TimeSpan.FromSeconds(5));
            action();
        }
        finally
        {
            if (acquired)
            {
                SmbusMutex.ReleaseMutex();
            }
        }
    }

    private static string BusName(SMBusInterface bus) => $"{bus.DeviceName ?? "SMBus"} port {bus.PortID}";

    /// <summary>SPD strings can carry stray control bytes — keep printable ASCII only.</summary>
    private static string Printable(string? raw) =>
        new([.. (raw ?? "").Where(c => c is >= ' ' and <= '~')]);

    public void Dispose() => _computer.Close();

    /// <summary>ISmBus over a RAMSPDToolkit bus (holds no state; the scanner owns locking).</summary>
    private sealed class RamSpdSmBus(SMBusInterface bus) : ISmBus
    {
        public string Name { get; } = BusName(bus);

        public int ReadByte(byte address) => bus.i2c_smbus_read_byte(address);

        public int ReadByteData(byte address, byte command) => bus.i2c_smbus_read_byte_data(address, command);

        public int WriteByteData(byte address, byte command, byte value) =>
            bus.i2c_smbus_write_byte_data(address, command, value);

        public int WriteWordData(byte address, byte command, ushort value) =>
            bus.i2c_smbus_write_word_data(address, command, value);

        public int WriteBlockData(byte address, byte command, byte[] data) =>
            bus.i2c_smbus_write_block_data(address, command, (byte)data.Length, data);
    }
}
