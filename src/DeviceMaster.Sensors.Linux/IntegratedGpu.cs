using System.Globalization;
using System.Text.RegularExpressions;
using DeviceMaster.Core.Curves;

namespace DeviceMaster.Sensors.Linux;

/// <summary>
/// GPU figures straight from the kernel, for machines the nvidia-smi path doesn't cover — an
/// integrated GPU (AMD APU, Intel) or an AMD card. Read-only sysfs, no tools needed:
/// temperature from the hwmon devices named <c>amdgpu</c>, <c>xe</c> or <c>i915</c>; busy % from
/// amdgpu's <c>gpu_busy_percent</c> (Intel has no such file, so its load stays unknown).
/// Callers use these only as a fallback: a GPU nvidia-smi reports always wins.
/// </summary>
public static partial class IntegratedGpu
{
    /// <summary>Hwmon driver names in preference order, each with its preferred temperature label.</summary>
    private static readonly (string Sensor, string Label)[] Preference =
    [
        ("amdgpu", "edge"),
        ("xe", "pkg"),
        ("i915", "pkg"),
    ];

    /// <summary>
    /// The GPU temperature among <paramref name="all"/> hwmon readings: amdgpu's "edge" first,
    /// then any amdgpu channel, then xe, then i915. Several devices of one driver (an APU and an
    /// AMD card) → the hottest, so a working card is never hidden. Implausible values are skipped.
    /// Null when there is no such GPU.
    /// </summary>
    public static double? TemperatureC(IReadOnlyList<HwmonTemp> all)
    {
        foreach (var (sensor, label) in Preference)
        {
            var ofDriver = all.Where(t => t.Sensor == sensor && SensorValidity.IsPlausibleTemperature(t.ValueC)).ToList();
            if (ofDriver.Count == 0)
            {
                continue;
            }

            var preferred = ofDriver.Where(t => t.Key == label).ToList();
            return (preferred.Count > 0 ? preferred : ofDriver).Max(t => t.ValueC);
        }

        return null;
    }

    /// <summary>Busy % of the busiest GPU with a <c>gpu_busy_percent</c> file (amdgpu); null when none.</summary>
    public static double? BusyPercent(string sysRoot = "/sys")
    {
        try
        {
            var drm = Path.Combine(sysRoot, "class", "drm");
            if (!Directory.Exists(drm))
            {
                return null;
            }

            double? best = null;
            foreach (var card in Directory.GetDirectories(drm, "card*").Where(d => CardDir().IsMatch(Path.GetFileName(d))))
            {
                string text;
                try
                {
                    text = File.ReadAllText(Path.Combine(card, "device", "gpu_busy_percent"));
                }
                catch
                {
                    continue; // not an amdgpu card
                }

                if (ParseBusyPercent(text) is { } busy && (best is null || busy > best))
                {
                    best = busy;
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A <c>gpu_busy_percent</c> file's content; null unless it is a number 0–100.</summary>
    public static double? ParseBusyPercent(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 100 ? v : null;

    [GeneratedRegex(@"^card\d+$")]
    private static partial Regex CardDir();
}
