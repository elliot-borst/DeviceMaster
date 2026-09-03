namespace DeviceMaster.Platform.Linux;

/// <summary>
/// Finds the /dev/i2c-N node of an NVIDIA GPU's internal I2C bus (where the board-partner RGB
/// controller — ENE at 0x67 on ASUS cards — lives). The nvidia driver exposes each GPU's buses
/// as i2c-dev devices named "NVIDIA i2c adapter <n> at <pci>" under /sys/bus/i2c/devices.
/// </summary>
public static class GpuI2cLocator
{
    public sealed record Match(string DevicePath, string Name, string PciAddress);

    /// <summary>All NVIDIA i2c adapter nodes visible to this host.</summary>
    public static IReadOnlyList<Match> FindAll(string sysRoot = "/sys")
    {
        var matches = new List<Match>();
        var deviceRoot = Path.Combine(sysRoot, "bus", "i2c", "devices");
        if (!Directory.Exists(deviceRoot))
        {
            return matches;
        }

        foreach (var entry in Directory.EnumerateDirectories(deviceRoot, "i2c-*"))
        {
            var name = ReadFile(Path.Combine(entry, "name"));
            if (name is null || !name.StartsWith("NVIDIA i2c adapter", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pci = ExtractPciAddress(name);
            var node = Path.GetFileName(entry); // "i2c-10"
            matches.Add(new Match($"/dev/{node}", name, pci));
        }

        return matches;
    }

    /// <summary>
    /// The i2c-dev node for the wanted GPU. <paramref name="preferredPciAddress"/> (e.g.
    /// "01:00.0", bus/device/function — width-insensitive) selects among several GPUs; null
    /// takes the first NVIDIA adapter found. Returns null when no adapter is visible.
    /// </summary>
    public static Match? Find(string? preferredPciAddress = null, string sysRoot = "/sys")
    {
        var all = FindAll(sysRoot);
        if (preferredPciAddress is null)
        {
            return all.FirstOrDefault();
        }

        return all.FirstOrDefault(m => SamePci(m.PciAddress, preferredPciAddress));
    }

    /// <summary>Normalizes "1:00.0" == "01:00.0" (numeric bus/device/function compare).</summary>
    public static bool SamePci(string a, string b)
    {
        return TryParse(a, out var pa) && TryParse(b, out var pb) && pa == pb;

        static bool TryParse(string text, out (int Bus, int Device, int Function) result)
        {
            result = default;
            var parts = text.Split(':');
            if (parts.Length != 2)
            {
                return false;
            }

            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var bus))
            {
                return false;
            }

            var devFn = parts[1].Split('.');
            if (devFn.Length != 2
                || !int.TryParse(devFn[0], System.Globalization.NumberStyles.HexNumber, null, out var device)
                || !int.TryParse(devFn[1], System.Globalization.NumberStyles.HexNumber, null, out var function))
            {
                return false;
            }

            result = (bus, device, function);
            return true;
        }
    }

    /// <summary>Extracts the PCI address from an adapter name ("... at 1:00.0").</summary>
    public static string? ExtractPciAddress(string name)
    {
        var at = name.LastIndexOf(" at ", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var pci = name[(at + 4)..].Trim();
        return pci.Length > 0 ? pci : null;
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return null;
        }
    }
}
