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
/// <para><b>Verification (#35, #37).</b> Embedded controllers apply fan settings asynchronously: a
/// duty reads back mid-ramp for a second or so, and a mode change written right after a duty
/// write can be overtaken by that still-pending duty (seen live on an nct6687 board: a handback
/// that read back fine, then flipped to manual again). So:</para>
/// <list type="bullet">
/// <item>a duty is judged <see cref="VerifyAfterMs"/> after it was written, not immediately;</item>
/// <item>a header that fails <see cref="FailuresBeforeDrop"/> verdicts running is dropped and handed
///   back only after <see cref="SettleMs"/> of quiet, and the handback itself is re-checked
///   <see cref="VerifyAfterMs"/> later (retried; full speed if it never holds);</item>
/// <item><see cref="Release"/> covers every header ever touched, dropped ones included, with the
///   same settle-then-verify rule.</item>
/// </list>
/// A header that cannot be handed back is left at full speed and reported — never stuck at
/// whatever duty it last had.
/// </summary>
public sealed class HwmonFanControl : IDisposable
{
    /// <summary>Consecutive failed verdicts before a header is given back to the BIOS.</summary>
    public const int FailuresBeforeDrop = 3;

    /// <summary>How long after a write its effect is read back.</summary>
    public const int VerifyAfterMs = 2_500;

    /// <summary>Quiet time after the last duty write before a mode change is written.</summary>
    public const int SettleMs = 2_000;

    /// <summary>Handback attempts before a header is set to full speed instead.</summary>
    public const int RestoreAttempts = 3;

    /// <summary>Read-back tolerance: drivers round the duty to the chip's own resolution.</summary>
    private const int PwmTolerance = 3;

    private readonly string _dir;
    private readonly Action<string>? _log;
    private readonly Func<long> _clock;
    private readonly Action<int> _sleep;
    private readonly Dictionary<int, int> _originalEnable = [];
    private readonly List<int> _controlled;
    private readonly Dictionary<int, int> _written = [];
    private readonly Dictionary<int, (int Raw, long At)> _verify = [];
    private readonly Dictionary<int, int> _failures = [];
    private readonly Dictionary<int, string> _dropped = [];
    private readonly Dictionary<int, Handback> _handbacks = [];
    private readonly Dictionary<int, string> _stuck = [];
    private readonly Dictionary<int, long> _lastDutyWrite = [];

    private sealed class Handback
    {
        public long WriteAt;     // when to (re)write the original mode
        public long? VerifyAt;   // when to read it back (null = not written yet)
        public int Attempts;
    }

    private HwmonFanControl(string dir, string chip, List<int> controlled, Action<string>? log, Func<long> clock, Action<int> sleep)
    {
        _dir = dir;
        _log = log;
        _clock = clock;
        _sleep = sleep;
        Chip = chip;
        _controlled = controlled;
        foreach (var index in controlled)
        {
            var mode = ReadInt($"pwm{index}_enable");
            if (mode is null or 0 or 1)
            {
                // manual (or unreadable) at start-up means a previous run never handed it back
                // (killed, OOM, overlapping restart): "restoring" that would leave it stuck — use
                // the chip's automatic mode instead
                var biosMode = BiosModeFor(chip);
                log?.Invoke($"motherboard fans: pwm{index} was in mode {mode?.ToString(CultureInfo.InvariantCulture) ?? "?"} at start-up "
                    + $"(left manual by an earlier run?) — it will be handed back in mode {biosMode}");
                mode = biosMode;
            }

            _originalEnable[index] = mode.Value;
        }
    }

    /// <summary>The automatic (BIOS curve) mode: 2 on nct6683/6687 drivers, SmartFan IV (5) on the nct6775 family.</summary>
    public static int BiosModeFor(string chip) =>
        chip.StartsWith("nct6683", StringComparison.Ordinal) || chip.StartsWith("nct6687", StringComparison.Ordinal) ? 2 : 5;

    /// <summary>The chip name from hwmon (e.g. "nct6799").</summary>
    public string Chip { get; }

    /// <summary>Headers under DeviceMaster's control right now (dropped headers leave this list).</summary>
    public IReadOnlyList<int> Controlled => _controlled;

    /// <summary>Headers that would not take a duty and were given back to the BIOS, with the reason.</summary>
    public IReadOnlyDictionary<int, string> Dropped => _dropped;

    /// <summary>Headers that would not go back to the BIOS and were set to full speed instead.</summary>
    public IReadOnlyDictionary<int, string> Stuck => _stuck;

    /// <summary>
    /// The Super I/O fan controller with its spinning headers, or null when no supported chip is
    /// bound (load the driver: <c>modprobe nct6775</c>). <paramref name="clock"/> and
    /// <paramref name="sleep"/> exist for tests.
    /// </summary>
    public static HwmonFanControl? Open(string sysRoot = "/sys", Action<string>? log = null, Func<long>? clock = null, Action<int>? sleep = null)
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
                return new HwmonFanControl(dir, name, spinning, log, clock ?? (() => Environment.TickCount64), sleep ?? Thread.Sleep);
            }
        }

        return null;
    }

    /// <summary>Every header with a pwm control, with its current fan speed and mode.</summary>
    public IReadOnlyList<HwmonFanHeader> Headers() => ReadHeaders(_dir);

    /// <summary>
    /// Sets every controlled header to <paramref name="dutyPercent"/> (floored at the header
    /// minimum, so a fan is never stopped), judges earlier writes that are due, and advances any
    /// pending handbacks. Writes only on change unless <paramref name="refresh"/>. Call once per tick.
    /// Returns a warning per header that failed this time (empty = all good).
    /// </summary>
    public IReadOnlyList<string> Apply(int dutyPercent, bool refresh = false)
    {
        var warnings = new List<string>();
        var now = _clock();
        if (_handingBack || _released)
        {
            // taking control again: cancel the handback of the headers we still control
            _handingBack = false;
            _released = false;
            foreach (var index in _controlled)
            {
                _handbacks.Remove(index);
            }

            _written.Clear();
        }

        var duty = SafetyGuard.ClampHeaderDuty(dutyPercent);
        var raw = (int)Math.Ceiling(duty * 255 / 100.0); // round up: never land under the floor (30 % = 76.5)

        // 1) verdicts on writes that have had time to land
        foreach (var (index, (wrote, at)) in _verify.ToList())
        {
            if (now - at < VerifyAfterMs)
            {
                continue;
            }

            _verify.Remove(index);
            _written.Remove(index); // re-written next tick, then judged again
            var readBack = ReadInt($"pwm{index}");
            if (readBack is { } r && Math.Abs(r - wrote) <= PwmTolerance)
            {
                _failures.Remove(index);
                continue;
            }

            Fail(index, $"wrote {wrote}, reads back {readBack?.ToString(CultureInfo.InvariantCulture) ?? "nothing"} after {VerifyAfterMs / 1000.0:0.#} s", warnings, now);
        }

        // 2) duty writes
        foreach (var index in _controlled.ToList())
        {
            if (!refresh && _written.GetValueOrDefault(index, -1) == raw)
            {
                continue;
            }

            try
            {
                Write($"pwm{index}_enable", 1);
                Write($"pwm{index}", raw);
                _written[index] = raw;
                _lastDutyWrite[index] = now;
                _verify[index] = (raw, now);
            }
            catch (Exception ex)
            {
                _written.Remove(index); // try again next tick
                _verify.Remove(index);
                Fail(index, ex.Message, warnings, now);
            }
        }

        // 3) handbacks of dropped headers
        AdvanceHandbacks(now);
        warnings.AddRange(_stuck.Select(s => $"pwm{s.Key}: {s.Value}"));
        return warnings;
    }

    /// <summary>
    /// Non-blocking handback for the loop (mode Off, or motherboard fans switched off): call it
    /// every tick instead of <see cref="Apply"/>. Each header goes back to its original mode after
    /// the settle time and is confirmed a moment later, over the following ticks. Returns warnings
    /// for headers that would not go back (set to full speed).
    /// </summary>
    public IReadOnlyList<string> HandBack()
    {
        var now = _clock();
        if (!_handingBack && !_released)
        {
            _handingBack = true;
            _verify.Clear();
            foreach (var index in _controlled)
            {
                _handbacks[index] = new Handback { WriteAt = _lastDutyWrite.GetValueOrDefault(index, now - SettleMs) + SettleMs };
            }
        }

        AdvanceHandbacks(now);
        return _stuck.Select(s => $"pwm{s.Key}: {s.Value}").ToList();
    }

    private bool _handingBack;
    private bool _released;

    /// <summary>
    /// Shutdown: hands every header DeviceMaster ever touched (dropped ones included) back to its
    /// original mode — waits for pending duties to settle, writes the mode, and confirms it held a
    /// moment later, retrying, and setting full speed where it never holds. Blocks for a few
    /// seconds (the loop uses <see cref="HandBack"/> instead). Returns one message per header that
    /// could not be handed back (empty = all restored).
    /// </summary>
    public IReadOnlyList<string> Release()
    {
        var pending = _originalEnable.Keys.ToHashSet();
        if (pending.Count == 0 || _released)
        {
            return [];
        }

        // let the last duty writes land first, or they can overtake the mode change
        var settleUntil = _lastDutyWrite.Values.DefaultIfEmpty(long.MinValue).Max() + SettleMs;
        var wait = settleUntil - _clock();
        if (wait > 0)
        {
            _sleep((int)wait);
        }

        var last = new Dictionary<int, string>();
        for (var attempt = 1; attempt <= RestoreAttempts && pending.Count > 0; attempt++)
        {
            foreach (var index in pending)
            {
                try
                {
                    Write($"pwm{index}_enable", _originalEnable[index]);
                }
                catch (Exception ex)
                {
                    last[index] = ex.Message;
                }
            }

            _sleep(VerifyAfterMs); // a mode change can be undone by the controller a moment later
            foreach (var index in pending.ToList())
            {
                var mode = ReadInt($"pwm{index}_enable");
                if (mode == _originalEnable[index])
                {
                    pending.Remove(index);
                }
                else
                {
                    last[index] = $"pwm{index}_enable reads back {mode?.ToString(CultureInfo.InvariantCulture) ?? "nothing"}";
                }
            }
        }

        var problems = pending.Select(index => FullSpeed(index, last.GetValueOrDefault(index, "did not hold"))).ToList();
        _released = true;
        _handingBack = false;
        _written.Clear();
        _verify.Clear();
        _failures.Clear();
        _handbacks.Clear();
        _lastDutyWrite.Clear();
        return problems;
    }

    public void Dispose() => Release();

    private void Fail(int index, string problem, List<string> warnings, long now)
    {
        warnings.Add($"pwm{index}: {problem}");
        var failures = _failures[index] = _failures.GetValueOrDefault(index) + 1;
        if (failures < FailuresBeforeDrop || !_controlled.Contains(index))
        {
            return;
        }

        _controlled.Remove(index);
        _written.Remove(index);
        _verify.Remove(index);
        _failures.Remove(index);
        _dropped[index] = $"{failures} failed writes in a row (last: {problem})";
        _handbacks[index] = new Handback { WriteAt = _lastDutyWrite.GetValueOrDefault(index, now) + SettleMs };
        _log?.Invoke($"motherboard fans: pwm{index} does not take a duty ({_dropped[index]}) — handing it back to the BIOS");
    }

    private void AdvanceHandbacks(long now)
    {
        foreach (var (index, h) in _handbacks.ToList())
        {
            var mode = _originalEnable[index];
            if (h.VerifyAt is null)
            {
                if (now < h.WriteAt)
                {
                    continue;
                }

                try
                {
                    Write($"pwm{index}_enable", mode);
                }
                catch
                {
                    // judged below like any other non-holding handback
                }

                h.Attempts++;
                h.VerifyAt = now + VerifyAfterMs;
                continue;
            }

            if (now < h.VerifyAt)
            {
                continue;
            }

            var current = ReadInt($"pwm{index}_enable");
            if (current == mode)
            {
                _handbacks.Remove(index);
                _log?.Invoke($"motherboard fans: pwm{index} is back on the BIOS curve (mode {mode}, confirmed)");
            }
            else if (h.Attempts >= RestoreAttempts)
            {
                _handbacks.Remove(index);
                FullSpeed(index, $"pwm{index}_enable reads back {current?.ToString(CultureInfo.InvariantCulture) ?? "nothing"}");
            }
            else
            {
                h.VerifyAt = null; // write it again now
                h.WriteAt = now;
            }
        }
    }

    /// <summary>The handback failed: full speed rather than a frozen duty. Logged with the manual fix.</summary>
    private string FullSpeed(int index, string why)
    {
        var mode = _originalEnable.GetValueOrDefault(index, 5);
        string fallback;
        try
        {
            try
            {
                Write($"pwm{index}_enable", 1); // usually already manual: that is the problem
            }
            catch
            {
                // the duty below is what matters
            }

            Write($"pwm{index}", 255);
            fallback = "left at full speed";
        }
        catch (Exception ex)
        {
            fallback = $"full-speed fallback also failed ({ex.Message})";
        }

        var message = $"pwm{index} could not be handed back to the BIOS (mode {mode}: {why}) — {fallback}; "
            + $"fix with: echo {mode} > {Path.Combine(_dir, $"pwm{index}_enable")}";
        _stuck[index] = message;
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
