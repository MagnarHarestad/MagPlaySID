using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Playback;

namespace MagSid.Playback.Tests;

/// <summary>
/// The song is edited while it plays. An instrument loaded after Play and then used in a pattern
/// stopped playback: the sequencer's table of instruments was built when it started, the lookup
/// threw on the audio thread, and the output stopped.
/// </summary>
public class EditWhilePlayingTests
{
    private static (Song Song, List<(int Offset, int Value)> Writes, SongSequencer Sequencer) Playing()
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xAA });

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), 1));
        for (int i = 0; i < 7; i++)
            pattern.Rows.Add(new HoldRow());
        song.Patterns.Add(pattern);

        for (int ch = 0; ch < 3; ch++)
            song.ChannelOrders[ch].Tokens.Add(new PatternRefToken(1));

        var writes = new List<(int Offset, int Value)>();
        var sequencer = new SongSequencer(song, (o, v) => writes.Add((o, v)));
        sequencer.Frame();
        return (song, writes, sequencer);
    }

    [Fact]
    public void A_note_on_an_instrument_loaded_after_play_sounds()
    {
        var (song, writes, sequencer) = Playing();

        // Loaded mid-play, then written into the pattern ahead of where the song is.
        song.Instruments.Add(new Instrument { Number = 2, Waveform = 0x41, AttackDecay = 0x00, SustainRelease = 0xF0 });
        song.Patterns[0].Rows[4] = new NoteRow(NoteName.Parse("E-4"), 2);

        // The C-4 on row 0 had already looked ahead over its holds when it was fetched, so the
        // new note is reached on the pattern's next pass round.
        writes.Clear();
        for (int frame = 0; frame < 16; frame++)
            sequencer.Frame();

        var (lo, hi) = NotePalTable.GetFreq(NoteName.Parse("E-4"));
        Assert.Contains((0, (int)lo), writes);
        Assert.Contains((1, (int)hi), writes);
    }

    [Fact]
    public void A_pattern_made_after_play_is_reached()
    {
        var (song, writes, sequencer) = Playing();

        var added = new Pattern { Number = 2 };
        added.Rows.Add(new NoteRow(NoteName.Parse("G-4"), 1));
        added.Rows.Add(new HoldRow());
        song.Patterns.Add(added);
        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(2));

        writes.Clear();
        for (int frame = 0; frame < 12; frame++)
            sequencer.Frame();

        var (lo, hi) = NotePalTable.GetFreq(NoteName.Parse("G-4"));
        Assert.Contains((0, (int)lo), writes);
        Assert.Contains((1, (int)hi), writes);
    }

    [Fact]
    public void A_note_on_an_instrument_that_is_gone_is_passed_over()
    {
        var (song, _, sequencer) = Playing();

        song.Patterns[0].Rows[4] = new NoteRow(NoteName.Parse("E-4"), 9);

        for (int frame = 0; frame < 16; frame++)
            sequencer.Frame();
    }

    [Fact]
    public void An_instrument_deleted_and_replaced_while_playing_plays_the_replacement()
    {
        // As reported: an Arp from the sound bank added as instrument 2, deleted, and a Bass added
        // in its place - the song kept playing the Arp, because the deleted object still carries
        // the number 2 and the sequencer's table still held it.
        var (song, writes, sequencer) = Playing();
        var arp = new Instrument { Number = 2, Waveform = 0x11, AttackDecay = 0x00, SustainRelease = 0xF0 };
        song.Instruments.Add(arp);
        song.Patterns[0].Rows[4] = new NoteRow(NoteName.Parse("E-4"), 2);
        for (int frame = 0; frame < 16; frame++)
            sequencer.Frame();

        song.Instruments.Remove(arp);
        song.Instruments.Add(new Instrument { Number = 2, Waveform = 0x41, AttackDecay = 0x00, SustainRelease = 0xF0 });

        writes.Clear();
        for (int frame = 0; frame < 16; frame++)
            sequencer.Frame();

        // The E-4 on voice 1 is struck on the Bass's pulse, not the Arp's triangle.
        Assert.Contains((4, 0x41), writes);
        Assert.DoesNotContain((4, 0x11), writes);
    }
}
