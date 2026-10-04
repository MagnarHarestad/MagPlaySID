namespace MagSid.Sid.Tests;

/// <summary>
/// The floating waveform DAC: a waveform switched off holds the value it was putting out, then
/// fades, instead of dropping to 0 at once. Only the default - switched on - is tested here; the
/// switch is static, and flipping it in one test would change what every other test running
/// alongside it hears.
/// </summary>
public class FloatingWaveformTests
{
    private static WaveformGenerator TriangleRunning(ChipModel model)
    {
        var wave = new WaveformGenerator();
        wave.SetChipModel(model);
        wave.WriteFreqHi(0x10);
        wave.WriteControlReg(0x11);     // triangle, gate
        wave.Clock(5_000);
        return wave;
    }

    [Theory]
    [InlineData(ChipModel.Mos6581)]
    [InlineData(ChipModel.Mos8580)]
    public void Switching_the_waveform_off_holds_the_value_it_had(ChipModel model)
    {
        var wave = TriangleRunning(model);
        int before = wave.Output();
        Assert.NotEqual(0, before);

        wave.WriteControlReg(0x08);     // what a hard restart writes: TEST, no waveform

        Assert.Equal(before, wave.Output());
        wave.Clock(1_000);
        Assert.Equal(before, wave.Output());
    }

    /// <summary>The 6581 lets go of the held value after about 54,000 cycles and fades it bit by
    /// bit; it is gone well before 54,000 + a dozen fade steps.</summary>
    [Fact]
    public void A_held_value_fades_to_nothing()
    {
        var wave = TriangleRunning(ChipModel.Mos6581);
        wave.WriteControlReg(0x08);

        wave.Clock(50_000);
        Assert.NotEqual(0, wave.Output());

        wave.Clock(54_000 + (1_400 * 14));
        Assert.Equal(0, wave.Output());
    }

    [Fact]
    public void Switching_a_waveform_back_on_follows_the_oscillator_again()
    {
        var wave = TriangleRunning(ChipModel.Mos8580);
        wave.WriteControlReg(0x08);
        wave.WriteControlReg(0x11);     // TEST released: the accumulator starts from 0

        Assert.Equal(0, wave.Output());
    }
}
