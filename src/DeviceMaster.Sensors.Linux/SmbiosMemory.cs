using System.Buffers.Binary;

namespace DeviceMaster.Sensors.Linux;

/// <summary>
/// Installed RAM from the firmware's SMBIOS table (the data <c>dmidecode -t memory</c> shows), so the
/// screens can read "22 / 64 GB" instead of the smaller usable figure in /proc/meminfo, which has the
/// firmware, integrated-GPU and crash-kernel reservations taken out (#106). Root-only file.
/// </summary>
public static class SmbiosMemory
{
    /// <summary>Installed RAM in GB (GiB, as sticks are sold), or null when the table can't be read.</summary>
    public static double? ReadInstalledGb(string sysRoot = "/sys")
    {
        try
        {
            var bytes = InstalledBytes(File.ReadAllBytes(Path.Combine(sysRoot, "firmware", "dmi", "tables", "DMI")));
            return bytes is { } b ? b / 1_073_741_824.0 : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sum of every populated Memory Device (structure type 17) in a raw SMBIOS table; null when there
    /// are none. Size at offset 0x0C: 0 = empty slot, 0xFFFF = unknown, 0x7FFF = see the extended size
    /// (MB) at 0x1C, bit 15 set = the size is in KB, otherwise MB. Pure: unit-tested.
    /// </summary>
    public static long? InstalledBytes(ReadOnlySpan<byte> table)
    {
        long total = 0;
        var any = false;
        var i = 0;
        while (i + 4 <= table.Length)
        {
            var type = table[i];
            var length = table[i + 1];
            if (type == 127 || length < 4 || i + length > table.Length)
            {
                break; // end-of-table marker, or a broken structure
            }

            if (type == 17 && length >= 0x0E)
            {
                var size = BinaryPrimitives.ReadUInt16LittleEndian(table.Slice(i + 0x0C, 2));
                if (size is not 0 and not 0xFFFF)
                {
                    total += size == 0x7FFF && length >= 0x20
                        ? (long)BinaryPrimitives.ReadUInt32LittleEndian(table.Slice(i + 0x1C, 4)) * 1024 * 1024
                        : (size & 0x8000) != 0 ? (long)(size & 0x7FFF) * 1024 : (long)size * 1024 * 1024;
                    any = true;
                }
            }

            // skip the formatted part, then the string set, which ends with two NUL bytes
            var j = i + length;
            while (j + 1 < table.Length && !(table[j] == 0 && table[j + 1] == 0))
            {
                j++;
            }

            i = j + 2;
        }

        return any ? total : null;
    }
}
