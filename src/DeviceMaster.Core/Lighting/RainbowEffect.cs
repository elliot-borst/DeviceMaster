namespace DeviceMaster.Core.Lighting;

/// <summary>How the LEDs are lit: one static colour, or an effect cycling along the chain.</summary>
public enum RgbEffect
{
    Static = 0,
    Rainbow,

    /// <summary>GTA 6: sunset orange, pink, purple and light blue flowing along the chain (#109).</summary>
    Gta6,
}

/// <summary>
/// Pure effect maths shared by every LED family and both apps: a full hue wheel (Rainbow) or the
/// GTA 6 palette (Gta6) spread along the LEDs, rotating over time. Streamed families (Corsair hubs)
/// ask for one frame at a time; families that store and loop an animation themselves (Lian Li
/// SL V3) get a whole cycle.
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
    /// The GTA 6 colours in the order they flow: sunset orange → pink → purple → light blue, then
    /// back the same way, so the loop never jumps from blue straight to orange.
    /// </summary>
    public static readonly (byte R, byte G, byte B)[] Gta6Palette =
    [
        (255, 72, 0),   // sunset orange
        (255, 16, 140), // pink
        (130, 0, 255),  // purple
        (0, 170, 255),  // light blue
    ];

    /// <summary>
    /// One colour for LED families that can't play a custom effect (ENE GPU and RAM controllers,
    /// which only know their own rainbow): the palette's pink, held still.
    /// </summary>
    public static (byte R, byte G, byte B) Gta6StillColor => Gta6Palette[1];

    /// <summary>The colour at <paramref name="position"/> 0–1 round the effect's loop.</summary>
    public static (byte R, byte G, byte B) ColorAt(RgbEffect effect, double position)
    {
        if (effect != RgbEffect.Gta6)
        {
            return HueToRgb(position);
        }

        // there and back along the palette: 0 → orange, 0.5 → light blue, 1 → orange again
        var t = Fraction(position) * 2;
        var along = (t <= 1 ? t : 2 - t) * (Gta6Palette.Length - 1);
        var index = Math.Min((int)Math.Floor(along), Gta6Palette.Length - 2);
        var f = along - index;
        var (a, b) = (Gta6Palette[index], Gta6Palette[index + 1]);
        byte Mix(byte from, byte to) => (byte)Math.Round(from + (to - from) * f);
        return (Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

    /// <summary>
    /// One frame as interleaved R,G,B bytes. <paramref name="phase"/> 0–1 rotates the wheel;
    /// <paramref name="brightness"/> 0–100 scales it toward black like the static colour.
    /// </summary>
    public static byte[] Frame(int ledCount, double phase, int brightness) => Frame(ledCount, phase, brightness, RgbEffect.Rainbow);

    /// <summary>One frame of <paramref name="effect"/> (Rainbow or Gta6); see <see cref="Frame(int, double, int)"/>.</summary>
    public static byte[] Frame(int ledCount, double phase, int brightness, RgbEffect effect)
    {
        var rgb = new byte[Math.Max(ledCount, 0) * 3];
        var scale = Math.Clamp(brightness, 0, 100) / 100.0;
        for (var i = 0; i < ledCount; i++)
        {
            var (r, g, b) = ColorAt(effect, Fraction(phase + (double)i / ledCount));
            rgb[i * 3] = (byte)Math.Round(r * scale);
            rgb[i * 3 + 1] = (byte)Math.Round(g * scale);
            rgb[i * 3 + 2] = (byte)Math.Round(b * scale);
        }

        return rgb;
    }

    /// <summary>A whole loop of <paramref name="frames"/> frames, back to back (for devices that play it themselves).</summary>
    public static byte[] Cycle(int ledCount, int frames, int brightness, RgbEffect effect = RgbEffect.Rainbow)
    {
        var all = new byte[ledCount * 3 * frames];
        for (var f = 0; f < frames; f++)
        {
            Frame(ledCount, (double)f / frames, brightness, effect).CopyTo(all, f * ledCount * 3);
        }

        return all;
    }

    /// <summary>Phase 0–1 for the current moment, so every hub and app shows the same point in the cycle.</summary>
    public static double PhaseAt(long milliseconds, int speed) =>
        Fraction(milliseconds / 1000.0 / CycleSeconds(speed));

    /// <summary>The colour a single-LED device shows right now (VKB head, Aura zones), brightness applied.</summary>
    public static (byte R, byte G, byte B) Now(RgbEffect effect, int speed, int brightness)
    {
        var frame = Frame(1, PhaseAt(Environment.TickCount64, speed), brightness, effect);
        return (frame[0], frame[1], frame[2]);
    }

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
