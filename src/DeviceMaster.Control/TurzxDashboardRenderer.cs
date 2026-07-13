using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using DeviceMaster.Sensors;

namespace DeviceMaster.Control;

/// <summary>
/// Renders the fixed Turzx 8.8" dashboard: a big FPS reading on the LEFT of the ultrawide bar (no
/// label), with a CPU telemetry row across the top-right and a GPU row across the bottom-right, so
/// the FPS sits to the left of both the CPU and GPU names. Each row is five columns —
/// name, usage %, temperature, memory (GB), power (W) — with the chip name coloured (CPU red,
/// GPU green by default) and every other field white. Output is a landscape 1920×480 PNG; the
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
        var cpuColor = ToColor(cpuNameColor ?? ((byte)240, (byte)90, (byte)90));    // red  (AMD)
        var gpuColor = ToColor(gpuNameColor ?? ((byte)80, (byte)220, (byte)120));   // green (NVIDIA)
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

            // left: the FPS number (no label), vertically centred so it sits to the LEFT of both the
            // CPU name (top row) and the GPU name (bottom row). Dim dash when nothing is rendering.
            var fpsColW = width * 0.20f;
            var fpsRect = new RectangleF(0, 0, fpsColW, height);
            if (fps is { } f)
            {
                using var fpsFont = FitFont(g, f.ToString(), height * 0.44f, fpsColW * 0.86f, FontStyle.Bold);
                using var fpsBrush = new SolidBrush(white);
                g.DrawString(f.ToString(), fpsFont, fpsBrush, fpsRect, center);
            }
            else
            {
                using var idleFont = new Font("Segoe UI", height * 0.16f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var idleBrush = new SolidBrush(Color.FromArgb(110, 120, 140));
                g.DrawString("—", idleFont, idleBrush, fpsRect, center);
            }

            // right of the FPS: CPU row (top) and GPU row (bottom) across the remaining width — five
            // columns each (name, usage %, temp, memory GB, power W), aligned vertically.
            var rowsX = fpsColW;
            var rowsW = width - fpsColW;

            // Vertically balanced: equal gap above the CPU row, between the two rows, and below the
            // GPU row. Two 0.26h rows ⇒ 3·gap + 2·0.26 = 1.0 ⇒ gap = 0.16h; CPU at 0.16h, GPU at 0.58h.
            DrawRow(g, center, rowsX, rowsW, height * 0.16f, height * 0.26f,
                Shorten(stats.CpuName, dropLeadingNumbers: true), cpuColor,
                stats.CpuLoadPercent, stats.CpuTempC, stats.RamUsedGb, stats.CpuPowerW, white);

            DrawRow(g, center, rowsX, rowsW, height * 0.58f, height * 0.26f,
                Shorten(stats.GpuName, dropLeadingNumbers: false), gpuColor,
                stats.GpuLoadPercent, stats.GpuTempC, stats.VramUsedGb, stats.GpuPowerW, white);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png); // PNG keeps the small telemetry text crisp
        return stream.ToArray();
    }

    private static void DrawRow(
        Graphics g, StringFormat center, float xLeft, float areaWidth, float yTop, float rowH,
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

        // The name is longer than the metrics, so give its column extra width; then size EVERY cell
        // with one shared font (the largest that fits them all in their own columns) so the name
        // renders at the same size as its metrics instead of being shrunk to a metric-width column.
        var weights = new[] { 1.8f, 1f, 1f, 1f, 1f };
        var totalWeight = weights.Sum();
        var colW = new float[cells.Length];
        var colX = new float[cells.Length];
        var x = xLeft;
        for (var i = 0; i < cells.Length; i++)
        {
            colW[i] = areaWidth * weights[i] / totalWeight;
            colX[i] = x;
            x += colW[i];
        }

        var size = rowH * 0.66f;
        for (var i = 0; i < cells.Length; i++)
        {
            size = Math.Min(size, MaxFitSize(g, cells[i].Text, size, colW[i] * 0.94f));
        }

        using var font = new Font("Segoe UI", Math.Max(size, 10f), FontStyle.Bold, GraphicsUnit.Pixel);
        for (var i = 0; i < cells.Length; i++)
        {
            using var brush = new SolidBrush(cells[i].Color);
            g.DrawString(cells[i].Text, font, brush, new RectangleF(colX[i], yTop, colW[i], rowH), center);
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
        => new("Segoe UI", MaxFitSize(g, text, startSize, maxWidth), style, GraphicsUnit.Pixel);

    /// <summary>Largest bold font size ≤ <paramref name="startSize"/> px whose text fits <paramref name="maxWidth"/>.</summary>
    private static float MaxFitSize(Graphics g, string text, float startSize, float maxWidth)
    {
        var size = Math.Max(startSize, 10f);
        while (size > 12f)
        {
            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            if (string.IsNullOrEmpty(text) || g.MeasureString(text, font).Width <= maxWidth)
            {
                return size;
            }

            size *= 0.92f;
        }

        return 12f;
    }

    private static Color ToColor((byte R, byte G, byte B) c) => Color.FromArgb(c.R, c.G, c.B);
}
