using DeviceMaster.Devices.EneRgb;

namespace DeviceMaster.Devices.Nvidia;

/// <summary>Result of scanning one NVIDIA GPU for a known RGB controller.</summary>
public sealed record GpuRgb(NvApi.NvGpu Gpu, string Partner, EneRgbDevice? Ene);

/// <summary>
/// Identifies the board partner of each NVIDIA GPU (PCI subsystem vendor via NvAPI) and
/// locates its RGB controller. Implemented: ASUS cards (ENE controller at I2C 0x67 —
/// TUF/ROG Strix/Astral all use it, per OpenRGB's detector tables). Other partners are
/// reported by name so support can be added when such a card is in front of us.
/// </summary>
public static class GpuRgbLocator
{
    private const byte AsusEneAddress = 0x67;

    private static readonly Dictionary<ushort, string> Partners = new()
    {
        [0x1043] = "ASUS",
        [0x1462] = "MSI",
        [0x1458] = "Gigabyte",
        [0x19DA] = "Zotac",
        [0x1569] = "Palit",
        [0x196E] = "PNY",
        [0x10DE] = "NVIDIA Founders Edition",
    };

    public static IReadOnlyList<GpuRgb> Scan(Action<string>? log = null)
    {
        var results = new List<GpuRgb>();
        foreach (var gpu in NvApi.EnumerateGpus())
        {
            var partner = Partners.GetValueOrDefault(gpu.SubVendor, $"unknown (0x{gpu.SubVendor:X4})");
            log?.Invoke($"{gpu.Name}: device {gpu.Vendor:X4}:{gpu.Device:X4}, subsystem {gpu.SubVendor:X4}:{gpu.SubDevice:X4} ({partner})");

            EneRgbDevice? ene = null;
            if (gpu.SubVendor == 0x1043)
            {
                var bus = new NvApiSmBus(gpu);
                if (EneRgbDevice.Fingerprint(bus, AsusEneAddress))
                {
                    var device = new EneRgbDevice(bus, AsusEneAddress);
                    if (device.Initialize())
                    {
                        log?.Invoke($"  ENE controller at 0x67: '{device.Version}', {device.LedCount} LEDs");
                        ene = device;
                    }
                }
                else
                {
                    log?.Invoke("  no ENE fingerprint at 0x67 — RGB controller not found on the I2C bus");
                }
            }

            results.Add(new GpuRgb(gpu, partner, ene));
        }

        return results;
    }
}
