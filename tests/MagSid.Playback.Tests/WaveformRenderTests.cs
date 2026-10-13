using MagSid.Sid;

namespace MagSid.Playback.Tests;

/// <summary>
/// The oscillator strip draws reSID's own output. A pulse is high while the oscillator's top
/// twelve bits are at or above the width, so width n of 16 (the instrument's nibble) is high for
/// 1 - n/16 of the period - on the chip, and so in the drawing.
/// </summary>
public class WaveformRenderTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(14)]
    public void A_pulse_is_drawn_with_the_duty_its_width_gives_it(int width)
    {
        foreach (var model in new[] { ChipModel.Mos6581, ChipModel.Mos8580 })
        {
            var samples = WaveformRender.Render(0x41, width, model, 1024);
            double high = samples.Count(v => v > 0) / (double)samples.Length;

            Assert.InRange(high, 1 - (width / 16.0) - 0.02, 1 - (width / 16.0) + 0.02);
        }
    }

    [Fact]
    public void Silence_and_a_width_of_zero_draw_a_flat_line()
    {
        Assert.All(WaveformRender.Render(0x09, 8, ChipModel.Mos8580, 256), v => Assert.Equal(0f, v, 0.01f));
        Assert.All(WaveformRender.Render(0x41, 0, ChipModel.Mos8580, 256), v => Assert.Equal(0f, v, 0.01f));
    }

    [Fact]
    public void Nothing_is_drawn_past_the_strip()
    {
        foreach (byte control in new byte[] { 0x11, 0x21, 0x41, 0x51, 0x61, 0x81 })
            Assert.All(WaveformRender.Render(control, 8, ChipModel.Mos8580, 300), v => Assert.InRange(v, -1f, 1f));
    }
}
