using System.Drawing;
using System.Drawing.Imaging;

namespace DeviceMaster.Control;

/// <summary>Solid-color JPEG frames for the LCD screens, cached per size and color.</summary>
public static class LcdFrames
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

            using var bitmap = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(r, g, b));
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Jpeg);
            var jpeg = stream.ToArray();
            Cache[(width, height, r, g, b)] = jpeg;
            return jpeg;
        }
    }
}
