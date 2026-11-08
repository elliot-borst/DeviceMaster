using System.Globalization;
using SkiaSharp;

namespace DeviceMaster.Lcd.Skia;

/// <summary>Everything the 8.8" bar shows; any figure may be null (drawn as a dash).</summary>
public sealed record TurzxDashboardData
{
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
/// palette: GPU and CPU/RAM, each with its cooler.
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
    private static readonly SKColor Warn = SKColor.Parse("#fbbf24");
    private static readonly SKColor Danger = SKColor.Parse("#ef4444");

    // colours for the card names: devices in their maker's colour
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

        // two cards: GPU | CPU, sharing the width equally
        const float pad = 18, gap = 16, top = 18;
        var bottom = Height - top;
        var cardWidth = (Width - 2 * pad - gap) / 2;
        DrawGpu(canvas, new SKRect(pad, top, pad + cardWidth, bottom), d);
        DrawCpu(canvas, new SKRect(pad + cardWidth + gap, top, Width - pad, bottom), d);
        return bitmap;
    }

    // ---------- cards ----------

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

    /// <summary>The maker's brand colour for a CPU/GPU name (NVIDIA green, AMD red, Intel blue).</summary>
    public static SKColor VendorColor(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"(?i)nvidia|geforce|\b(rtx|gtx|quadro)\b") ? Nvidia
        : System.Text.RegularExpressions.Regex.IsMatch(name, @"(?i)\bamd\b|ryzen|radeon|epyc|threadripper") ? Amd
        : System.Text.RegularExpressions.Regex.IsMatch(name, @"(?i)intel|\bcore\b|\barc\b|xeon") ? Intel
        : Text;

    private static SKColor TempColor(double? t, double warn, double hot) =>
        t is not { } v ? Faint : v >= hot ? Danger : v >= warn ? Warn : Text;

    // ---------- text helpers ----------

    public static string ShortGpu(string name) =>
        Fit(name.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase).Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase), 30);

    /// <summary>"AMD Ryzen 7 9800X3D 8-Core Processor" → "R7 9800X3D".</summary>
    public static string ShortCpu(string name)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(name.Replace("AMD ", "").Replace("Intel(R) ", "").Replace("(TM)", ""), @"\s+\d+-Core Processor", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"Ryzen (\d+)\b", "R$1");
        return Fit(s.Trim(), 30);
    }

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
