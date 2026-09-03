using SkiaSharp;

namespace DeviceMaster.Lcd.Skia;

/// <summary>
/// Cross-platform (SkiaSharp) JPEG frame rendering for the round LCD screens.
///
/// The Windows app renders with System.Drawing (LcdFrames / LcdMetricRenderer in
/// DeviceMaster.Control). System.Drawing.Common is Windows-only on .NET 9, so the
/// headless Linux loop renders through this project instead. The layout math mirrors
/// the System.Drawing renderer exactly (boxes, fit-to-width font, rotation) so both
/// platforms paint identical screens.
/// </summary>
public static class SkiaLcdFrames
{
    private static readonly Dictionary<(int W, int H, byte R, byte G, byte B), byte[]> Cache = [];
    private static readonly object Gate = new();

    public static byte[] Solid(int width, int height, byte r, byte g, byte b)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue((width, height, r, g, b), out var cached))
            {
                return cached;
            }

            using var bitmap = new SKBitmap(width, height);
            using var paint = new SKPaint { Color = new SKColor(r, g, b), IsAntialias = true };
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(paint.Color);
            }

            var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
            Cache[(width, height, r, g, b)] = jpeg;
            return jpeg;
        }
    }
}

/// <summary>
/// Renders a single metric (label, big value, unit line) as a JPEG frame for the round LCD
/// screens. Text is auto-sized to fill the panel; rotation happens at render time so both
/// screen families behave identically. Frames are cached by their full content key, so an
/// unchanged value costs nothing and identical fan screens share one encode.
/// </summary>
public static class SkiaLcdMetricRenderer
{
    private static readonly Dictionary<string, byte[]> Cache = [];
    private static readonly object Gate = new();

    /// <summary>
    /// Font family chain: the Windows face first (used when running there), then families
    /// that exist on the slim Linux base (fonts-dejavu-core), then a generic sans fallback.
    /// </summary>
    private const string FontFamilies = "Segoe UI, DejaVu Sans, Liberation Sans, sans-serif";

    public static byte[] Render(
        int width, int height, string label, string value, string unit,
        (byte R, byte G, byte B) accent, int rotationDegrees = 0,
        (byte R, byte G, byte B)? background = null)
    {
        var bg = background ?? ((byte)0, (byte)0, (byte)0);
        var key = $"{width}x{height}|{label}|{value}|{unit}|{accent.R},{accent.G},{accent.B}|{rotationDegrees}|{bg}";
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

            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(bg.R, bg.G, bg.B));

            var accentColor = new SKColor(accent.R, accent.G, accent.B);

            // label/unit: light gray on dark backgrounds, same as the value color otherwise
            var dim = bg.R + bg.G + bg.B < 120
                ? new SKColor(176, 186, 205)
                : accentColor;

            // round panels lose the corners — keep text inside ~78% of the width
            var safeWidth = width * 0.78f;

            using var labelPaint = FitPaint(label, height * 0.135f, safeWidth);
            DrawCentered(canvas, label, labelPaint, dim, 0, height * 0.13f, width, height * 0.16f);

            using var valuePaint = FitPaint(value, height * 0.42f, safeWidth);
            DrawCentered(canvas, value, valuePaint, accentColor, 0, height * 0.28f, width, height * 0.44f);

            // unit line matches the label line: same size, same bold weight
            using var unitPaint = FitPaint(unit, height * 0.135f, safeWidth);
            DrawCentered(canvas, unit, unitPaint, dim, 0, height * 0.70f, width, height * 0.16f);

            var angle = ((rotationDegrees % 360) + 360) % 360;
            SKBitmap? rotated = null;
            if (angle is 90 or 180 or 270)
            {
                rotated = Rotate(bitmap, angle);
            }

            var jpeg = (rotated ?? (SKBitmap)bitmap).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
            rotated?.Dispose();
            Cache[key] = jpeg;
            return jpeg;
        }
    }

    private static void DrawCentered(SKCanvas canvas, string text, SKPaint paint, SKColor color,
        float x, float y, float w, float h)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        paint.Color = color;
        var bounds = new SKRect();
        paint.MeasureText(text, ref bounds);
        var originX = x + (w - bounds.Width) / 2f;
        var originY = y + h / 2f - (bounds.Top + bounds.Bottom) / 2f;
        canvas.DrawText(text, originX, originY, paint);
    }

    /// <summary>Largest font at most <paramref name="startSize"/> px whose text fits the safe width.</summary>
    private static SKPaint FitPaint(string text, float startSize, float maxWidth)
    {
        var size = Math.Max(startSize, 8f);
        var typeface = SKTypeface.FromFamilyName(FontFamilies, SKFontStyle.Bold) ?? SKTypeface.Default;
        while (size > 10f)
        {
            var paint = new SKPaint
            {
                Typeface = typeface,
                TextSize = size,
                IsAntialias = true,
            };
            if (string.IsNullOrEmpty(text) || paint.MeasureText(text) <= maxWidth)
            {
                return paint;
            }

            paint.Dispose();
            size *= 0.92f;
        }

        return new SKPaint
        {
            Typeface = typeface,
            TextSize = 10f,
            IsAntialias = true,
        };
    }

    // SKBitmap has no in-place rotate; render the rotated result into a fresh bitmap.
    private static SKBitmap Rotate(SKBitmap source, int degrees)
    {
        var w = source.Width;
        var h = source.Height;
        var target = new SKBitmap(w, h);
        using var canvas = new SKCanvas(target);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees(degrees);
        canvas.DrawBitmap(source, -w / 2f, -h / 2f);
        return target;
    }
}
