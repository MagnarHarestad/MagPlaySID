using MagSid.Core.Model;
using MagSid.Sid;

namespace MagSid.Playback.Tests;

/// <summary>
/// A retrigger's hard reset used to force release=0 (the fastest rate) to drop the previous
/// note's envelope instantly. On real SID hardware the envelope's own rate counter is never
/// reset by a register write, only compared against the period the current rate selects - so
/// dropping the period below wherever a slower decay had already driven that counter (which
/// release 0 does to any note that had been sustaining a few frames) makes the counter wrap its
/// full 15-bit range before the envelope can move at all, real and verified against actual SID
/// hardware (see EnvelopeGenerator's own ADSR-delay-bug comment). That takes longer than the one
/// frame before the next attack, so attack starts from the old note's still-full envelope value
/// instead of zero - and (envelope+1)&0xff from 255 rolls over to 0 without ever landing on the
/// exact 0xff that would hand off to decay/sustain, freezing the new note silent until the *next*
/// retrigger. These pin the fix (release 15, not 0 - the slowest rate, so its period is never
/// smaller than whatever any instrument on any song was already counting towards) against real
/// rendered audio, not just register writes, since the writes alone look identical either way -
/// only the envelope's actual output tells the two apart.
/// </summary>
public class EnvelopeRetriggerTests
{
    private static int PeakOverFrame(SidChip sid)
    {
        var buf = new short[882]; // one 50Hz frame at 44.1kHz
        int deltaT = 19705; // one PAL frame's worth of SID cycles
        int written = 0;
        while (written < buf.Length)
        {
            int produced = sid.ClockAndSample(ref deltaT, buf, written, buf.Length - written);
            if (produced == 0) break;
            written += produced;
        }
        int peak = 0;
        for (int i = 0; i < written; i++) peak = Math.Max(peak, Math.Abs((int)buf[i]));
        return peak;
    }

    [Fact]
    public void A_retrigger_after_a_sustained_note_does_not_freeze_the_next_note_silent()
    {
        // Instrument 1: decay 7 (slow enough to matter), sustain 15, release 7 - matches
        // Magnificent's own instrument 01, the exact instrument this bug was found on.
        var sustained = new Instrument
        { Number = 1, Waveform = 0x41, AttackDecay = 0x07, SustainRelease = 0xf7 };
        // Instrument 2: a different, unrelated instrument - what actually retriggers.
        var next = new Instrument
        { Number = 2, Waveform = 0x41, AttackDecay = 0x05, SustainRelease = 0xb7 };

        var song = new Song { Speed = 0 };
        song.Instruments.Add(sustained);
        song.Instruments.Add(next);

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-3"), 1));
        // Several holds: long enough for decay 7's slow rate counter to accumulate well past
        // where release 0's period (9) would sit - this is what makes the bug reproduce.
        for (int i = 0; i < 5; i++) pattern.Rows.Add(new HoldRow());
        pattern.Rows.Add(new NoteRow(NoteName.Parse("E-3"), 2));
        for (int i = 0; i < 3; i++) pattern.Rows.Add(new HoldRow());
        song.Patterns.Add(pattern);

        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        var sid = new SidChip();
        sid.SetSamplingParameters(985248.0, SamplingMethod.Interpolate, 44100);
        var sequencer = new SongSequencer(song, sid.Write);

        int lastPeak = 0;
        for (int frame = 0; frame < 9; frame++)
        {
            sequencer.Frame();
            lastPeak = PeakOverFrame(sid);
        }

        // Frame 8 is well into instrument 2's own attack/sustain, several frames after its
        // trigger at frame 6 - it should be genuinely loud, not stuck at the near-silent DC
        // level a frozen envelope produces.
        Assert.True(lastPeak > 5000, $"expected a loud, healthy attack, got peak {lastPeak}");
    }
}
