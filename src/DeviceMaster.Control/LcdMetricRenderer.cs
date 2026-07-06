using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace DeviceMaster.Control;

/// <summary>
/// Renders a single metric (label, big value, unit line) as a JPEG frame for the round LCD
/// screens. Text is auto-sized to fill the panel; rotation happens at render time so both
/// screen families behave identically. Frames are cached by their full content key, so an
/// unchanged value costs nothing and identical fan screens share one encode.
/// </summary>
public static class LcdMetricRenderer
{
    private static readonly Dictionary<string, byte[]> Cache = [];
    private static readonly object Gate = new();

    public static byte[] Render(
        int width, int height, string label, string value, string unit,
        (byte R, byte G, byte B) accent, int rotationDegrees = 0)
    {
        var key = $"{width}x{height}|{label}|{value}|{unit}|{accent.R},{accent.G},{accent.B}|{rotationDegrees}";
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (Cache.Count > 128)
            {
                Cache.Clear(); // metrics churn through values — keep the cache bounded
            }

            using var bitmap = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Black);

                var accentColor = Color.FromArgb(accent.R, accent.G, accent.B);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var dim = new SolidBrush(Color.FromArgb(176, 186, 205));

                // round panels lose the corners — keep text inside ~78% of the width
                var safeWidth = width * 0.78f;

                using var labelFont = FitFont(g, label, height * 0.135f, safeWidth, FontStyle.Bold);
                g.DrawString(label, labelFont, dim, new RectangleF(0, height * 0.13f, width, height * 0.16f), format);

                using var valueFont = FitFont(g, value, height * 0.42f, safeWidth, FontStyle.Bold);
                using var valueBrush = new SolidBrush(accentColor);
                g.DrawString(value, valueFont, valueBrush, new RectangleF(0, height * 0.28f, width, height * 0.44f), format);

                using var unitFont = FitFont(g, unit, height * 0.115f, safeWidth, FontStyle.Regular);
                g.DrawString(unit, unitFont, dim, new RectangleF(0, height * 0.70f, width, height * 0.16f), format);
            }

            switch (((rotationDegrees % 360) + 360) % 360)
            {
                case 90:
                    bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    break;
                case 180:
                    bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    break;
                case 270:
                    bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);
                    break;
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Jpeg);
            var jpeg = stream.ToArray();
            Cache[key] = jpeg;
            return jpeg;
        }
    }

    /// <summary>Largest font at most <paramref name="startSize"/> px whose text fits the safe width.</summary>
    private static Font FitFont(Graphics g, string text, float startSize, float maxWidth, FontStyle style)
    {
        var size = Math.Max(startSize, 8f);
        while (size > 10f)
        {
            var font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
            if (string.IsNullOrEmpty(text) || g.MeasureString(text, font).Width <= maxWidth)
            {
                return font;
            }

            font.Dispose();
            size *= 0.92f;
        }

        return new Font("Segoe UI", 10f, style, GraphicsUnit.Pixel);
    }
}
