using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// What the DAW's envelope marker reads: the SID's own envelope for the voice playing an
/// instrument, frame by frame, through the real engine - so the marker follows what is heard,
/// release tail included, rather than what the instrument's numbers say should happen.
/// </summary>
public class EnvelopePlayheadTests
{
    private const int SamplesPerFrame = SongPlaybackEngine.SampleRate / 50;

    /// <summary>One note on instrument 1 - attack 8 (100ms), decay 4, sustain 8, release 4 - held
    /// for 30 frames and released. Speed 0, so a row is a frame.</summary>
    private static SongPlaybackEngine Build()
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x41, AttackDecay = 0x84, SustainRelease = 0x84 });

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), 1));
        for (int i = 0; i < 29; i++)
            pattern.Rows.Add(new HoldRow());
        pattern.Rows.Add(new NoteOffRow());
        for (int i = 0; i < 99; i++)
            pattern.Rows.Add(new HoldRow());

        song.Patterns.Add(pattern);
        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        var engine = new SongPlaybackEngine(song);
        var buffer = new byte[SamplesPerFrame * 90 * 2];
        engine.Read(buffer, 0, buffer.Length);
        return engine;
    }

    [Fact]
    public void The_envelope_goes_attack_then_decay_sustain_then_release_then_silence()
    {
        var engine = Build();
        var stages = new List<int>();
        int previousStage = -1, previousLevel = 0;
        bool silentAtEnd = false;

        for (int frame = 0; frame < 90; frame++)
        {
            if (!engine.EnvelopeAt((long)frame * SamplesPerFrame, 1, out int stage, out int level))
            {
                silentAtEnd = stages.Count > 0;
                continue;
            }

            Assert.InRange(level, 1, 255);
            if (stage == previousStage && stage == 0)
                Assert.True(level >= previousLevel, $"frame {frame}: attack fell from {previousLevel} to {level}");
            if (stage == previousStage && stage == 2)
                Assert.True(level <= previousLevel, $"frame {frame}: release rose from {previousLevel} to {level}");

            if (stages.Count == 0 || stages[^1] != stage)
                stages.Add(stage);

            previousStage = stage;
            previousLevel = level;
        }

        Assert.Equal([0, 1, 2], stages);
        Assert.True(silentAtEnd, "the release should die away within the rendered frames");
    }

    [Fact]
    public void An_instrument_nobody_plays_has_no_envelope()
    {
        var engine = Build();

        Assert.False(engine.EnvelopeAt(10L * SamplesPerFrame, 2, out _, out _));
    }

    /// <summary>Sustain is where decay stops: at sustain 8 the SID holds level $88.</summary>
    [Fact]
    public void A_held_note_sits_at_the_sustain_level()
    {
        var engine = Build();

        Assert.True(engine.EnvelopeAt(25L * SamplesPerFrame, 1, out int stage, out int level));
        Assert.Equal(1, stage);
        Assert.Equal(0x88, level);
    }
}
