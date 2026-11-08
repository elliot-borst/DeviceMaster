using System.Globalization;
using SkiaSharp;

namespace DeviceMaster.Lcd.Skia;

/// <summary>One serving slot on the dashboard: "idle", "generating", "prefill", … and tokens held.</summary>
public sealed record DashboardSlot(string State, int HeldTokens);

/// <summary>Everything the 8.8" bar shows; any figure may be null (drawn as a dash).</summary>
public sealed record TurzxDashboardData
{
    public string Host { get; init; } = "";

    // local LLM (null block = no LLM server configured)
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
    public long LlmRequests { get; init; }
    public bool LlmReachable { get; init; } = true;

    public string GpuName { get; init; } = "GPU";
    public double? GpuTempC { get; init; }
    public double? GpuUtil { get; init; }
    public double? GpuPowerW { get; init; }
    public double? VramUsedGb { get; init; }
    public double? VramTotalGb { get; init; }

    public string CpuName { get; init; } = "CPU";
    public double? CpuTempC { get; init; }
    public double? CpuLoad { get; init; }
    public double? CpuPowerW { get; init; }
    public double? RamUsedGb { get; init; }
    public double? RamTotalGb { get; init; }

    public int? FanRpm { get; init; }
    public int FanDuty { get; init; }
    public string FanMode { get; init; } = "";
    public bool Failsafe { get; init; }

    public DateTime Now { get; init; } = DateTime.Now;
}

/// <summary>
/// The Turzx 8.8" (1920×480) system dashboard, drawn with SkiaSharp in the DeviceMaster "Thermal"
/// palette: local-LLM activity (state, speed, slots), GPU, CPU/RAM and cooling. Renders landscape and
/// returns the panel's native 480×1920 BGRA frame for <c>TurzxScreen.SendNativeFrame</c>.
/// </summary>
public static class TurzxDashboard
{
    public const int Width = 1920, Height = 480;

    private static readonly SKColor Bg = SKColor.Parse("#0f1115");
    private static readonly SKColor Card = SKColor.Parse("#171a20");
    private static readonly SKColor Line = SKColor.Parse("#2a2f39");
    private static readonly SKColor Text = SKColor.Parse("#eceef2");
    private static readonly SKColor Dim = SKColor.Parse("#a0a6b3");
    private static readonly SKColor Faint = SKColor.Parse("#6b7180");
    private static readonly SKColor Accent = SKColor.Parse("#ff7a45");
    private static readonly SKColor Cool = SKColor.Parse("#4f8cff");
    private static readonly SKColor Warm = SKColor.Parse("#d06cd8");
    private static readonly SKColor Good = SKColor.Parse("#34d399");
    private static readonly SKColor Warn = SKColor.Parse("#fbbf24");
    private static readonly SKColor Danger = SKColor.Parse("#ef4444");

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

        // four cards: LLM (widest) | GPU | CPU + RAM | cooling + clock
        const float pad = 18, gap = 16, top = 18;
        var widths = d.HasLlm ? new float[] { 640, 430, 430, 0 } : new float[] { 0, 600, 600, 0 };
        widths[3] = Width - 2 * pad - gap * (widths.Count(w => w > 0)) - widths.Sum();
        var x = pad;
        var h = Height - 2 * top;
        if (d.HasLlm)
        {
            DrawLlm(canvas, new SKRect(x, top, x + widths[0], top + h), d);
            x += widths[0] + gap;
        }

        DrawGpu(canvas, new SKRect(x, top, x + widths[1], top + h), d);
        x += widths[1] + gap;
        DrawCpu(canvas, new SKRect(x, top, x + widths[2], top + h), d);
        x += widths[2] + gap;
        DrawCooling(canvas, new SKRect(x, top, Width - pad, top + h), d);
        return bitmap;
    }

    // ---------- cards ----------

    private static void DrawLlm(SKCanvas c, SKRect r, TurzxDashboardData d)
    {
        CardShell(c, r, "LOCAL AI", ShortModel(d.LlmModel));
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

        var busy = Math.Max(d.LlmRunning, slots.Count(s => s.State != "idle"));
        var (stateText, stateColor) = !d.LlmReachable ? ("OFFLINE", Danger) : (StateWord(d.LlmState).ToUpperInvariant(), StateColor(d.LlmState));
        var stateWidth = Label(c, stateText, r.Left + 24, r.Top + 108, 26, stateColor, bold: true);
        var detail = d.LlmQueued > 0 ? $"+{d.LlmQueued} waiting" : d.LlmReachable && d.LlmPhase is { Length: > 0 } phase ? Fit(phase, 34) : "";
        if (detail.Length > 0)
        {
            Label(c, detail, r.Left + 40 + stateWidth, r.Top + 108, 22, d.LlmQueued > 0 ? Warn : Dim);
        }

        // big speed + sparkline
        var speed = d.LlmTokPerSec is { } t && t > 0 ? t.ToString("0", CultureInfo.InvariantCulture) : "0";
        var speedWidth = Label(c, speed, r.Left + 24, r.Top + 216, 96, Text, bold: true);
        Label(c, "tok/s", r.Left + 34 + speedWidth, r.Top + 216, 26, Dim);
        Sparkline(c, new SKRect(r.Left + 300, r.Top + 140, r.Right - 24, r.Top + 220), d.LlmTokHistory);

        // slots
        var slotW = (r.Width - 48 - 12 * (slots.Count - 1)) / slots.Count;
        for (var i = 0; i < slots.Count; i++)
        {
            var sx = r.Left + 24 + i * (slotW + 12);
            var sr = new SKRect(sx, r.Bottom - 150, sx + slotW, r.Bottom - 24);
            var active = slots[i].State != "idle";
            var tint = StateColor(slots[i].State);
            using (var fill = new SKPaint { Color = active ? tint.WithAlpha(40) : SKColor.Parse("#0c0e12"), IsAntialias = true })
            {
                c.DrawRoundRect(sr, 12, 12, fill);
            }

            using (var border = new SKPaint { Color = active ? tint : Line, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
            {
                c.DrawRoundRect(sr, 12, 12, border);
            }

            Label(c, $"SLOT {i + 1}", sr.Left + 16, sr.Top + 34, 18, Faint, bold: true);
            Label(c, StateWord(slots[i].State), sr.Left + 16, sr.Top + 72, 24, active ? tint : Dim, bold: active);
            Label(c, slots[i].HeldTokens > 0 ? Tokens(slots[i].HeldTokens) + " ctx" : "", sr.Left + 16, sr.Top + 106, 18, Faint);
        }

        // above the slot pills: how many conversations are being worked on, and model calls since the server started
        Label(c, $"{busy}/{slots.Count} busy · {d.LlmRequests:N0} model calls since start", r.Left + 24, r.Bottom - 162, 18, Faint);
    }

    private static void DrawGpu(SKCanvas c, SKRect r, TurzxDashboardData d)
    {
        CardShell(c, r, "GPU", ShortGpu(d.GpuName));
        BigReading(c, r, d.GpuTempC is { } t ? $"{t:0}°" : "—", TempColor(d.GpuTempC, 70, 83));
        Bar(c, r, 0, "LOAD", d.GpuUtil, 100, d.GpuUtil is { } u ? $"{u:0}%" : "—", d.GpuPowerW is { } p ? $"{p:0} W" : "");
        Bar(c, r, 1, "VRAM", d.VramUsedGb, d.VramTotalGb, d.VramUsedGb is { } v ? $"{v:0.0} GB" : "—", d.VramTotalGb is { } vt ? $"of {vt:0}" : "");
    }

    private static void DrawCpu(SKCanvas c, SKRect r, TurzxDashboardData d)
    {
        CardShell(c, r, "CPU", ShortCpu(d.CpuName));
        BigReading(c, r, d.CpuTempC is { } t ? $"{t:0}°" : "—", TempColor(d.CpuTempC, 75, 88));
        Bar(c, r, 0, "LOAD", d.CpuLoad, 100, d.CpuLoad is { } l ? $"{l:0}%" : "—", d.CpuPowerW is { } p ? $"{p:0} W" : "");
        Bar(c, r, 1, "RAM", d.RamUsedGb, d.RamTotalGb, d.RamUsedGb is { } u ? $"{u:0} GB" : "—", d.RamTotalGb is { } rt ? $"of {rt:0}" : "");
    }

    private static void DrawCooling(SKCanvas c, SKRect r, TurzxDashboardData d)
    {
        CardShell(c, r, "COOLING", d.Failsafe ? "FAILSAFE — fans 100%" : d.FanMode);
        var rpm = d.FanRpm is { } f ? f.ToString("N0", CultureInfo.InvariantCulture) : "—";
        var w = Label(c, rpm, r.Left + 24, r.Top + 196, 72, d.Failsafe ? Danger : Text, bold: true);
        Label(c, "rpm", r.Left + 32 + w, r.Top + 196, 24, Dim);
        Label(c, $"fans {d.FanDuty}%", r.Left + 24, r.Top + 240, 24, Accent, bold: true);

        Label(c, d.Now.ToString("HH:mm", CultureInfo.InvariantCulture), r.Left + 24, r.Bottom - 56, 64, Text, bold: true);
        Label(c, d.Now.ToString("ddd d MMM", CultureInfo.InvariantCulture) + (d.Host.Length > 0 ? " · " + d.Host : ""),
            r.Left + 24, r.Bottom - 22, 20, Faint);
    }

    // ---------- pieces ----------

    private static void CardShell(SKCanvas c, SKRect r, string title, string subtitle)
    {
        using (var fill = new SKPaint { Color = Card, IsAntialias = true })
        {
            c.DrawRoundRect(r, 18, 18, fill);
        }

        using (var border = new SKPaint { Color = Line, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
        {
            c.DrawRoundRect(r, 18, 18, border);
        }

        Label(c, title, r.Left + 24, r.Top + 44, 22, Faint, bold: true);
        Label(c, Fit(subtitle, 34), r.Left + 24, r.Top + 74, 22, Dim);
    }

    private static void BigReading(SKCanvas c, SKRect r, string value, SKColor color) =>
        Label(c, value, r.Left + 24, r.Top + 196, 92, color, bold: true);

    /// <summary>Labelled progress bar; row 0 or 1 in the card's lower half.</summary>
    private static void Bar(SKCanvas c, SKRect r, int row, string label, double? value, double? max, string valueText, string extra)
    {
        var y = r.Top + 262 + row * 80;
        Label(c, label, r.Left + 24, y, 18, Faint, bold: true);
        Label(c, valueText + (extra.Length > 0 ? "  " + extra : ""), r.Right - 24, y, 20, Dim, align: SKTextAlign.Right);
        var track = new SKRect(r.Left + 24, y + 16, r.Right - 24, y + 32);
        using (var bg = new SKPaint { Color = SKColor.Parse("#0c0e12"), IsAntialias = true })
        {
            c.DrawRoundRect(track, 8, 8, bg);
        }

        if (value is { } v && max is > 0)
        {
            var fraction = (float)Math.Clamp(v / max.Value, 0, 1);
            if (fraction > 0.005f)
            {
                var fillRect = new SKRect(track.Left, track.Top, track.Left + track.Width * fraction, track.Bottom);
                using var shader = SKShader.CreateLinearGradient(new SKPoint(track.Left, 0), new SKPoint(track.Right, 0),
                    [Cool, Warm, Accent], [0f, 0.5f, 1f], SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = shader, IsAntialias = true };
                c.DrawRoundRect(fillRect, 8, 8, paint);
            }
        }
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

        using var shader = SKShader.CreateLinearGradient(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0),
            [Cool, Warm, Accent], [0f, 0.5f, 1f], SKShaderTileMode.Clamp);
        using var stroke = new SKPaint { Shader = shader, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, StrokeJoin = SKStrokeJoin.Round };
        c.DrawPath(path, stroke);
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

    private static SKColor StateColor(string state) => state switch
    {
        "idle" or "offline" => Faint,
        "generating" or "decode" or "decoding" => Good,
        "prefill" or "prefilling" or "reading" => Warn,
        _ => Accent,
    };

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

    public static string ShortCpu(string name) =>
        Fit(System.Text.RegularExpressions.Regex.Replace(name.Replace("AMD ", "").Replace("Intel(R) ", "").Replace("(TM)", ""), @"\s+\d+-Core Processor", "").Trim(), 30);

    /// <summary>12345 → "12.3K".</summary>
    public static string Tokens(int n) => n >= 1000 ? (n / 1000.0).ToString(n >= 100_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "K" : n.ToString(CultureInfo.InvariantCulture);

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
