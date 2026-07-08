using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using DeviceMaster.Sensors;

namespace DeviceMaster.Control;

/// <summary>
/// Renders the fixed Turzx 8.8" dashboard: a big FPS reading centred on the ultrawide bar with a
/// CPU telemetry row across the top and a GPU row across the bottom. Each row is five columns —
/// name, usage %, temperature, memory (GB), power (W) — with the chip name coloured (CPU green,
/// GPU red by default) and every other field white. Output is a landscape 1920×480 PNG; the
/// panel-rotation onto the native portrait framebuffer is done by <c>TurzxScreen</c>, so this
/// draws upright landscape only. Pure/deterministic — safe to unit-test.
/// </summary>
public static class TurzxDashboardRenderer
{
    private static readonly HashSet<string> BrandWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AMD", "Intel", "NVIDIA", "GeForce", "Radeon", "Ryzen", "Core",
        "Processor", "CPU", "GPU", "(R)", "(TM)", "with", "w/", "Graphics",
    };

    public static byte[] Render(
        int width, int height, SystemStats stats, int? fps,
        (byte R, byte G, byte B)? cpuNameColor = null, (byte R, byte G, byte B)? gpuNameColor = null)
    {
        var cpuColor = ToColor(cpuNameColor ?? ((byte)80, (byte)220, (byte)120));   // green
        var gpuColor = ToColor(gpuNameColor ?? ((byte)240, (byte)90, (byte)90));    // red
        var white = Color.FromArgb(236, 238, 244);

        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Black);

            using var center = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            // top: CPU row, bottom: GPU row (5 columns each, aligned vertically)
            DrawRow(g, center, width, height * 0.04f, height * 0.26f,
                Shorten(stats.CpuName, dropLeadingNumbers: true), cpuColor,
                stats.CpuLoadPercent, stats.CpuTempC, stats.RamUsedGb, stats.CpuPowerW, white);

            DrawRow(g, center, width, height * 0.70f, height * 0.26f,
                Shorten(stats.GpuName, dropLeadingNumbers: false), gpuColor,
                stats.GpuLoadPercent, stats.GpuTempC, stats.VramUsedGb, stats.GpuPowerW, white);

            // middle: the FPS hero — a big number dead-centre of the panel, no label. Real reading
            // is big and white; when nothing is rendering, a small dim dash instead of a huge bar.
            var fpsRect = new RectangleF(0, height * 0.30f, width, height * 0.40f);
            if (fps is { } f)
            {
                using var fpsFont = FitFont(g, f.ToString(), height * 0.40f, width * 0.55f, FontStyle.Bold);
                using var fpsBrush = new SolidBrush(white);
                g.DrawString(f.ToString(), fpsFont, fpsBrush, fpsRect, center);
            }
            else
            {
                using var idleFont = new Font("Segoe UI", height * 0.14f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var idleBrush = new SolidBrush(Color.FromArgb(110, 120, 140));
                g.DrawString("—", idleFont, idleBrush, fpsRect, center);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png); // PNG keeps the small telemetry text crisp
        return stream.ToArray();
    }

    private static void DrawRow(
        Graphics g, StringFormat center, int width, float yTop, float rowH,
        string name, Color nameColor,
        double? load, double? temp, double? mem, double? power, Color white)
    {
        var cells = new (string Text, Color Color)[]
        {
            (name, nameColor),
            (FmtOrDash(load, "%"), white),
            (FmtOrDash(temp, "°C"), white),
            (FmtOrDash(mem, "GB"), white),
            (FmtOrDash(power, "W"), white),
        };

        var colW = width / 5f;
        for (var i = 0; i < cells.Length; i++)
        {
            using var font = FitFont(g, cells[i].Text, rowH * 0.66f, colW * 0.94f, FontStyle.Bold);
            using var brush = new SolidBrush(cells[i].Color);
            g.DrawString(cells[i].Text, font, brush, new RectangleF(i * colW, yTop, colW, rowH), center);
        }
    }

    private static string FmtOrDash(double? value, string suffix) =>
        value is { } v ? $"{v:F0}{suffix}" : "—";

    /// <summary>Strips vendor/brand words (and, for CPUs, a leading tier number) to a short model.</summary>
    private static string Shorten(string name, bool dropLeadingNumbers)
    {
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !BrandWords.Contains(t))
            .ToList();

        if (dropLeadingNumbers)
        {
            while (tokens.Count > 1 && tokens[0].All(char.IsDigit))
            {
                tokens.RemoveAt(0);
            }
        }

        var result = string.Join(" ", tokens).Trim();
        return result.Length == 0 ? name : result;
    }

    /// <summary>Largest font ≤ <paramref name="startSize"/> px whose text fits <paramref name="maxWidth"/>.</summary>
    private static Font FitFont(Graphics g, string text, float startSize, float maxWidth, FontStyle style)
    {
        var size = Math.Max(startSize, 10f);
        while (size > 12f)
        {
            var font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
            if (string.IsNullOrEmpty(text) || g.MeasureString(text, font).Width <= maxWidth)
            {
                return font;
            }

            font.Dispose();
            size *= 0.92f;
        }

        return new Font("Segoe UI", 12f, style, GraphicsUnit.Pixel);
    }

    private static Color ToColor((byte R, byte G, byte B) c) => Color.FromArgb(c.R, c.G, c.B);
}
