using System.Globalization;
using SkiaSharp;

namespace DeviceMaster.Lcd.Skia;

/// <summary>One serving slot on the dashboard: "idle", "generating", "prefill", … and tokens held.</summary>
public sealed record DashboardSlot(string State, int HeldTokens);

/// <summary>
/// One reply's figures: wait before the first word (while reading: the time so far), prompt share
/// reused from cache, draft guesses kept, tokens written. Live = still being worked on.
/// </summary>
public sealed record DashboardReply(bool Live, bool Reading, double? Seconds, double? CacheHitPercent, double? GuessHitPercent, int? Written);

/// <summary>Everything the 8.8" bar shows; any figure may be null (drawn as a dash).</summary>
public sealed record TurzxDashboardData
{
    // local LLM (HasLlm false = no LLM server configured)
    public bool HasLlm { get; init; }
    public string LlmModel { get; init; } = "";
    public string LlmState { get; init; } = "";

    /// <summary>What the model is doing right now ("thinking", "writing a tool call: terminal", …).</summary>
    public string? LlmPhase { get; init; }

    /// <summary>Conversations being worked on, including ones the server runs outside its batch slots.</summary>
    public int LlmRunning { get; init; }
    public double? LlmTokPerSec { get; init; }
    public IReadOnlyList<double> LlmTokHistory { get; init; } = [];
    public IReadOnlyList<DashboardSlot> LlmSlots { get; init; } = [];
    public int LlmQueued { get; init; }
    public bool LlmReachable { get; init; } = true;

    /// <summary>The reply being worked on, else the last finished one.</summary>
    public DashboardReply? LlmReply { get; init; }

    /// <summary>Tokens the current (or last) conversation holds, against the model's context limit.</summary>
    public int? LlmContextUsed { get; init; }
    public int? LlmContextMax { get; init; }

    public string GpuName { get; init; } = "GPU";
    public double? GpuTempC { get; init; }
    public double? GpuUtil { get; init; }
    public double? GpuPowerW { get; init; }

    /// <summary>GPU fan speed in percent (0 = the card's idle fan stop).</summary>
    public double? GpuFanPercent { get; init; }
    public int? GpuFanRpm { get; init; }
    public double? VramUsedGb { get; init; }
    public double? VramTotalGb { get; init; }

    public string CpuName { get; init; } = "CPU";
    public double? CpuTempC { get; init; }
    public double? CpuLoad { get; init; }
    public double? CpuPowerW { get; init; }
    public double? RamUsedGb { get; init; }
    public double? RamTotalGb { get; init; }

    /// <summary>The CPU cooler's fans (motherboard header).</summary>
    public int? FanRpm { get; init; }
    public int FanDuty { get; init; }
    public bool Failsafe { get; init; }
}

/// <summary>
/// The Turzx 8.8" (1920×480) system dashboard, drawn with SkiaSharp in the DeviceMaster "Thermal"
/// palette: local-LLM activity (state, speed, totals, slots), GPU, and CPU/RAM with its cooler.
/// Renders landscape and returns the panel's native 480×1920 BGRA frame for
/// <c>TurzxScreen.SendNativeFrame</c>.
/// </summary>
public static class TurzxDashboard
{
    public const int Width = 1920, Height = 480;

    private static readonly SKColor Bg = SKColor.Parse("#0f1115");
    private static readonly SKColor Card = SKColor.Parse("#171a20");
    private static readonly SKColor Line = SKColor.Parse("#2a2f39");
    private static readonly SKColor Track = SKColor.Parse("#0c0e12");
    private static readonly SKColor Text = SKColor.Parse("#eceef2");
    private static readonly SKColor Dim = SKColor.Parse("#a0a6b3");
    private static readonly SKColor Faint = SKColor.Parse("#6b7180");
    private static readonly SKColor Accent = SKColor.Parse("#ff7a45");
    private static readonly SKColor Cool = SKColor.Parse("#4f8cff");
    private static readonly SKColor Warm = SKColor.Parse("#d06cd8");
    private static readonly SKColor Good = SKColor.Parse("#34d399");
    private static readonly SKColor Warn = SKColor.Parse("#fbbf24");
    private static readonly SKColor Danger = SKColor.Parse("#ef4444");

    // colours for the card names: the model in purple, devices in their maker's colour
    private static readonly SKColor ModelPurple = SKColor.Parse("#a970ff");
    private static readonly SKColor Nvidia = SKColor.Parse("#76b900");
    private static readonly SKColor Amd = SKColor.Parse("#f2353b");
    private static readonly SKColor Intel = SKColor.Parse("#3d9be9");

    private const string Families = "DejaVu Sans, Segoe UI, Liberation Sans, sans-serif";
    private static readonly SKTypeface Regular = Face(SKFontStyle.Normal);
    private static readonly SKTypeface Bold = Face(SKFontStyle.Bold);

    /// <summary>The landscape frame as PNG (for previews and tests).</summary>
    public static byte[] RenderPng(TurzxDashboardData data)
    {
        using var landscape = RenderLandscape(data);
        using var image = SKImage.FromBitmap(landscape);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>The native panel frame: 480×1920 BGRA, landscape rotated 90° (270° when flipped).</summary>
    public static byte[] RenderNative(TurzxDashboardData data, bool flip)
    {
        using var landscape = RenderLandscape(data);
        using var native = new SKBitmap(new SKImageInfo(Height, Width, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(native))
        {
            if (flip)
            {
                canvas.Translate(0, Width);
                canvas.RotateDegrees(-90);
            }
            else
            {
                canvas.Translate(Height, 0);
                canvas.RotateDegrees(90);
            }

            canvas.DrawBitmap(landscape, 0, 0);
        }

        return native.Bytes;
    }

    private static SKBitmap RenderLandscape(TurzxDashboardData d)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Bg);

        // three cards: LLM (widest) | GPU | CPU, the last two sharing what's left equally
        const float pad = 18, gap = 16, top = 18, llmWidth = 840;
        var bottom = Height - top;
        var x = pad;
        if (d.HasLlm)
        {
            DrawLlm(canvas, new SKRect(x, top, x + llmWidth, bottom), d);
            x += llmWidth + gap;
        }

        var gpuWidth = (Width - pad - x - gap) / 2;
        DrawGpu(canvas, new SKRect(x, top, x + gpuWidth, bottom), d);
        x += gpuWidth + gap;
        DrawCpu(canvas, new SKRect(x, top, Width - pad, bottom), d);
        return bitmap;
    }

    // ---------- cards ----------

    private static void DrawLlm(SKCanvas c, SKRect r, TurzxDashboardData d)
    {
        CardShell(c, r, "LOCAL AI", ShortModel(d.LlmModel), 30, bold: true, ModelPurple);

        // a lone conversation runs outside the batch slots (the slots all say idle):
        // light the first idle pills for that work so the bar matches what's happening
        var slots = d.LlmSlots.Count == 0 ? new List<DashboardSlot> { new("idle", 0) } : d.LlmSlots.ToList();
        var extra = Math.Max(0, d.LlmRunning - slots.Count(s => s.State != "idle"));
        for (var i = 0; i < slots.Count && extra > 0; i++)
        {
            if (slots[i].State == "idle")
            {
                slots[i] = new DashboardSlot(d.LlmState, slots[i].HeldTokens);
                extra--;
            }
        }

        // stats hug the right edge; the speed graph and context bar take the rest
        const float statsWidth = 160;
        var leftRight = r.Right - 24 - statsWidth - 40;

        // state: dot + word + live phase (or queue)
        var (stateText, stateColor) = !d.LlmReachable ? ("Offline", Danger) : (StateWord(d.LlmState), StateColor(d.LlmState));
        using (var dot = new SKPaint { Color = stateColor, IsAntialias = true })
        {
            c.DrawCircle(r.Left + 32, r.Top + 99, 8, dot);
        }

        var stateWidth = Label(c, stateText, r.Left + 50, r.Top + 111, 34, stateColor, bold: true);
        var detail = d.LlmQueued > 0 ? $"+{d.LlmQueued} waiting" : d.LlmReachable && d.LlmPhase is { Length: > 0 } phase ? phase : "";
        if (detail.Length > 0)
        {
            var dx = r.Left + 66 + stateWidth;
            FitLabel(c, detail, dx, r.Top + 111, 24, leftRight - dx, d.LlmQueued > 0 ? Warn : Dim);
        }

        // big speed + sparkline
        var speed = d.LlmTokPerSec is { } t && t > 0 ? t.ToString("0", CultureInfo.InvariantCulture) : "0";
        var speedWidth = Label(c, speed, r.Left + 22, r.Top + 226, 112, Text, bold: true);
        Label(c, "tok/s", r.Left + 34 + speedWidth, r.Top + 226, 30, Dim);
        if (d.LlmContextUsed is { } used && d.LlmContextMax is { } max && max > 0)
        {
            // context: a slim bar under the speed graph, amber past 75 %, red past 90 %
            Sparkline(c, new SKRect(r.Left + 24, r.Top + 246, leftRight, r.Top + 298), d.LlmTokHistory);
            var fraction = Math.Clamp((double)used / max, 0, 1);
            var tint = fraction >= 0.9 ? Danger : fraction >= 0.75 ? Warn : Cool;
            // the row sits midway between the graph (ends +298) and the slot pills (start +356)
            var labelW = Label(c, "CONTEXT", r.Left + 24, r.Top + 334, 18, Dim, bold: true);
            var valueW = Label(c, $"{Tokens(used)} / {Tokens(max)}", leftRight, r.Top + 334, 20, fraction >= 0.75 ? tint : Text, bold: true, align: SKTextAlign.Right);
            var track = new SKRect(r.Left + 36 + labelW, r.Top + 321, leftRight - valueW - 14, r.Top + 333);
            using (var bg = new SKPaint { Color = Track, IsAntialias = true })
            {
                c.DrawRoundRect(track, 6, 6, bg);
            }

            using var fill = new SKPaint { Color = tint, IsAntialias = true };
            c.DrawRoundRect(new SKRect(track.Left, track.Top, track.Left + (float)(track.Width * fraction), track.Bottom), 6, 6, fill);
        }
        else
        {
            Sparkline(c, new SKRect(r.Left + 24, r.Top + 250, leftRight, r.Top + 330), d.LlmTokHistory);
        }

        // stats column
        var sx = r.Right - 24 - statsWidth;
        using (var divider = new SKPaint { Color = Line, StrokeWidth = 2 })
        {
            c.DrawLine(sx - 20, r.Top + 70, sx - 20, r.Top + 336, divider);
        }

        // this reply (live) or the last one: wait before the first word, cache reuse, guesses kept, length
        var reply = d.LlmReachable ? d.LlmReply : null;
        var live = reply?.Live == true;
        Label(c, live ? "THIS REPLY" : "LAST REPLY", sx, r.Top + 94, 18, live ? Good : Faint, bold: true);
        if (reply is { Reading: true })
        {
            Stat(c, sx, r.Top + 134, "READING", reply.Seconds is { } s ? $"{s:0.0}" : "…", "s", Warn);
        }
        else
        {
            Stat(c, sx, r.Top + 134, "STARTED IN", reply?.Seconds is { } s ? $"{s:0.0}" : "—", reply?.Seconds is null ? "" : "s");
        }

        Stat(c, sx, r.Top + 192, "CACHE HIT", reply?.CacheHitPercent is { } k ? $"{k:0}%" : live ? "…" : "—", "");
        Stat(c, sx, r.Top + 250, "GUESS HIT", reply?.GuessHitPercent is { } h ? $"{h:0}%" : live ? "…" : "—", "");
        Stat(c, sx, r.Top + 308, "WRITTEN", reply?.Written is { } w ? Tokens(w) : "—", reply?.Written is null ? "" : "tok");

        // slot pills: S1 Write / S2 Idle …, centred
        const float slotGap = 12;
        var slotW = (r.Width - 48 - slotGap * (slots.Count - 1)) / slots.Count;
        for (var i = 0; i < slots.Count; i++)
        {
            var px = r.Left + 24 + i * (slotW + slotGap);
            var sr = new SKRect(px, r.Bottom - 88, px + slotW, r.Bottom - 24);
            var active = slots[i].State != "idle";
            var tint = StateColor(slots[i].State);
            using (var fill = new SKPaint { Color = active ? tint.WithAlpha(40) : Track, IsAntialias = true })
            {
                c.DrawRoundRect(sr, 12, 12, fill);
            }

            using (var border = new SKPaint { Color = active ? tint : Line, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
            {
                c.DrawRoundRect(sr, 12, 12, border);
            }

            var tag = $"S{i + 1}";
            var word = SlotWord(slots[i].State);
            var tagW = Measure(tag, 20, bold: true);
            var wordW = Measure(word, 28, bold: active);
            var tx = sr.MidX - (tagW + 12 + wordW) / 2;
            var baseline = sr.MidY + 10;
            Label(c, tag, tx, baseline, 20, Faint, bold: true);
            Label(c, word, tx + tagW + 12, baseline, 28, active ? tint : Dim, bold: active);
        }
    }

    private static void DrawGpu(SKCanvas c, SKRect r, TurzxDashboardData d)
    {
        CardShell(c, r, "GPU", ShortGpu(d.GpuName), 30, bold: true, VendorColor(d.GpuName));
        Reading(c, r, d.GpuTempC, TempColor(d.GpuTempC, 70, 83), d.GpuPowerW);
        Bar(c, r.Left + 24, r.Right - 24, r.Top + 226, "LOAD", d.GpuUtil, 100, d.GpuUtil is { } u ? $"{u:0}%" : "—");
        Bar(c, r.Left + 24, r.Right - 24, r.Top + 306, "VRAM", d.VramUsedGb, d.VramTotalGb,
            d.VramUsedGb is { } v ? (d.VramTotalGb is { } vt ? $"{v:0.0} / {vt:0} GB" : $"{v:0.0} GB") : "—");
        var fan = d.GpuFanPercent is < 0.5 || (d.GpuFanPercent is null && d.GpuFanRpm == 0) ? "stopped"
            : (d.GpuFanRpm, d.GpuFanPercent) switch
            {
                ({ } rpm, { } pct) => $"{Rpm(rpm)} · {pct:0}%",
                ({ } rpm, null) => Rpm(rpm),
                (null, { } pct) => $"{pct:0}%",
                _ => "—",
            };
        Bar(c, r.Left + 24, r.Right - 24, r.Top + 386, "COOLER", d.GpuFanPercent, 100, fan, fill: Cool, valueColor: Cool);
    }

    private static void DrawCpu(SKCanvas c, SKRect r, TurzxDashboardData d)
    {
        CardShell(c, r, "CPU", ShortCpu(d.CpuName), 30, bold: true, VendorColor(d.CpuName));
        Reading(c, r, d.CpuTempC, TempColor(d.CpuTempC, 75, 88), d.CpuPowerW);
        Bar(c, r.Left + 24, r.Right - 24, r.Top + 226, "LOAD", d.CpuLoad, 100, d.CpuLoad is { } l ? $"{l:0}%" : "—");
        Bar(c, r.Left + 24, r.Right - 24, r.Top + 306, "RAM", d.RamUsedGb, d.RamTotalGb,
            d.RamUsedGb is { } u ? (d.RamTotalGb is { } rt ? $"{u:0} / {rt:0} GB" : $"{u:0} GB") : "—");
        var fans = d.Failsafe ? "FAILSAFE 100%" : d.FanRpm is { } rpm ? $"{Rpm(rpm)} · {d.FanDuty}%" : $"{d.FanDuty}%";
        Bar(c, r.Left + 24, r.Right - 24, r.Top + 386, "COOLER", d.Failsafe ? 100 : d.FanDuty, 100, fans,
            fill: d.Failsafe ? Danger : Cool, valueColor: d.Failsafe ? Danger : Cool);
    }

    private static string Rpm(int rpm) => rpm.ToString("N0", CultureInfo.InvariantCulture) + " rpm";

    // ---------- pieces ----------

    private static void CardShell(SKCanvas c, SKRect r, string title, string subtitle, float subtitleSize, bool bold, SKColor? subtitleColor = null)
    {
        using (var fill = new SKPaint { Color = Card, IsAntialias = true })
        {
            c.DrawRoundRect(r, 18, 18, fill);
        }

        using (var border = new SKPaint { Color = Line, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
        {
            c.DrawRoundRect(r, 18, 18, border);
        }

        var titleWidth = Label(c, title, r.Left + 24, r.Top + 46, 22, Dim, bold: true);
        FitLabel(c, subtitle, r.Right - 24, r.Top + 47, subtitleSize, r.Width - 48 - titleWidth - 24, subtitleColor ?? Text, bold, SKTextAlign.Right);
    }

    /// <summary>Temperature (left) and power draw (right) at one size, room for three digits each.</summary>
    private static void Reading(SKCanvas c, SKRect r, double? tempC, SKColor color, double? powerW)
    {
        const float size = 84, unit = 44;
        var y = r.Top + 160;
        Label(c, tempC is { } t ? $"{t:0}°" : "—", r.Left + 22, y, size, color, bold: true);
        if (powerW is not { } p)
        {
            Label(c, "—", r.Right - 24, y, size, Text, bold: true, align: SKTextAlign.Right);
            return;
        }

        var unitWidth = Label(c, "W", r.Right - 24, y, unit, Dim, bold: true, align: SKTextAlign.Right);
        Label(c, $"{p:0}", r.Right - 34 - unitWidth, y, size, Text, bold: true, align: SKTextAlign.Right);
    }

    private static void Stat(SKCanvas c, float x, float y, string label, string value, string unit, SKColor? color = null)
    {
        Label(c, label, x, y, 16, Dim, bold: true);
        var w = Label(c, value, x, y + 30, 28, color ?? Text, bold: true);
        if (unit.Length > 0)
        {
            Label(c, unit, x + w + 6, y + 30, 18, Dim);
        }
    }

    /// <summary>Label + value line with a bar under it, between <paramref name="x0"/> and <paramref name="x1"/>.</summary>
    private static void Bar(SKCanvas c, float x0, float x1, float y, string label, double? value, double? max, string valueText,
        SKColor? fill = null, SKColor? valueColor = null)
    {
        var labelWidth = Label(c, label, x0, y, 22, Dim, bold: true);
        FitLabel(c, valueText, x1, y + 1, 26, x1 - x0 - labelWidth - 12, valueColor ?? Text, bold: true, align: SKTextAlign.Right);
        var track = new SKRect(x0, y + 12, x1, y + 30);
        using (var bg = new SKPaint { Color = Track, IsAntialias = true })
        {
            c.DrawRoundRect(track, 9, 9, bg);
        }

        if (value is not { } v || max is not > 0)
        {
            return;
        }

        var fraction = (float)Math.Clamp(v / max.Value, 0, 1);
        if (fraction <= 0.005f)
        {
            return;
        }

        var fillRect = new SKRect(track.Left, track.Top, track.Left + track.Width * fraction, track.Bottom);
        using var shader = fill is null
            ? SKShader.CreateLinearGradient(new SKPoint(track.Left, 0), new SKPoint(track.Right, 0), [Cool, Warm, Accent], [0f, 0.5f, 1f], SKShaderTileMode.Clamp)
            : null;
        using var paint = new SKPaint { Color = fill ?? SKColors.White, Shader = shader, IsAntialias = true };
        c.DrawRoundRect(fillRect, 9, 9, paint);
    }

    private static void Sparkline(SKCanvas c, SKRect r, IReadOnlyList<double> history)
    {
        if (history.Count < 2)
        {
            return;
        }

        var max = Math.Max(history.Max(), 10);
        using var path = new SKPath();
        for (var i = 0; i < history.Count; i++)
        {
            var px = r.Left + r.Width * i / (history.Count - 1);
            var py = r.Bottom - (float)(history[i] / max) * r.Height;
            if (i == 0)
            {
                path.MoveTo(px, py);
            }
            else
            {
                path.LineTo(px, py);
            }
        }

        using (var area = new SKPath(path))
        {
            area.LineTo(r.Right, r.Bottom);
            area.LineTo(r.Left, r.Bottom);
            area.Close();
            using var under = new SKPaint { Color = Accent.WithAlpha(40), IsAntialias = true };
            c.DrawPath(area, under);
        }

        using var shader = SKShader.CreateLinearGradient(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0),
            [Cool, Warm, Accent], [0f, 0.5f, 1f], SKShaderTileMode.Clamp);
        using var stroke = new SKPaint { Shader = shader, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, StrokeJoin = SKStrokeJoin.Round };
        c.DrawPath(path, stroke);
    }

    /// <summary>Draws text shrunk (down to 60 %) and then ellipsised to fit <paramref name="maxWidth"/>.</summary>
    private static void FitLabel(SKCanvas c, string text, float x, float y, float size, float maxWidth, SKColor color,
        bool bold = false, SKTextAlign align = SKTextAlign.Left)
    {
        var s = size;
        while (s > size * 0.6f && Measure(text, s, bold) > maxWidth)
        {
            s -= 1;
        }

        while (text.Length > 2 && Measure(text, s, bold) > maxWidth)
        {
            text = text[..^2] + "…";
        }

        Label(c, text, x, y, s, color, bold, align);
    }

    private static float Measure(string text, float size, bool bold)
    {
        using var paint = new SKPaint { TextSize = size, Typeface = bold ? Bold : Regular };
        return paint.MeasureText(text);
    }

/// <summary>Draws text and returns its width.</summary>
    private static float Label(SKCanvas c, string text, float x, float y, float size, SKColor color, bool bold = false, SKTextAlign align = SKTextAlign.Left)
    {
        using var paint = new SKPaint
        {
            Color = color,
            TextSize = size,
            IsAntialias = true,
            Typeface = bold ? Bold : Regular,
            TextAlign = align,
        };
        c.DrawText(text, x, y, paint);
        return paint.MeasureText(text);
    }

    /// <summary>Server state → the short word the bar shows (writing = generating tokens, reading = prefill).</summary>
    public static string StateWord(string state) => state switch
    {
        "idle" => "Idle",
        "generating" or "decode" or "decoding" => "Writing",
        "prefill" or "prefilling" or "reading" => "Reading",
        "offline" => "Offline",
        var other => Fit(Title(other), 9),
    };

    /// <summary>The one-word slot label: Read, Write or Idle (pills are narrow).</summary>
    public static string SlotWord(string state) => StateWord(state) switch
    {
        "Writing" => "Write",
        "Reading" => "Read",
        var other => other,
    };

    private static SKColor StateColor(string state) => state switch
    {
        "idle" or "offline" => Faint,
        "generating" or "decode" or "decoding" => Good,
        "prefill" or "prefilling" or "reading" => Warn,
        _ => Accent,
    };

    /// <summary>The maker's brand colour for a CPU/GPU name (NVIDIA green, AMD red, Intel blue).</summary>
    public static SKColor VendorColor(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"(?i)nvidia|geforce|\b(rtx|gtx|quadro)\b") ? Nvidia
        : System.Text.RegularExpressions.Regex.IsMatch(name, @"(?i)\bamd\b|ryzen|radeon|epyc|threadripper") ? Amd
        : System.Text.RegularExpressions.Regex.IsMatch(name, @"(?i)intel|\bcore\b|\barc\b|xeon") ? Intel
        : Text;

    private static SKColor TempColor(double? t, double warn, double hot) =>
        t is not { } v ? Faint : v >= hot ? Danger : v >= warn ? Warn : Text;

    // ---------- text helpers ----------

    /// <summary>"qwen3.8-flash-next-unsloth-ud-iq3_xxs" → "qwen3.8 flash next · UD-IQ3_XXS".</summary>
    public static string ShortModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "model";
        }

        var name = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;
        var quant = System.Text.RegularExpressions.Regex.Match(name, @"(?i)(ud-)?(i?q\d[\w]*|f16|bf16|fp8|nvfp4|int4)$");
        if (quant.Success)
        {
            var baseName = name[..quant.Index].TrimEnd('-', '_', '.').Replace("-unsloth", "", StringComparison.OrdinalIgnoreCase);
            return Fit($"{baseName.Replace('-', ' ')} · {quant.Value.ToUpperInvariant()}", 40);
        }

        return Fit(name.Replace('-', ' '), 40);
    }

    public static string ShortGpu(string name) =>
        Fit(name.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase).Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase), 30);

    /// <summary>"AMD Ryzen 7 9800X3D 8-Core Processor" → "R7 9800X3D".</summary>
    public static string ShortCpu(string name)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(name.Replace("AMD ", "").Replace("Intel(R) ", "").Replace("(TM)", ""), @"\s+\d+-Core Processor", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"Ryzen (\d+)\b", "R$1");
        return Fit(s.Trim(), 30);
    }

    /// <summary>12345 → "12.3K", 1240000 → "1.24M".</summary>
    public static string Tokens(long n) => n switch
    {
        >= 1_000_000_000 => (n / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (n / 1e6).ToString(n >= 100_000_000 ? "0" : n >= 10_000_000 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + "M",
        >= 1000 => (n / 1000.0).ToString(n >= 100_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "K",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    private static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Fit(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static SKTypeface Face(SKFontStyle style)
    {
        foreach (var family in Families.Split(',', StringSplitOptions.TrimEntries))
        {
            var face = SKTypeface.FromFamilyName(family, style);
            if (face is not null && face.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
            {
                return face;
            }
        }

        return SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
    }
}
