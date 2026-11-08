using System.Globalization;
using DeviceMaster.Core.Safety;

namespace DeviceMaster.Sensors.Linux;

/// <summary>One motherboard fan header as the kernel's Super I/O driver exposes it.</summary>
public sealed record HwmonFanHeader(int Index, string Label, int? Rpm, int Pwm, int Enable);

/// <summary>
/// Motherboard fan headers through the kernel's Super I/O hwmon driver (nct6775 family: Nuvoton
/// NCT67xx chips on ASUS/MSI/ASRock boards). Writes go to <c>pwmN_enable</c> (1 = manual) and
/// <c>pwmN</c> (0–255); <see cref="Release"/> puts back each header's original mode (normally the
/// BIOS SmartFan curve, 5) so the board keeps cooling after DeviceMaster stops.
///
/// Only headers with a spinning fan at start-up are controlled — an empty header is left alone,
/// and nothing is written below <see cref="SafetyLimits.HeaderMinimumDutyPercent"/>.
/// </summary>
public sealed class HwmonFanControl : IDisposable
{
    private readonly string _dir;
    private readonly Dictionary<int, int> _originalEnable = [];
    private readonly List<int> _controlled;
    private readonly Dictionary<int, int> _written = [];

    private HwmonFanControl(string dir, string chip, List<int> controlled)
    {
        _dir = dir;
        Chip = chip;
        _controlled = controlled;
        foreach (var index in controlled)
        {
            _originalEnable[index] = ReadInt($"pwm{index}_enable") ?? 5;
        }
    }

    /// <summary>The chip name from hwmon (e.g. "nct6799").</summary>
    public string Chip { get; }

    public IReadOnlyList<int> Controlled => _controlled;

    /// <summary>
    /// The Super I/O fan controller with its spinning headers, or null when no supported chip is
    /// bound (load the driver: <c>modprobe nct6775</c>).
    /// </summary>
    public static HwmonFanControl? Open(string sysRoot = "/sys")
    {
        var root = Path.Combine(sysRoot, "class", "hwmon");
        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = SafeRead(Path.Combine(dir, "name"))?.Trim();
            // Nuvoton Super I/O chips: the nct6775 driver names them nct6775 … nct6799, nct6683/6687
            if (name is null || !name.StartsWith("nct6", StringComparison.Ordinal))
            {
                continue;
            }

            var headers = ReadHeaders(dir);
            var spinning = headers.Where(h => h.Rpm is > 0).Select(h => h.Index).ToList();
            if (headers.Count > 0)
            {
                return new HwmonFanControl(dir, name, spinning);
            }
        }

        return null;
    }

    /// <summary>Every header with a pwm control, with its current fan speed and mode.</summary>
    public IReadOnlyList<HwmonFanHeader> Headers() => ReadHeaders(_dir);

    /// <summary>
    /// Sets every controlled header to <paramref name="dutyPercent"/> (floored at the header
    /// minimum, so a fan is never stopped). Writes only on change unless <paramref name="refresh"/>.
    /// </summary>
    public void Apply(int dutyPercent, bool refresh = false)
    {
        var duty = SafetyGuard.ClampHeaderDuty(dutyPercent);
        var raw = (int)Math.Ceiling(duty * 255 / 100.0); // round up: never land under the floor (30 % = 76.5)
        foreach (var index in _controlled)
        {
            if (!refresh && _written.GetValueOrDefault(index, -1) == raw)
            {
                continue;
            }

            Write($"pwm{index}_enable", 1);
            Write($"pwm{index}", raw);
            _written[index] = raw;
        }
    }

    /// <summary>Hands every controlled header back to its original mode (the BIOS curve).</summary>
    public void Release()
    {
        foreach (var (index, mode) in _originalEnable)
        {
            try
            {
                Write($"pwm{index}_enable", mode);
            }
            catch
            {
                // best effort — the BIOS also reclaims the headers at the next boot
            }
        }

        _written.Clear();
    }

    public void Dispose() => Release();

    // ---------- sysfs ----------

    public static List<HwmonFanHeader> ReadHeaders(string dir)
    {
        var headers = new List<HwmonFanHeader>();
        for (var i = 1; i <= 10; i++)
        {
            if (!File.Exists(Path.Combine(dir, $"pwm{i}")))
            {
                continue;
            }

            var label = SafeRead(Path.Combine(dir, $"fan{i}_label"))?.Trim();
            headers.Add(new HwmonFanHeader(
                i,
                string.IsNullOrEmpty(label) ? $"fan{i}" : label,
                ParseInt(SafeRead(Path.Combine(dir, $"fan{i}_input"))),
                ParseInt(SafeRead(Path.Combine(dir, $"pwm{i}"))) ?? 0,
                ParseInt(SafeRead(Path.Combine(dir, $"pwm{i}_enable"))) ?? 0));
        }

        return headers;
    }

    private int? ReadInt(string file) => ParseInt(SafeRead(Path.Combine(_dir, file)));

    private void Write(string file, int value) =>
        File.WriteAllText(Path.Combine(_dir, file), value.ToString(CultureInfo.InvariantCulture));

    private static int? ParseInt(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? SafeRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
