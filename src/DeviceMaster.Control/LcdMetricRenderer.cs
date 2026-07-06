using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace DeviceMaster.Control;

/// <summary>
/// Renders a single metric (label, big value, unit line, accent ring) as a JPEG frame for
/// the round LCD screens. Frames are cached by their full content key, so a value that
/// hasn't changed costs nothing and identical fan screens share one encode.
/// </summary>
public static class LcdMetricRenderer
{
    private static readonly Dictionary<string, byte[]> Cache = [];
    private static readonly object Gate = new();

    public static byte[] Render(int width, int height, string label, string value, string unit, (byte R, byte G, byte B) accent)
    {
        var key = $"{width}x{height}|{label}|{value}|{unit}|{accent.R},{accent.G},{accent.B}";
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
                using var ringPen = new Pen(Color.FromArgb(160, accentColor), Math.Max(4f, width * 0.015f));
                var inset = width * 0.035f;
                g.DrawEllipse(ringPen, inset, inset, width - 2 * inset, height - 2 * inset);

                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var dim = new SolidBrush(Color.FromArgb(150, 160, 180));

                using var labelFont = new Font("Segoe UI", height * 0.085f, FontStyle.Regular, GraphicsUnit.Pixel);
                g.DrawString(label, labelFont, dim, new RectangleF(0, height * 0.15f, width, height * 0.12f), format);

                using var valueFont = new Font("Segoe UI", height * 0.30f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var valueBrush = new SolidBrush(accentColor);
                g.DrawString(value, valueFont, valueBrush, new RectangleF(0, height * 0.32f, width, height * 0.36f), format);

                using var unitFont = new Font("Segoe UI", height * 0.075f, FontStyle.Regular, GraphicsUnit.Pixel);
                g.DrawString(unit, unitFont, dim, new RectangleF(0, height * 0.70f, width, height * 0.12f), format);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Jpeg);
            var jpeg = stream.ToArray();
            Cache[key] = jpeg;
            return jpeg;
        }
    }
}
