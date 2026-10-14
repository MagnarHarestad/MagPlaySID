using MagSid.Core.Model;
using MagSid.Playback;

namespace MagSid.Playback.Tests;

/// <summary>
/// Starting playback part-way into a song, which the editor does when Space resumes rather than
/// restarts.
///
/// The case that matters is channels that have drifted apart. Their song orders are independent
/// and their patterns are different lengths, so by the time playback stops each channel is at
/// its own place in its own order - a channel looping a short pattern is several entries ahead
/// of one playing long patterns. Resuming from a single shared position therefore only lines up
/// at the very start of a song; anywhere else it drops the channels onto each other's timeline
/// and the parts come back in the wrong places relative to each other.
/// </summary>
public class ResumeTests
{
    private static Instrument Instr(int number) =>
        new() { Number = number, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xaa };

    /// <summary>Three one-row patterns, each holding a note that identifies it, and a different
    /// order per channel - so "which note did this channel play" says exactly which order entry
    /// it started on.</summary>
    private static Song ThreePatternSong()
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(Instr(1));

        string[] notes = ["C-4", "D-4", "E-4"];
        for (int p = 0; p < 3; p++)
        {
            var pattern = new Pattern { Number = p + 1 };
            pattern.Rows.Add(new NoteRow(NoteName.Parse(notes[p]), 1));
            song.Patterns.Add(pattern);
        }

        // Deliberately different per channel, and different lengths.
        int[][] orders = [[1, 2, 3], [3, 1, 2], [2, 3, 1]];
        for (int ch = 0; ch < 3; ch++)
        {
            foreach (int n in orders[ch])
            {
                song.ChannelOrders[ch].Tokens.Add(new PatternRefToken(n));
            }
        }

        return song;
    }

    /// <summary>A position naming, per channel, an order entry and a row - the loop bookkeeping
    /// left at zero, as it is for a channel that has not entered an LS/LE section.</summary>
    private static SongSequencer.SequencerPosition At(Song song, params (int Order, int Row)[] perChannel)
    {
        var channels = new SongSequencer.ChannelPlaybackState[3];
        for (int ch = 0; ch < 3; ch++)
        {
            var tokens = song.ChannelOrders[ch].Tokens;
            var (order, row) = perChannel[ch];
            int index = tokens.Count == 0 ? 0 : Math.Clamp(order, 0, tokens.Count - 1);
            int pattern = tokens.Count == 0 ? 0 : ((PatternRefToken)tokens[index]).PatternNumber;
            channels[ch] = new SongSequencer.ChannelPlaybackState(index + 1, pattern, row, 0, 0, 0);
        }

        return new SongSequencer.SequencerPosition(-1, channels);
    }

    private static List<NoteName?> FirstNotePerChannel(
        Song song, SongSequencer.SequencerPosition? resumeFrom)
    {
        var first = new List<NoteName?> { null, null, null };
        var sequencer = new SongSequencer(song, (_, _) => { }, resumeFrom);
        sequencer.RowTriggered += (ch, _, _, _, row) =>
        {
            if (first[ch] is null && row is NoteRow(var note, _))
            {
                first[ch] = note;
            }
        };

        // Several frames, not one: an exactly-restored channel carries the tick countdown it had
        // when it was captured, so its next row can legitimately be a few frames away. Only the
        // *first* note each channel plays is recorded, so running on cannot change the answer.
        for (int i = 0; i < 8; i++)
        {
            sequencer.Frame();
        }

        return first;
    }

    [Fact]
    public void Without_a_resume_position_every_channel_starts_at_its_own_first_order_entry()
    {
        var song = ThreePatternSong();

        // Orders are 1,2,3 / 3,1,2 / 2,3,1 - so entry 0 is a different pattern on each channel.
        Assert.Equal(
            [NoteName.Parse("C-4"), NoteName.Parse("E-4"), NoteName.Parse("D-4")],
            FirstNotePerChannel(song, null));
    }

    [Fact]
    public void Each_channel_resumes_at_its_own_order_entry_rather_than_a_shared_one()
    {
        var song = ThreePatternSong();

        // The channels are at different points in their own orders, which is the normal state
        // once a song has been playing for a while. Entry 2 / 0 / 1 selects pattern 3 / 3 / 3 -
        // picked so that a correct resume gives the same note on all three, while any
        // implementation that applied one shared index would land on three different patterns.
        var resume = At(song, (2, 0), (0, 0), (1, 0));

        Assert.Equal(
            [NoteName.Parse("E-4"), NoteName.Parse("E-4"), NoteName.Parse("E-4")],
            FirstNotePerChannel(song, resume));
    }

    [Fact]
    public void A_shared_position_would_not_have_kept_the_channels_together()
    {
        // Guards the test above from passing for the wrong reason: if applying one index to
        // every channel happened to give the same answer, it would prove nothing. Song position
        // 2 means pattern 3 / 2 / 1 on the three channels - three different notes.
        var song = ThreePatternSong();
        var shared = At(song, (2, 0), (2, 0), (2, 0));

        var notes = FirstNotePerChannel(song, shared);
        Assert.Equal(3, notes.Distinct().Count());
    }

    [Fact]
    public void A_resume_row_starts_part_way_into_a_pattern()
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(Instr(1));

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), 1));
        pattern.Rows.Add(new NoteRow(NoteName.Parse("D-4"), 1));
        pattern.Rows.Add(new NoteRow(NoteName.Parse("E-4"), 1));
        song.Patterns.Add(pattern);
        foreach (var order in song.ChannelOrders)
        {
            order.Tokens.Add(new PatternRefToken(1));
        }

        // Row 2 of the only pattern - the third note, not the first.
        var notes = FirstNotePerChannel(song, At(song, (0, 2), (0, 2), (0, 2)));
        Assert.All(notes, n => Assert.Equal(NoteName.Parse("E-4"), n));
    }

    [Fact]
    public void A_channel_stopped_inside_a_loop_resumes_with_its_repeat_count_intact()
    {
        // LS / C-4 / LE03 / D-4 - the section plays three times, then falls through.
        //
        // Stop after the second pass and resume: only one pass should be left. Restoring the row
        // but not the repeat count starts the loop over instead, so the channel plays the
        // section three more times and drifts away from the other two - which is what a resume
        // that carried only an order entry and a row used to do.
        var song = new Song { Speed = 0 };
        song.Instruments.Add(Instr(1));

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new LoopStartRow());
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), 1));
        pattern.Rows.Add(new LoopEndRow(3));
        pattern.Rows.Add(new NoteRow(NoteName.Parse("D-4"), 1));
        song.Patterns.Add(pattern);
        foreach (var order in song.ChannelOrders)
        {
            order.Tokens.Add(new PatternRefToken(1));
        }

        static List<NoteName> PlayFrom(Song song, SongSequencer.SequencerPosition? from, int frames,
                                       out SongSequencer.SequencerPosition captured)
        {
            var notes = new List<NoteName>();
            var sequencer = new SongSequencer(song, (_, _) => { }, from);
            sequencer.RowTriggered += (ch, _, _, _, row) =>
            {
                if (ch == 0 && row is NoteRow(var note, _))
                {
                    notes.Add(note);
                }
            };
            for (int i = 0; i < frames; i++)
            {
                sequencer.Frame();
            }
            captured = sequencer.Capture();
            return notes;
        }

        var c4 = NoteName.Parse("C-4");
        var d4 = NoteName.Parse("D-4");

        // Two frames in, C-4 has played twice and one pass is left.
        var before = PlayFrom(song, null, 2, out var midLoop);
        Assert.Equal([c4, c4], before);

        // Carrying on must give exactly one more C-4 and then fall through to D-4.
        var after = PlayFrom(song, midLoop, 2, out _);
        Assert.Equal([c4, d4], after);

        // And the same resume with the loop bookkeeping zeroed - all a position carrying only an
        // order entry and a row can offer - must NOT produce that, or the assertion above would
        // hold whether or not the repeat count was actually restored. It starts the section over,
        // so the second frame is another C-4 instead of falling through to D-4.
        var rowOnly = new SongSequencer.SequencerPosition(
            midLoop.SpeedCounter,
            [.. midLoop.Channels.Select(c => c with { LoopStartRowIndex = 0, LoopRepeatCounter = 0 })]);
        Assert.Equal([c4, c4], PlayFrom(song, rowOnly, 2, out _));
    }

    [Fact]
    public void Winding_forward_puts_a_shorter_channel_where_it_really_is_not_at_the_same_index()
    {
        // Lightforce's shape: channels 1 and 2 run 128-tick patterns while channel 3 runs
        // 64-tick ones, so channel 3 gets through two order entries for every one of theirs.
        // Order entry N is therefore a different moment in the music on channel 3 than on the
        // other two - which is exactly why "play from here" cannot just hand every channel the
        // same song position.
        var song = new Song { Speed = 0 };
        song.Instruments.Add(Instr(1));

        // Long patterns: 4 rows. Short: 2 rows. One note then holds, so the note identifies it.
        static Pattern Make(int number, string note, int rows)
        {
            var p = new Pattern { Number = number };
            p.Rows.Add(new NoteRow(NoteName.Parse(note), 1));
            for (int i = 1; i < rows; i++) p.Rows.Add(new HoldRow());
            return p;
        }

        song.Patterns.Add(Make(1, "C-4", 4));
        song.Patterns.Add(Make(2, "D-4", 4));
        song.Patterns.Add(Make(3, "E-4", 2));   // channel 3's short patterns
        song.Patterns.Add(Make(4, "F-4", 2));
        song.Patterns.Add(Make(5, "G-4", 2));
        song.Patterns.Add(Make(6, "A-4", 2));

        foreach (int n in new[] { 1, 2 }) song.ChannelOrders[0].Tokens.Add(new PatternRefToken(n));
        foreach (int n in new[] { 1, 2 }) song.ChannelOrders[1].Tokens.Add(new PatternRefToken(n));
        foreach (int n in new[] { 3, 4, 5, 6 }) song.ChannelOrders[2].Tokens.Add(new PatternRefToken(n));

        // Ask for channel 1's order entry 1 (its second pattern), which starts at tick 4.
        // By then channel 3 - two ticks per entry - must be on its *third* entry, pattern 5.
        var found = SongSequencer.FindPosition(song, channel: 0, orderIndex: 1, rowIndex: 0);

        Assert.NotNull(found);

        // Asserted through what the song then plays rather than through the captured fields: the
        // position is deliberately the frame boundary *before* the target row triggers, so the
        // channels' own state there still names the previous entry. What matters is that
        // resuming from it plays channel 1's second pattern (D-4) while channel 3 is already
        // three entries into its own, shorter order (G-4) - not the pattern that shares channel
        // 1's index, which is F-4 and is what a shared song position would have given.
        var notes = FirstNotePerChannel(song, found);
        Assert.Equal(NoteName.Parse("D-4"), notes[0]);
        Assert.Equal(NoteName.Parse("G-4"), notes[2]);
        Assert.NotEqual(NoteName.Parse("F-4"), notes[2]);
    }

    [Fact]
    public void An_out_of_range_resume_position_is_clamped_rather_than_throwing()
    {
        // A caller holding a song position past the end of a short channel's order is normal,
        // since the three orders can differ in length.
        var song = ThreePatternSong();
        var beyond = At(song, (99, 99), (99, 99), (99, 99));

        var notes = FirstNotePerChannel(song, beyond);
        Assert.All(notes, n => Assert.True(n is not null, "every channel should still have triggered a row"));
    }

    [Fact]
    public void Starting_where_one_channel_changes_pattern_plays_what_the_song_plays_from_the_top()
    {
        // Channel 1 runs one eight-row pattern while channel 3 plays two four-row ones, so channel
        // 3's second entry starts as channel 1 reaches row 4. At that boundary channel 3 sits one
        // past its pattern's last row; restored onto that row instead, it played it again and
        // came out a row behind - what the piano roll's REC starts from.
        var song = new Song { Speed = 0 };
        song.Instruments.Add(Instr(1));

        var eight = new Pattern { Number = 1 };
        var four = new Pattern { Number = 2 };
        for (int row = 0; row < 8; row++)
            eight.Rows.Add(new NoteRow(NoteName.AllNotes[24 + row], 1));
        for (int row = 0; row < 4; row++)
            four.Rows.Add(new NoteRow(NoteName.AllNotes[48 + row], 1));
        song.Patterns.Add(eight);
        song.Patterns.Add(four);

        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(2));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(2));

        var from = SongSequencer.FindPosition(song, channel: 2, orderIndex: 1, rowIndex: 0);
        Assert.NotNull(from);

        static List<string> Triggers(Song song, SongSequencer.SequencerPosition? from, int frames)
        {
            var heard = new List<string>();
            var frame = new List<string>();
            var sequencer = new SongSequencer(song, (_, _) => { }, from);
            sequencer.RowTriggered += (ch, pattern, row, _, _) => frame.Add($"{ch}:{pattern}:{row}");
            for (int i = 0; i < frames; i++)
            {
                frame.Clear();
                sequencer.Frame();
                heard.Add(string.Join(" ", frame));
            }

            return heard;
        }

        var fromTop = Triggers(song, null, from!.Frame + 12);
        var started = Triggers(song, from, 12);

        Assert.Equal("0:1:4 1:1:4 2:2:0", started[0]);
        Assert.Equal(fromTop.Skip(from.Frame), started);
    }

    [Fact]
    public void An_exact_position_starts_on_the_row_itself_even_inside_a_held_note()
    {
        // Speed 2, three frames a row. Channel 1 holds one note for eight rows, channel 2 plays a
        // note every row - so row 5 is a hold on channel 1, and FindPosition would go back to the
        // note at row 0, five rows early.
        var song = new Song { Speed = 2 };
        song.Instruments.Add(Instr(1));

        var held = new Pattern { Number = 1 };
        held.Rows.Add(new NoteRow(NoteName.Parse("C-3"), 1));
        for (int row = 1; row < 8; row++)
            held.Rows.Add(new HoldRow());
        var busy = new Pattern { Number = 2 };
        for (int row = 0; row < 8; row++)
            busy.Rows.Add(new NoteRow(NoteName.AllNotes[40 + row], 1));
        song.Patterns.Add(held);
        song.Patterns.Add(busy);

        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(2));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(2));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(2));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(2));

        var exact = SongSequencer.FindExactPosition(song, channel: 0, orderIndex: 1, rowIndex: 5);
        Assert.NotNull(exact);
        Assert.Equal((8 + 5) * 3, exact!.Frame);

        static List<string> Triggers(Song song, SongSequencer.SequencerPosition? from, int frames)
        {
            var heard = new List<string>();
            var frame = new List<string>();
            var sequencer = new SongSequencer(song, (_, _) => { }, from);
            sequencer.RowTriggered += (ch, pattern, row, _, _) => frame.Add($"{ch}:{pattern}:{row}");
            for (int i = 0; i < frames; i++)
            {
                frame.Clear();
                sequencer.Frame();
                heard.Add(string.Join(" ", frame));
            }

            return heard;
        }

        var fromTop = Triggers(song, null, exact.Frame + 30);
        var started = Triggers(song, exact, 30);

        // Channels 2 and 3 play row 5; channel 1 is named on the hold it is already sounding.
        Assert.Equal("1:2:5 2:2:5 0:1:5", started[0]);
        Assert.Equal(fromTop.Skip(exact.Frame + 1), started.Skip(1));
    }

    [Fact]
    public void A_resume_inside_a_held_note_names_the_row_being_held_without_retriggering_it()
    {
        // Channel 1 holds C-3 for eight rows. Resumed on row 5, it triggers nothing until row 8 -
        // and the retro view, which follows RowTriggered, had nothing to draw for it until then.
        var song = new Song { Speed = 2 };
        song.Instruments.Add(Instr(1));
        var held = new Pattern { Number = 1 };
        held.Rows.Add(new NoteRow(NoteName.Parse("C-3"), 1));
        for (int row = 1; row < 8; row++)
            held.Rows.Add(new HoldRow());
        song.Patterns.Add(held);
        for (int ch = 0; ch < 3; ch++)
            song.ChannelOrders[ch].Tokens.Add(new PatternRefToken(1));

        var from = SongSequencer.FindExactPosition(song, channel: 0, orderIndex: 0, rowIndex: 5);
        var named = new List<(int Channel, int Row)>();
        var controlWrites = new List<int>();
        var sequencer = new SongSequencer(song, (register, value) =>
        {
            if (register == 4)
                controlWrites.Add(value);
        }, from);
        sequencer.RowTriggered += (ch, _, row, _, _) => named.Add((ch, row));

        sequencer.Frame();

        Assert.Equal([(0, 5), (1, 5), (2, 5)], named);
        Assert.Empty(controlWrites);    // no hard restart, no gate - the note is not played again

        named.Clear();
        sequencer.Frame();
        Assert.Empty(named);            // once
    }
}
