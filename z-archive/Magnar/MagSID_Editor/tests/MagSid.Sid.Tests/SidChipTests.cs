using MagSid.Sid;

namespace MagSid.Sid.Tests;

/// <summary>Integration smoke tests: feed SidChip a known register sequence (matching what
/// a real player would write) and verify it produces plausible, non-crashing audio output.</summary>
public class SidChipTests
{
    private static SidChip PlayVoice1Sawtooth()
    {
        var sid = new SidChip();
        sid.SetChipModel(ChipModel.Mos6581);

        sid.Write(0x00, 0x00); // voice1 freq lo
        sid.Write(0x01, 0x10); // voice1 freq hi
        sid.Write(0x05, 0x00); // AD: attack=0, decay=0
        sid.Write(0x06, 0xf0); // SR: sustain=15, release=0
        sid.Write(0x04, 0x21); // waveform=sawtooth(2), gate=1
        sid.Write(0x18, 0x0f); // volume max, no filter routing (filt bits = 0)

        return sid;
    }

    [Fact]
    public void Clock_produces_varying_nonzero_output_within_16bit_range()
    {
        var sid = PlayVoice1Sawtooth();

        var outputs = new List<int>();
        for (int i = 0; i < 5000; i++)
        {
            sid.Clock();
            outputs.Add(sid.Output());
        }

        Assert.Contains(outputs, o => o != 0);
        Assert.True(outputs.Distinct().Count() > 1,
            "expected varying output as the envelope attacks and the waveform oscillates");
        Assert.All(outputs, o => Assert.InRange(o, -32768, 32767));
    }

    [Fact]
    public void ClockAndSample_produces_the_requested_number_of_samples()
    {
        var sid = PlayVoice1Sawtooth();

        var buffer = new short[100];
        int deltaT = 100_000; // plenty of cycles for ~44 samples at 985248/44100
        int produced = sid.ClockAndSample(ref deltaT, buffer, 0, buffer.Length);

        Assert.True(produced > 0);
        Assert.True(produced <= buffer.Length);
        Assert.Contains(buffer, s => s != 0);
    }

    [Fact]
    public void NoteOff_settles_to_a_steady_level_far_from_the_sustained_swing()
    {
        var sid = PlayVoice1Sawtooth();

        // Let the attack complete, then sample the sustained (playing) output swing.
        int maxDuringSustain = 0;
        for (int i = 0; i < 5000; i++)
        {
            sid.Clock();
            maxDuringSustain = Math.Max(maxDuringSustain, Math.Abs(sid.Output()));
        }
        Assert.True(maxDuringSustain > 1000, "expected a substantial signal swing while the note is sustaining");

        sid.Write(0x06, 0x00); // release = 0 (fastest)
        sid.Write(0x04, 0x20); // gate off, waveform unchanged

        // Release from full envelope to zero takes a bounded number of cycles at the
        // fastest rate (well under 10000 at the fastest setting) - clock generously past
        // that, then confirm the output has settled to a steady value (not still swinging
        // with the waveform) that's far below the sustained level. On the modeled 6581,
        // "silence" is a fixed nonzero DC level, not exactly zero - see Voice/ExternalFilter's
        // ported DC-offset comments - so this compares magnitudes rather than asserting zero.
        for (int i = 0; i < 20_000; i++)
        {
            sid.Clock();
        }

        int released1 = sid.Output();
        for (int i = 0; i < 100; i++)
        {
            sid.Clock();
        }
        int released2 = sid.Output();

        // Steady, no longer tracking the oscillating waveform - the external filter's highpass
        // pole is very slow (w0hp is ~1000x smaller than w0lp), so it is still asymptotically
        // converging at this point; check it has stopped moving in large (waveform-sized) steps
        // rather than requiring it to be perfectly settled.
        Assert.True(Math.Abs(released2 - released1) < maxDuringSustain / 10,
            $"expected the output to have stopped swinging with the waveform (released1={released1}, released2={released2}, sustainSwing={maxDuringSustain})");
        Assert.True(Math.Abs(released1) < maxDuringSustain / 4,
            $"expected released output ({released1}) to be far below the sustained swing ({maxDuringSustain})");
    }
}
