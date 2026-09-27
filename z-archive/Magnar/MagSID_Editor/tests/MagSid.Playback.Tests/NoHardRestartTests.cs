using System.Reflection;
using MagSid.Core.Model;
using MagSid.Sid;

namespace MagSid.Playback.Tests;

/// <summary>
/// The ADSR-delay-bug freeze EnvelopeRetriggerTests documents (envelope pinned at 0xff, then a
/// retrigger's release stall leaves it there so the next attack's (envelope+1)&amp;0xff wraps to
/// 0 instead of landing on 0xff) needs two things to reproduce: the outgoing instrument's own
/// sustain pinned at 15 (0xff, so envelope genuinely sits at the exact trap value rather than
/// somewhere below it), and its rate_counter parked somewhere the hard-restart's fixed release=7
/// (period 313) cannot reach in one frame.
///
/// A slow-but-not-absurd decay (10, period 1954 - comfortably inside one 19705-cycle frame, so
/// there is a real fix to demonstrate) with an own release chosen large enough to cover it
/// (period 3126) is what these tests use to prove NoHardRestart actually works - leaving AD/SR
/// alone on retrigger lets that own, appropriately-chosen release genuinely run instead of the
/// mismatched constant stalling.
///
/// Lightforce's own real instrument 02 (decay 15, period 31251) is a harder case this flag alone
/// does not fully solve: that period exceeds a whole frame, so no single-frame release choice -
/// forced or the instrument's own - can guarantee moving the envelope off exactly 0xff before
/// the next attack. NOHR still helps there (verified separately: it turns the stall into a real,
/// if incomplete, decay step) but a full fix for that specific instrument needs its own sustain
/// nudged below 15 so the trap value is never reached in the first place - a musical-data change
/// deliberately left to the composer rather than made automatically here.
/// </summary>
public class NoHardRestartTests
{
    private static int EnvelopeOutput(SidChip sid)
    {
        var voice = ((Array)typeof(SidChip)
            .GetField("_voice", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(sid)!).GetValue(0)!;
        var envelope = voice.GetType().GetProperty("Envelope")!.GetValue(voice)!;
        return (int)envelope.GetType().GetMethod("Output")!.Invoke(envelope, null)!;
    }

    private static Song BuildSong(bool noHardRestartOnOutgoing)
    {
        // Outgoing: decay 10 (fits inside one frame), sustain pinned at 15 (the exact trap
        // value), own release 11 - large enough to cover decay 10's own resting rate_counter.
        var outgoing = new Instrument
        {
            Number = 1, Waveform = 0x11, AttackDecay = 0x0a, SustainRelease = 0xfb,
            NoHardRestart = noHardRestartOnOutgoing,
        };
        // Incoming: an unrelated instrument, same shape used throughout this test suite.
        var incoming = new Instrument
        { Number = 2, Waveform = 0x41, AttackDecay = 0x02, SustainRelease = 0xb4 };

        var song = new Song { Speed = 0 };
        song.Instruments.Add(outgoing);
        song.Instruments.Add(incoming);

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("D#1"), 1));
        for (int i = 0; i < 6; i++) pattern.Rows.Add(new HoldRow()); // long enough to reach 0xff
        pattern.Rows.Add(new NoteRow(NoteName.Parse("D#1"), 2));
        for (int i = 0; i < 3; i++) pattern.Rows.Add(new HoldRow());
        song.Patterns.Add(pattern);

        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        return song;
    }

    private static int EnvelopeAtFrame(Song song, int targetFrame)
    {
        var sid = new SidChip();
        sid.SetSamplingParameters(985248.0, SamplingMethod.Interpolate, 44100);
        var sequencer = new SongSequencer(song, sid.Write);

        var buf = new short[882];
        int env = 0;
        for (int frame = 0; frame <= targetFrame; frame++)
        {
            sequencer.Frame();
            int deltaT = 19705;
            int written = 0;
            while (written < buf.Length)
            {
                int produced = sid.ClockAndSample(ref deltaT, buf, written, buf.Length - written);
                if (produced == 0) break;
                written += produced;
            }
            env = EnvelopeOutput(sid);
        }
        return env;
    }

    [Fact]
    public void Without_the_flag_the_next_note_freezes_at_zero()
    {
        // Row 7 (frame 7) triggers instrument 2; frame 10 is well into its own decay/sustain.
        int env = EnvelopeAtFrame(BuildSong(noHardRestartOnOutgoing: false), targetFrame: 10);
        Assert.Equal(0, env);
    }

    [Fact]
    public void With_the_flag_on_the_outgoing_instrument_the_next_note_reaches_its_own_sustain()
    {
        int env = EnvelopeAtFrame(BuildSong(noHardRestartOnOutgoing: true), targetFrame: 10);
        // Instrument 2's SR=$b4: sustain nibble $b -> sustain_level[11] = 0xbb = 187.
        Assert.Equal(187, env);
    }

    [Fact]
    public void The_flag_on_the_wrong_incoming_instrument_does_not_help()
    {
        // Sanity check for the design itself: flagging instrument 2 (incoming) rather than
        // instrument 1 (outgoing, the one whose slow decay actually causes the stall) must not
        // accidentally paper over the bug - the fix only works when it targets the instrument
        // whose own rate the reset pair collides with.
        var song = BuildSong(noHardRestartOnOutgoing: false);
        song.Instruments.Single(i => i.Number == 2).NoHardRestart = true;

        int env = EnvelopeAtFrame(song, targetFrame: 10);
        Assert.Equal(0, env);
    }
}
