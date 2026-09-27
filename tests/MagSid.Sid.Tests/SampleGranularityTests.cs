using MagSid.Sid;

namespace MagSid.Sid.Tests;

/// <summary>
/// Asking the chip for one sample at a time must give exactly the audio that asking for many at
/// once does.
///
/// SidFilePlaybackEngine relies on this: it samples one at a time so it can read each voice's own
/// output at the instant a sample was taken, which is what the player window's scopes draw. If the
/// two granularities drifted, every exported tune would play subtly differently in that window than
/// in the tracker, and nothing else in the suite would notice - the register-equivalence tests never
/// clock the chip at all.
/// </summary>
public class SampleGranularityTests
{
    private const double ClockFrequency = 985248.0;
    private const int SampleRate = 44100;

    [Theory]
    [InlineData(SamplingMethod.Interpolate)]
    [InlineData(SamplingMethod.Fast)]
    public void Sampling_one_at_a_time_matches_sampling_in_bulk(SamplingMethod method)
    {
        const int cycles = 200_000;     // about a fifth of a second
        const int capacity = 16_384;

        short[] bulk = Render(method, cycles, capacity, oneAtATime: false);
        short[] single = Render(method, cycles, capacity, oneAtATime: true);

        Assert.True(bulk.Length > 8000, $"Only {bulk.Length} samples came out; the test would prove little.");
        Assert.Equal(bulk.Length, single.Length);

        for (int i = 0; i < bulk.Length; i++)
        {
            if (bulk[i] != single[i])
            {
                Assert.Fail(
                    $"{method}: sample {i} differs - {bulk[i]} in bulk, {single[i]} one at a time. " +
                    "SidFilePlaybackEngine's per-sample loop is not equivalent to a bulk render.");
            }
        }
    }

    /// <summary>A voice sounding with a moving filter, so the comparison exercises the parts of the
    /// chip whose state carries between calls rather than a silent one.</summary>
    private static short[] Render(SamplingMethod method, int cycles, int capacity, bool oneAtATime)
    {
        var sid = new SidChip();
        sid.SetChipModel(ChipModel.Mos6581);
        sid.SetSamplingParameters(ClockFrequency, method, SampleRate);

        sid.Write(0x18, 0x1F);      // volume 15, low-pass
        sid.Write(0x17, 0xF1);      // resonance, voice 1 routed through the filter
        sid.Write(0x15, 0x00);
        sid.Write(0x16, 0x40);
        sid.Write(0x00, 0x25);      // voice 1 frequency
        sid.Write(0x01, 0x11);
        sid.Write(0x05, 0x00);      // instant attack/decay
        sid.Write(0x06, 0xF0);      // full sustain
        sid.Write(0x04, 0x41);      // pulse + gate

        var buffer = new short[capacity];
        int written = 0;
        int remaining = cycles;

        while (remaining > 0 && written < capacity)
        {
            int before = remaining;
            int produced = oneAtATime
                ? sid.ClockAndSample(ref remaining, buffer, written, 1)
                : sid.ClockAndSample(ref remaining, buffer, written, capacity - written);

            written += produced;

            // A call that neither produced a sample nor consumed a cycle would spin forever.
            if (produced == 0 && remaining == before)
                break;
        }

        return buffer[..written];
    }
}
