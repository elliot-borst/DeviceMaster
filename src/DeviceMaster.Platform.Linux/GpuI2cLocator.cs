namespace DeviceMaster.Platform.Linux;

/// <summary>
/// Finds the /dev/i2c-N node of an NVIDIA GPU's internal I2C bus (where the board-partner RGB
/// controller — ENE at 0x67 on ASUS cards — lives). The nvidia driver exposes each GPU's buses
/// as i2c-dev devices named "NVIDIA i2c adapter <n> at <pci>" under /sys/bus/i2c/devices.
/// </summary>
public static class GpuI2cLocator
{
    public sealed record Match(string DevicePath, string Name, string PciAddress);

    /// <summary>PCI subsystem vendor of ASUS boards (their GPUs carry the ENE RGB controller).</summary>
    public const int AsusSubsystemVendor = 0x1043;

    /// <summary>All NVIDIA i2c adapter nodes visible to this host, in adapter order (1, 2, …) per GPU.</summary>
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

        // directory order is arbitrary (i2c-10 sorts before i2c-7); the RGB chip is on adapter 1
        return matches.OrderBy(m => m.PciAddress, StringComparer.Ordinal).ThenBy(m => AdapterNumber(m.Name)).ToList();
    }

    /// <summary>Every adapter of the wanted GPU (null = the first GPU found), in adapter order.</summary>
    public static IReadOnlyList<Match> Candidates(string? preferredPciAddress = null, string sysRoot = "/sys")
    {
        var all = FindAll(sysRoot);
        var pci = preferredPciAddress ?? all.FirstOrDefault()?.PciAddress;
        return pci is null ? [] : all.Where(m => m.PciAddress is { } p && SamePci(p, pci)).ToList();
    }

    /// <summary>The GPU's PCI subsystem vendor (the board maker: 0x1043 = ASUS), or null.</summary>
    public static int? SubsystemVendor(string pciAddress, string sysRoot = "/sys")
    {
        var parts = pciAddress.Split(':', '.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var bus)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var device)
            || !int.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var function))
        {
            return null;
        }

        var text = ReadFile(Path.Combine(sysRoot, "bus", "pci", "devices", $"0000:{bus:x2}:{device:x2}.{function:x}", "subsystem_vendor"));
        return text is not null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out var vendor) ? vendor : null;
    }

    /// <summary>"NVIDIA i2c adapter 4 at 1:00.0" → 4 (unknown → last).</summary>
    public static int AdapterNumber(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"adapter (\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : int.MaxValue;
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
