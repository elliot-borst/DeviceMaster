using DeviceMaster.Core.Lighting;

namespace DeviceMaster.Core.Tests;

public class RainbowEffectTests
{
    [Theory]
    [InlineData(0.0, 255, 0, 0)]
    [InlineData(1 / 6.0, 255, 255, 0)]
    [InlineData(1 / 3.0, 0, 255, 0)]
    [InlineData(0.5, 0, 255, 255)]
    [InlineData(2 / 3.0, 0, 0, 255)]
    [InlineData(5 / 6.0, 255, 0, 255)]
    [InlineData(1.0, 255, 0, 0)]
    public void HueWheelHitsThePrimaries(double hue, int r, int g, int b) =>
        Assert.Equal(((byte)r, (byte)g, (byte)b), RainbowEffect.HueToRgb(hue));

    [Fact]
    public void FrameSpreadsTheWheelAlongTheLeds()
    {
        var frame = RainbowEffect.Frame(6, phase: 0, brightness: 100);
        Assert.Equal(18, frame.Length);
        Assert.Equal(new byte[] { 255, 0, 0 }, frame[0..3]);     // LED 0 red
        Assert.Equal(new byte[] { 0, 255, 255 }, frame[9..12]);  // LED 3 (half way) cyan
    }

    [Fact]
    public void BrightnessScalesTowardBlack()
    {
        Assert.Equal(new byte[] { 128, 0, 0 }, RainbowEffect.Frame(1, 0, 50));
        Assert.All(RainbowEffect.Frame(10, 0.3, 0), v => Assert.Equal(0, v));
    }

    [Fact]
    public void CycleLoopsSeamlessly()
    {
        const int leds = 40, frames = 36;
        var cycle = RainbowEffect.Cycle(leds, frames, 100);
        Assert.Equal(leds * 3 * frames, cycle.Length);
        Assert.Equal(RainbowEffect.Frame(leds, 0, 100), cycle[..(leds * 3)]);
        // the frame after the last is frame 0 again (within one step of float rounding)
        var wrapped = RainbowEffect.Frame(leds, frames / (double)frames, 100);
        var first = RainbowEffect.Frame(leds, 0, 100);
        Assert.All(Enumerable.Range(0, first.Length), i => Assert.InRange(Math.Abs(first[i] - wrapped[i]), 0, 1));
    }

    [Fact]
    public void SpeedIsClampedAndFasterMeansShorterCycles()
    {
        Assert.Equal(RainbowEffect.CycleSeconds(1), RainbowEffect.CycleSeconds(-3));
        Assert.Equal(RainbowEffect.CycleSeconds(5), RainbowEffect.CycleSeconds(99));
        Assert.True(RainbowEffect.CycleSeconds(5) < RainbowEffect.CycleSeconds(1));
        Assert.Equal(0.5, RainbowEffect.PhaseAt(3500, 3), 6); // half of the 7 s cycle
    }

    // ---- GTA 6 (#109) ----

    [Fact]
    public void Gta6WalksThePaletteThereAndBack()
    {
        var p = RainbowEffect.Gta6Palette;
        var steps = 2 * (p.Length - 1); // up the stripes and back down
        for (var i = 0; i < p.Length; i++)
        {
            Assert.Equal(p[i], RainbowEffect.ColorAt(RgbEffect.Gta6, (double)i / steps));
            Assert.Equal(p[i], RainbowEffect.ColorAt(RgbEffect.Gta6, 1 - (double)i / steps));
        }

        Assert.Equal(p[^1], RainbowEffect.ColorAt(RgbEffect.Gta6, 0.5)); // the top stripe, blue
    }

    [Fact]
    public void Gta6NeverJumpsBetweenNeighbouringLeds()
    {
        // 120 LEDs over one loop: no channel moves more than a small step from one LED to the next
        var frame = RainbowEffect.Frame(120, 0.37, 100, RgbEffect.Gta6);
        for (var i = 3; i < frame.Length; i++)
        {
            Assert.InRange(Math.Abs(frame[i] - frame[i - 3]), 0, 16);
        }
    }

    [Fact]
    public void Gta6StaysInsideThePaletteColours()
    {
        // every colour is a blend of two neighbouring stripes: green is never the strongest channel
        for (var k = 0; k < 200; k++)
        {
            var (r, g, b) = RainbowEffect.ColorAt(RgbEffect.Gta6, k / 200.0);
            Assert.True(g < Math.Max(r, b), $"greenish {r},{g},{b} at {k}");
        }
    }

    [Fact]
    public void Gta6CycleAndBrightnessWorkLikeRainbow()
    {
        var cycle = RainbowEffect.Cycle(9, 12, 100, RgbEffect.Gta6);
        Assert.Equal(RainbowEffect.Frame(9, 0, 100, RgbEffect.Gta6), cycle[..27]);
        Assert.All(RainbowEffect.Frame(9, 0.2, 0, RgbEffect.Gta6), v => Assert.Equal(0, v));
        Assert.Equal(RainbowEffect.Frame(9, 0.2, 100), RainbowEffect.Frame(9, 0.2, 100, RgbEffect.Rainbow));
    }

    [Fact]
    public void Gta6ChainNeverMirrorsItself()
    {
        // #111: 13 fans × 8 LEDs on one hub — the fan k from the start must not match the fan k from the end
        const int fans = 13, perFan = 8, leds = fans * perFan;
        foreach (var phase in new[] { 0.0, 0.13, 0.5, 0.77 })
        {
            var frame = RainbowEffect.Frame(leds, phase, 100, RgbEffect.Gta6);
            (byte, byte, byte) Fan(int k) { var o = (k * perFan + perFan / 2) * 3; return (frame[o], frame[o + 1], frame[o + 2]); }
            for (var k = 0; k < fans / 2; k++)
            {
                Assert.NotEqual(Fan(k), Fan(fans - 1 - k));
            }
        }
    }
}
