namespace DeviceMaster.Devices.EneRgb;

/// <summary>
/// Finds ENE RGB controllers on RGB DRAM over one SMBus — transport-agnostic, shared by the
/// Windows scanner (PawnIO/RAMSPDToolkit) and the Linux headless loop (i2c-dev SMBus ioctls).
/// Ported from OpenRGB's ENESMBusControllerDetect with one deliberate hardening: a device
/// answering at 0x77 is READ-fingerprinted as ENE before the remap registers are written —
/// the reference writes blindly to whatever sits at 0x77. Nothing that fails the read-only
/// fingerprint is ever written to. The caller owns any cross-process bus locking.
/// </summary>
public static class EneDramScan
{
    /// <summary>Candidate ENE DRAM addresses, in the reference probe order.</summary>
    public static readonly IReadOnlyList<byte> CandidateAddresses =
    [
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77,
        0x4F, 0x66, 0x67, 0x39, 0x3A, 0x3B, 0x3C, 0x3D,
    ];

    public const byte UnconfiguredAddress = 0x77;

    /// <summary>DIMM slots an unconfigured-controller remap can enumerate.</summary>
    private const int MaxSlots = 8;

    /// <summary>
    /// Remaps unconfigured controllers off 0x77 (fingerprint first), then returns every
    /// fingerprinted, initialized controller among the candidate addresses.
    /// </summary>
    public static IReadOnlyList<EneRgbDevice> FindControllers(ISmBus bus, Action<string>? log = null, Action? pause = null)
    {
        pause ??= () => Thread.Sleep(1);
        RemapUnconfigured(bus, log, pause);

        var devices = new List<EneRgbDevice>();
        foreach (var address in CandidateAddresses)
        {
            if (bus.ReadByte(address) < 0 && bus.ReadByteData(address, 0x00) < 0)
            {
                continue; // nothing there
            }

            if (!EneRgbDevice.Fingerprint(bus, address))
            {
                continue; // some other SMBus device — never touch it
            }

            var device = new EneRgbDevice(bus, address);
            if (device.Initialize())
            {
                log?.Invoke($"ENE RGB controller at 0x{address:X2} on {bus.Name}: '{device.Version}', {device.LedCount} LEDs");
                devices.Add(device);
            }

            pause();
        }

        return devices;
    }

    private static void RemapUnconfigured(ISmBus bus, Action<string>? log, Action pause)
    {
        for (byte slot = 0; slot < MaxSlots; slot++)
        {
            if (bus.ReadByte(UnconfiguredAddress) < 0)
            {
                return; // nothing (left) at 0x77
            }

            if (!EneRgbDevice.Fingerprint(bus, UnconfiguredAddress))
            {
                log?.Invoke($"device at 0x77 on {bus.Name} is not an ENE controller — leaving it alone");
                return;
            }

            var free = CandidateAddresses.FirstOrDefault(a => a != UnconfiguredAddress && bus.ReadByte(a) < 0);
            if (free == 0)
            {
                return; // no free address left
            }

            log?.Invoke($"remapping ENE controller at 0x77 (slot {slot}) to 0x{free:X2} on {bus.Name}");
            new EneRgbDevice(bus, UnconfiguredAddress).RemapAddress(slot, free);
            pause();
        }
    }
}
