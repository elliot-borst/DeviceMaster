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
/// The detection itself (fingerprint-before-write, 0x77 remap) is the shared
/// <see cref="EneDramScan"/>.
/// Requires administrator + PawnIO. All transactions hold the system SMBus mutex.
/// </summary>
public sealed class EneRamScanner : IDisposable
{
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
                devices.AddRange(EneDramScan.FindControllers(new RamSpdSmBus(bus), _log));
            }
        });

        return devices;
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
