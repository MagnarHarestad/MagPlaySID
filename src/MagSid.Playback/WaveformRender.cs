using MagSid.Sid;

namespace MagSid.Playback;

/// <summary>
/// What a voice really sounds like, for drawing: one SID voice gated on with a control byte and a
/// pulse width, run through reSID, and its audio output read back. So the pulse has the duty the
/// width gives it and sags the way the chip's output filter makes it, combined waveforms are the
/// chip's own AND of the oscillators rather than a sum, and the 6581 and the 8580 each look like
/// themselves.
/// </summary>
public static class WaveformRender
{
    private const double ClockFrequency = 985248.0;

    /// <summary>Frequency register $1000, about 240Hz: low enough that a few periods show the pulse
    /// shape, high enough that the output filter does not flatten it into spikes.</summary>
    private const int FrequencyRegister = 0x1000;

    /// <summary>Cycles in one period at that frequency - the accumulator is 24 bits.</summary>
    private const int CyclesPerPeriod = (1 << 24) / FrequencyRegister;

    /// <summary>The attack, and the output filter settling on the waveform's own level, before the
    /// periods that are drawn. Measured: within 3% of fully settled (under a pixel in the strip)
    /// at 40ms, and a third of the time 120ms took - the strip is redrawn as a knob turns.</summary>
    private const int WarmUpCycles = 40_000;

    private static readonly Dictionary<ChipModel, float> Reference = [];

    /// <summary>
    /// <paramref name="periods"/> periods of the voice's output as <paramref name="samples"/>
    /// values, centred on zero. 1 is the swing of a square wave (pulse width 8) on the same chip,
    /// so a quiet combined waveform is drawn quiet; a louder one is scaled to fit.
    /// </summary>
    public static float[] Render(byte control, int pulseWidth, ChipModel model, int samples, int periods = 3,
        int warmUpCycles = WarmUpCycles)
    {
        var raw = RenderRaw(control, pulseWidth, model, samples, periods, warmUpCycles);
        float reference = ReferenceSwing(model);

        // Louder than the square - some combined waveforms on an 8580 are - is scaled down to fit
        // rather than clipped, which would cut off the very shape being shown.
        float mean = raw.Length == 0 ? 0f : raw.Average();
        float peak = raw.Length == 0 ? 0f : raw.Max(v => Math.Abs(v - mean));
        float scale = Math.Max(reference, peak);
        var values = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            values[i] = (raw[i] - mean) / scale;

        return values;
    }

    private static float ReferenceSwing(ChipModel model)
    {
        lock (Reference)
        {
            if (!Reference.TryGetValue(model, out float swing))
            {
                var square = RenderRaw(0x41, 8, model, 512, 3, WarmUpCycles);
                swing = Math.Max(1f, (square.Max() - square.Min()) / 2f);
                Reference[model] = swing;
            }

            return swing;
        }
    }

    private static float[] RenderRaw(byte control, int pulseWidth, ChipModel model, int samples, int periods, int warmUpCycles)
    {
        samples = Math.Max(2, samples);

        var sid = new SidChip();
        sid.SetChipModel(model);
        sid.Write(0x18, 0x0F);                      // volume, no filter
        sid.Write(0x05, 0x00);                      // attack 2ms, decay irrelevant
        sid.Write(0x06, 0xF0);                      // sustain at full
        sid.Write(0x00, FrequencyRegister & 0xFF);
        sid.Write(0x01, FrequencyRegister >> 8);
        sid.Write(0x02, 0x00);
        sid.Write(0x03, Math.Clamp(pulseWidth, 0, 15));
        sid.Write(0x04, control | 0x01);

        // Whole periods, so the drawing always starts at the same point of the wave.
        int warmUp = (warmUpCycles + CyclesPerPeriod - 1) / CyclesPerPeriod * CyclesPerPeriod;
        for (int i = 0; i < warmUp; i++)
            sid.Clock();

        // One reading per sample, spread evenly over the periods drawn.
        var values = new float[samples];
        double cyclesPerSample = periods * CyclesPerPeriod / (double)samples;
        double due = 0;
        for (int i = 0; i < samples; i++)
        {
            due += cyclesPerSample;
            int cycles = (int)due;
            due -= cycles;
            for (int c = 0; c < cycles; c++)
                sid.Clock();

            values[i] = sid.Output();
        }

        return values;
    }
}
