using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// What the DAW's cutoff marker reads: which frame of its filter program an instrument is on, and
/// the cutoff the SID was really given then - through the real engine, frame by frame.
/// </summary>
public class FilterPlayheadTests
{
    private const int SamplesPerFrame = SongPlaybackEngine.SampleRate / 50;

    /// <summary>Instrument 1 owns a three-step filter program, $20 $40 $60, no sweep, and plays
    /// one note on channel 1 at row 0. Instrument 2 has no filter program and plays on channel 2.
    /// Speed 0, so a row is a frame.</summary>
    private static SongPlaybackEngine Build()
    {
        var song = new Song { Speed = 0 };
        var filtered = new Instrument { Number = 1, Waveform = 0x41, AttackDecay = 0x09, SustainRelease = 0xAA };
        filtered.Filter.AddRange([FilterStep.Setup(0x1F, 0xF1, 0x20), FilterStep.Sweep(0x40), FilterStep.Sweep(0x60)]);
        song.Instruments.Add(filtered);
        song.Instruments.Add(new Instrument { Number = 2, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xAA });

        Pattern Single(int number, int instrument)
        {
            var pattern = new Pattern { Number = number };
            pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), instrument));
            for (int i = 0; i < 40; i++)
                pattern.Rows.Add(new HoldRow());
            return pattern;
        }

        song.Patterns.Add(Single(1, 1));
        song.Patterns.Add(Single(2, 2));
        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(2));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(2));

        var engine = new SongPlaybackEngine(song);
        var buffer = new byte[SamplesPerFrame * 20 * 2];
        engine.Read(buffer, 0, buffer.Length);
        return engine;
    }

    [Fact]
    public void Each_frame_of_the_program_reports_its_own_cutoff_and_the_last_one_holds()
    {
        var engine = Build();
        var seen = new List<(int Frame, int Cutoff)>();

        for (int frame = 0; frame < 8; frame++)
        {
            if (engine.FilterAt((long)frame * SamplesPerFrame, 1, out int at, out int cutoff))
                seen.Add((at, cutoff));
        }

        // The trigger claims the filter after that frame's filter step, so the program's first
        // step plays - and is reported as frame 0 - one frame after the note.
        Assert.Equal([(0, 0x20), (1, 0x40), (2, 0x60), (3, 0x60), (4, 0x60), (5, 0x60), (6, 0x60)], seen);
    }

    [Fact]
    public void An_instrument_that_does_not_own_the_filter_has_no_cutoff_marker()
    {
        var engine = Build();

        Assert.False(engine.FilterAt(5L * SamplesPerFrame, 2, out _, out _));
    }
}
