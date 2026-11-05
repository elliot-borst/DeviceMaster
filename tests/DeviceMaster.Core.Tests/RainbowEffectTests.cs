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
}
