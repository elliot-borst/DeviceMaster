using System.Globalization;
using DeviceMaster.Core.Safety;

namespace DeviceMaster.Sensors.Linux;

/// <summary>One motherboard fan header as the kernel's Super I/O driver exposes it.</summary>
public sealed record HwmonFanHeader(int Index, string Label, int? Rpm, int Pwm, int Enable);

/// <summary>
/// Motherboard fan headers through the kernel's Super I/O hwmon driver (nct6775 family and the
/// out-of-tree nct6687 driver: Nuvoton chips on ASUS/MSI/ASRock boards). Writes go to
/// <c>pwmN_enable</c> (1 = manual) and <c>pwmN</c> (0–255); <see cref="Release"/> puts back each
/// header's original mode (normally the BIOS curve) so the board keeps cooling after DeviceMaster stops.
///
/// Only headers with a spinning fan at start-up are controlled — an empty header is left alone,
/// and nothing is written below <see cref="SafetyLimits.HeaderMinimumDutyPercent"/>.
///
/// Every write is read back (#35). Some driver/board mappings accept a manual mode but ignore or
/// reject the duty (seen live on an nct6687 board: EIO on writes, duties that never moved, and
/// restores that failed). A header that fails <see cref="FailuresBeforeDrop"/> times running is
/// handed back to the BIOS and dropped from control; a header that can't be handed back is left
/// at full speed and reported, never stuck at whatever duty it last had.
/// </summary>
public sealed class HwmonFanControl : IDisposable
{
    /// <summary>Consecutive failed writes before a header is given back to the BIOS.</summary>
    public const int FailuresBeforeDrop = 3;

    /// <summary>Read-back tolerance: drivers round the duty to the chip's own resolution.</summary>
    private const int PwmTolerance = 3;

    private const int RestoreAttempts = 3;

    private readonly string _dir;
    private readonly Action<string>? _log;
    private readonly Dictionary<int, int> _originalEnable = [];
    private readonly List<int> _controlled;
    private readonly Dictionary<int, int> _written = [];
    private readonly Dictionary<int, int> _failures = [];
    private readonly Dictionary<int, string> _dropped = [];

    private HwmonFanControl(string dir, string chip, List<int> controlled, Action<string>? log)
    {
        _dir = dir;
        _log = log;
        Chip = chip;
        _controlled = controlled;
        foreach (var index in controlled)
        {
            _originalEnable[index] = ReadInt($"pwm{index}_enable") ?? 5;
        }
    }

    /// <summary>The chip name from hwmon (e.g. "nct6799").</summary>
    public string Chip { get; }

    /// <summary>Headers under DeviceMaster's control right now (dropped headers leave this list).</summary>
    public IReadOnlyList<int> Controlled => _controlled;

    /// <summary>Headers that would not take a duty and were handed back to the BIOS, with the reason.</summary>
    public IReadOnlyDictionary<int, string> Dropped => _dropped;

    /// <summary>
    /// The Super I/O fan controller with its spinning headers, or null when no supported chip is
    /// bound (load the driver: <c>modprobe nct6775</c>).
    /// </summary>
    public static HwmonFanControl? Open(string sysRoot = "/sys", Action<string>? log = null)
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
                return new HwmonFanControl(dir, name, spinning, log);
            }
        }

        return null;
    }

    /// <summary>Every header with a pwm control, with its current fan speed and mode.</summary>
    public IReadOnlyList<HwmonFanHeader> Headers() => ReadHeaders(_dir);

    /// <summary>
    /// Sets every controlled header to <paramref name="dutyPercent"/> (floored at the header
    /// minimum, so a fan is never stopped). Writes only on change unless <paramref name="refresh"/>.
    /// Returns a warning per header that failed this time (empty = all good).
    /// </summary>
    public IReadOnlyList<string> Apply(int dutyPercent, bool refresh = false)
    {
        var warnings = new List<string>();
        var duty = SafetyGuard.ClampHeaderDuty(dutyPercent);
        var raw = (int)Math.Ceiling(duty * 255 / 100.0); // round up: never land under the floor (30 % = 76.5)
        foreach (var index in _controlled.ToList())
        {
            if (!refresh && _written.GetValueOrDefault(index, -1) == raw)
            {
                continue;
            }

            string? problem;
            try
            {
                Write($"pwm{index}_enable", 1);
                Write($"pwm{index}", raw);
                var readBack = ReadInt($"pwm{index}");
                problem = readBack is { } r && Math.Abs(r - raw) <= PwmTolerance
                    ? null
                    : $"wrote {raw}, reads back {readBack?.ToString(CultureInfo.InvariantCulture) ?? "nothing"}";
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }

            if (problem is null)
            {
                _written[index] = raw;
                _failures.Remove(index);
                continue;
            }

            _written.Remove(index); // try again next tick
            var failures = _failures[index] = _failures.GetValueOrDefault(index) + 1;
            warnings.Add($"pwm{index}: {problem}");
            if (failures >= FailuresBeforeDrop)
            {
                Drop(index, $"{failures} failed writes in a row (last: {problem})");
            }
        }

        return warnings;
    }

    /// <summary>
    /// Hands every controlled header back to its original mode (the BIOS curve), verifying each
    /// restore. A header that will not go back is set to full speed instead. Returns one message
    /// per header that could not be handed back (empty = all restored).
    /// </summary>
    public IReadOnlyList<string> Release()
    {
        var problems = new List<string>();
        foreach (var index in _controlled)
        {
            if (Restore(index) is { } problem)
            {
                problems.Add(problem);
            }
        }

        _written.Clear();
        _failures.Clear();
        return problems;
    }

    public void Dispose() => Release();

    private void Drop(int index, string reason)
    {
        var restore = Restore(index);
        _controlled.Remove(index);
        _written.Remove(index);
        _failures.Remove(index);
        _dropped[index] = reason;
        _log?.Invoke($"motherboard fans: pwm{index} does not take a duty ({reason}) — handed back to the BIOS and no longer controlled"
            + (restore is null ? "" : $"; {restore}"));
    }

    /// <summary>Puts one header back in its original mode, verified. Null on success, else what happened.</summary>
    private string? Restore(int index)
    {
        var mode = _originalEnable.GetValueOrDefault(index, 5);
        string? last = null;
        for (var attempt = 1; attempt <= RestoreAttempts; attempt++)
        {
            try
            {
                Write($"pwm{index}_enable", mode);
                var now = ReadInt($"pwm{index}_enable");
                if (now == mode)
                {
                    return null;
                }

                last = $"pwm{index}_enable reads back {now?.ToString(CultureInfo.InvariantCulture) ?? "nothing"}";
            }
            catch (Exception ex)
            {
                last = ex.Message;
            }

            if (attempt < RestoreAttempts)
            {
                Thread.Sleep(100 * attempt);
            }
        }

        // still manual: never leave it at the last curve duty — full speed until someone looks
        string fallback;
        try
        {
            Write($"pwm{index}", 255);
            fallback = "left at full speed";
        }
        catch (Exception ex)
        {
            fallback = $"full-speed fallback also failed ({ex.Message})";
        }

        var message = $"pwm{index} could not be handed back to the BIOS (mode {mode}: {last}) — {fallback}; "
            + $"fix with: echo {mode} > {Path.Combine(_dir, $"pwm{index}_enable")}";
        _log?.Invoke($"motherboard fans: {message}");
        return message;
    }

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
