namespace DeviceMaster.Control;

/// <summary>One filesystem as the fleet display shows it.</summary>
public sealed record StorageInfo(string Name, long TotalBytes, long UsedBytes)
{
    public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;
}

/// <summary>Storage, re-read at most every 30 s (disk usage doesn't need the 1 Hz loop cadence).</summary>
public sealed class StorageSampler
{
    private IReadOnlyList<StorageInfo> _last = [];
    private long _due;

    public IReadOnlyList<StorageInfo> Get(IEnumerable<string>? paths = null)
    {
        if (Environment.TickCount64 >= _due)
        {
            _due = Environment.TickCount64 + 30_000;
            _last = SystemInfo.Storage(paths);
        }

        return _last;
    }
}

/// <summary>Machine-level facts for the fleet display (#49): uptime and storage.</summary>
public static class SystemInfo
{
    /// <summary>
    /// Seconds since boot. Linux reads /proc/uptime, which is the host's even inside a container
    /// (uptime isn't namespaced); elsewhere the system tick count.
    /// </summary>
    public static double UptimeSeconds()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/uptime"))
            {
                var first = File.ReadAllText("/proc/uptime").Split(' ')[0];
                if (double.TryParse(first, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
                {
                    return s;
                }
            }
        }
        catch
        {
            // fall back below
        }

        return Environment.TickCount64 / 1000.0;
    }

    /// <summary>
    /// Fixed disks (Windows), or the given paths (Linux containers see only what's mounted into
    /// them, so the host's filesystems are listed explicitly, e.g. "/host" or "/mnt/user"). Each
    /// entry is "path" or "label=path".
    /// </summary>
    public static IReadOnlyList<StorageInfo> Storage(IEnumerable<string>? paths = null)
    {
        var result = new List<StorageInfo>();
        try
        {
            if (paths is null)
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                {
                    result.Add(new StorageInfo(drive.Name.TrimEnd('\\'), drive.TotalSize, drive.TotalSize - drive.TotalFreeSpace));
                }

                return result;
            }

            foreach (var entry in paths)
            {
                var eq = entry.IndexOf('=');
                var (label, path) = eq > 0 ? (entry[..eq], entry[(eq + 1)..]) : (entry, entry);
                if (!Directory.Exists(path))
                {
                    continue; // DriveInfo would quietly report the parent disk for a missing mount
                }

                try
                {
                    var drive = new DriveInfo(path);
                    if (drive.IsReady && drive.TotalSize > 0)
                    {
                        result.Add(new StorageInfo(label, drive.TotalSize, drive.TotalSize - drive.TotalFreeSpace));
                    }
                }
                catch
                {
                    // a missing mount just isn't listed
                }
            }
        }
        catch
        {
            // storage is informational only
        }

        return result;
    }
}
