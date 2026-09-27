using MagSid.Sid;

namespace MagSid.Sid.Tests;

public class WaveformGeneratorTests
{
    [Fact]
    public void Sawtooth_ramps_linearly_and_wraps()
    {
        var wave = new WaveformGenerator();
        wave.WriteFreqLo(0x00);
        wave.WriteFreqHi(0x10); // freq = 0x1000 = 4096
        wave.WriteControlReg(0x20); // waveform=2 (sawtooth)

        for (int k = 1; k <= 10; k++)
        {
            wave.Clock();
            Assert.Equal(k, wave.Output());
        }
    }

    [Fact]
    public void Pulse_output_follows_the_pulse_width_comparator()
    {
        var wave = new WaveformGenerator();
        wave.WriteFreqLo(0x00);
        wave.WriteFreqHi(0x10); // freq = 4096, so accumulator>>12 increments by 1 each clock
        wave.WritePwLo(0x00);
        wave.WritePwHi(0x08); // pw = 0x800 = 2048

        wave.WriteControlReg(0x40); // waveform=4 (pulse)

        for (int i = 0; i < 2047; i++)
        {
            wave.Clock();
        }
        Assert.Equal(0x000, wave.Output()); // accumulator>>12 == 2047 < pw

        wave.Clock(); // accumulator>>12 == 2048 == pw
        Assert.Equal(0xfff, wave.Output());
    }

    [Fact]
    public void Test_bit_forces_pulse_output_high_regardless_of_pulse_width()
    {
        var wave = new WaveformGenerator();
        wave.WritePwLo(0x00);
        wave.WritePwHi(0x0f); // pw = 0xf00, would normally hold output low for a long time
        wave.WriteControlReg(0x4c); // waveform=4 (pulse), test bit set

        wave.Clock();
        Assert.Equal(0xfff, wave.Output());
    }
}
