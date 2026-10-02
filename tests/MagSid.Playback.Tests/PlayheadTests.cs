using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// What the editor's live step highlight reads. The question it answers is "which step of this
/// instrument is sounding at this instant" - not "which step did it reach last time it played",
/// which is what coverage is for and what ProgramCoverageTests pins instead.
/// </summary>
public class PlayheadTests
{
    private const int SampleRate = 44100;
    private const int SamplesPerFrame = SampleRate / 50;

    /// <summary>Channel 1 plays instrument 1 for four rows and then hands the voice to instrument
    /// 2 for the rest. Speed 0 so a row is a frame and the arithmetic below is in frames.</summary>
    private static SongPlaybackEngine Build()
    {
        var song = new Song { Speed = 0 };

        var first = new Instrument { Number = 1, Waveform = 0x41, AttackDecay = 0x09, SustainRelease = 0xAA };
        first.Wavetable.AddRange([0x41, 0x21, 0x11]);
        var second = new Instrument { Number = 2, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xAA };
        second.Wavetable.AddRange([0x21, 0x41]);

        song.Instruments.Add(first);
        song.Instruments.Add(second);

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), 1));
        for (int i = 0; i < 3; i++)
            pattern.Rows.Add(new HoldRow());

        pattern.Rows.Add(new NoteRow(NoteName.Parse("E-4"), 2));
        for (int i = 0; i < 60; i++)
            pattern.Rows.Add(new HoldRow());

        song.Patterns.Add(pattern);
        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        return new SongPlaybackEngine(song);
    }

    private static void Render(SongPlaybackEngine engine, int frames)
    {
        var buffer = new byte[SamplesPerFrame * frames * 2];
        engine.Read(buffer, 0, buffer.Length);
    }

    [Fact]
    public void A_sounding_instrument_reports_the_step_it_is_on()
    {
        var engine = Build();
        Render(engine, 20);

        bool found = engine.PlayheadAt(SamplesPerFrame * 2, 1, out int wave, out _, out _);

        Assert.True(found, "instrument 1 is sounding two frames in");
        Assert.True(wave >= 0, $"expected a wavetable step, got {wave}");
    }

    /// <summary>
    /// The bug this exists for: the editor lit the program steps of whatever instrument was
    /// selected, playing or not. Reading the playhead walked every buffered frame and kept the
    /// last step it saw for that instrument instead of taking the newest frame's answer, so an
    /// instrument that had stopped stayed lit for as long as its last note was still in the ring.
    /// </summary>
    [Fact]
    public void An_instrument_that_has_stopped_playing_reports_nothing()
    {
        var engine = Build();
        Render(engine, 20); // row 4 handed the voice to instrument 2 long before this

        bool found = engine.PlayheadAt(SamplesPerFrame * 18, 1, out int wave, out int arp, out int filter);

        Assert.False(found, "instrument 1 stopped sounding at row 4");
        Assert.Equal(-1, wave);
        Assert.Equal(-1, arp);
        Assert.Equal(-1, filter);
    }

    [Fact]
    public void The_instrument_that_took_the_voice_reports_instead()
    {
        var engine = Build();
        Render(engine, 20);

        bool found = engine.PlayheadAt(SamplesPerFrame * 18, 2, out int wave, out _, out _);

        Assert.True(found, "instrument 2 has the voice by now");
        Assert.True(wave >= 0, $"expected a wavetable step, got {wave}");
    }

    [Fact]
    public void An_instrument_that_never_played_reports_nothing()
    {
        var engine = Build();
        Render(engine, 20);

        Assert.False(engine.PlayheadAt(SamplesPerFrame * 10, 9, out int wave, out int arp, out int filter));
        Assert.Equal(-1, wave);
        Assert.Equal(-1, arp);
        Assert.Equal(-1, filter);
    }
}
