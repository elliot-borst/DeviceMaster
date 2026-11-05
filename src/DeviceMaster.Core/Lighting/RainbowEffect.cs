namespace DeviceMaster.Core.Lighting;

/// <summary>How the LEDs are lit: one static colour, or a rainbow cycling along the chain.</summary>
public enum RgbEffect
{
    Static = 0,
    Rainbow,
}

/// <summary>
/// Pure rainbow maths shared by every LED family and both apps: a full hue wheel spread along
/// the LEDs, rotating over time. Streamed families (Corsair hubs) ask for one frame at a time;
/// families that store and loop an animation themselves (Lian Li SL V3) get a whole cycle.
/// </summary>
public static class RainbowEffect
{
    public const int MinSpeed = 1, MaxSpeed = 5, DefaultSpeed = 3;

    /// <summary>Seconds for one full trip round the colour wheel, by speed 1 (slow) … 5 (fast).</summary>
    public static double CycleSeconds(int speed) => Math.Clamp(speed, MinSpeed, MaxSpeed) switch
    {
        1 => 20,
        2 => 12,
        3 => 7,
        4 => 4,
        _ => 2,
    };

    /// <summary>
    /// One frame as interleaved R,G,B bytes. <paramref name="phase"/> 0–1 rotates the wheel;
    /// <paramref name="brightness"/> 0–100 scales it toward black like the static colour.
    /// </summary>
    public static byte[] Frame(int ledCount, double phase, int brightness)
    {
        var rgb = new byte[Math.Max(ledCount, 0) * 3];
        var scale = Math.Clamp(brightness, 0, 100) / 100.0;
        for (var i = 0; i < ledCount; i++)
        {
            var hue = Fraction(phase + (double)i / ledCount);
            var (r, g, b) = HueToRgb(hue);
            rgb[i * 3] = (byte)Math.Round(r * scale);
            rgb[i * 3 + 1] = (byte)Math.Round(g * scale);
            rgb[i * 3 + 2] = (byte)Math.Round(b * scale);
        }

        return rgb;
    }

    /// <summary>A whole loop of <paramref name="frames"/> frames, back to back (for devices that play it themselves).</summary>
    public static byte[] Cycle(int ledCount, int frames, int brightness)
    {
        var all = new byte[ledCount * 3 * frames];
        for (var f = 0; f < frames; f++)
        {
            Frame(ledCount, (double)f / frames, brightness).CopyTo(all, f * ledCount * 3);
        }

        return all;
    }

    /// <summary>Phase 0–1 for the current moment, so every hub and app shows the same point in the cycle.</summary>
    public static double PhaseAt(long milliseconds, int speed) =>
        Fraction(milliseconds / 1000.0 / CycleSeconds(speed));

    /// <summary>Fully saturated, full-value hue (0–1) to 8-bit RGB.</summary>
    public static (byte R, byte G, byte B) HueToRgb(double hue)
    {
        var h = Fraction(hue) * 6;
        var sector = (int)Math.Floor(h);
        var t = h - sector;
        byte Up() => (byte)Math.Round(255 * t);
        byte Down() => (byte)Math.Round(255 * (1 - t));
        return sector switch
        {
            0 => (255, Up(), 0),
            1 => (Down(), 255, 0),
            2 => (0, 255, Up()),
            3 => (0, Down(), 255),
            4 => (Up(), 0, 255),
            _ => (255, 0, Down()),
        };
    }

    private static double Fraction(double x) => x - Math.Floor(x);
}
