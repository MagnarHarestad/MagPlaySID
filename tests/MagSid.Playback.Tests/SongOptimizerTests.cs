using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Playback.Tests;

/// <summary>
/// The optimizer rewrites pattern data, so the test that matters is not how many bytes it saves
/// but that the song still plays the same notes afterwards. These run both versions through
/// SongSequencer and compare every SID register write - the same differential check used against
/// the real assembled player. Lives in the playback test project for exactly that reason.
/// </summary>
public class SongOptimizerTests
{
    private static List<(int Offset, int Value)> Render(Song song, int frames)
    {
        var writes = new List<(int, int)>();
        var sequencer = new SongSequencer(song, (offset, value) => writes.Add((offset, value)));
        for (int i = 0; i < frames; i++)
            sequencer.Frame();
        return writes;
    }

    private static Song SongWith(params PatternRow[] rows)
    {
        var song = new Song { Speed = 1 };
        for (int n = 1; n <= 3; n++)
        {
            song.Instruments.Add(new Instrument
            {
                Number = n, Waveform = 0x41, AttackDecay = 0x09, SustainRelease = 0xAA,
            });
        }

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.AddRange(rows);
        song.Patterns.Add(pattern);
        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));
        return song;
    }

    private static NoteRow N(string note, int instrument = 1) =>
        new(NoteName.Parse(note), instrument);

    [Fact]
    public void A_looped_song_plays_exactly_what_it_played_before()
    {
        // Four distinct notes, six times over - the case a loop is unambiguously worth it.
        var rows = new List<PatternRow>();
        for (int i = 0; i < 6; i++)
        {
            rows.AddRange([N("C-4"), N("E-4"), N("G-4"), N("C-5")]);
        }

        var original = SongWith(rows.ToArray());
        var optimized = SongCloner.Clone(original);
        var result = SongOptimizer.Apply(optimized);

        Assert.True(result.BytesSaved > 0);
        Assert.Contains(optimized.Patterns[0].Rows, r => r is LoopEndRow);
        Assert.Equal(Render(original, 200), Render(optimized, 200));
    }

    [Fact]
    public void Optimizing_never_makes_a_song_larger()
    {
        // Rows with nothing worth looping: every candidate has to be rejected rather than
        // applied at a loss, which a finder trusting row counts instead of bytes would not do.
        var song = SongWith(N("C-4"), new HoldRow(), new HoldRow(), N("E-4", 2), new HoldRow());
        var before = SongSizeCalculator.Measure(song).TotalBytes;

        var result = SongOptimizer.Apply(song);

        Assert.True(result.After.TotalBytes <= before);
        Assert.Equal(before, SongSizeCalculator.Measure(song).TotalBytes);
    }

    [Fact]
    public void Dead_loop_starts_go_and_live_ones_stay()
    {
        var song = SongWith(
            new LoopStartRow(),                 // live - the LE below reaches it
            N("C-4"), N("E-4"),
            new LoopEndRow(2),
            new LoopStartRow(),                 // dead - replaced before any LE
            N("G-4"),
            new LoopStartRow(),                 // dead - nothing follows it
            N("C-5"));

        var original = SongCloner.Clone(song);
        var result = SongOptimizer.Apply(song);

        Assert.Equal(2, song.Patterns[0].Rows.Count(r => r is LoopStartRow) + 1);  // one left
        Assert.Single(song.Patterns[0].Rows.Where(r => r is LoopStartRow));
        Assert.Contains(result.Findings, f => f.Description.Contains("dead LS"));

        // Removing them emits no byte either way, so the song is untouched in both size and sound.
        Assert.Equal(0, result.BytesSaved);
        Assert.Equal(Render(original, 120), Render(song, 120));
    }

    [Fact]
    public void A_pattern_that_already_loops_is_left_alone_apart_from_dead_markers()
    {
        // An LE aims at whichever LS it last passed, so a new LS dropped into a pattern that
        // already has one could re-aim it. The optimizer declines rather than risk that.
        var rows = new List<PatternRow> { new LoopStartRow() };
        for (int i = 0; i < 4; i++)
            rows.AddRange([N("C-4"), N("E-4"), N("G-4"), N("C-5")]);
        rows.Add(new LoopEndRow(2));

        var song = SongWith(rows.ToArray());
        var original = SongCloner.Clone(song);

        SongOptimizer.Apply(song);

        Assert.Equal(original.Patterns[0].Rows, song.Patterns[0].Rows);
    }

    [Fact]
    public void Analyze_reports_without_changing_the_song()
    {
        var rows = new List<PatternRow>();
        for (int i = 0; i < 6; i++)
            rows.AddRange([N("C-4"), N("E-4"), N("G-4"), N("C-5")]);

        var song = SongWith(rows.ToArray());
        var snapshot = SongCloner.Clone(song);

        var result = SongOptimizer.Analyze(song);

        Assert.True(result.BytesSaved > 0);
        Assert.Equal(snapshot.Patterns[0].Rows, song.Patterns[0].Rows);
    }

    [Fact]
    public void A_program_tail_the_song_never_reaches_is_trimmed()
    {
        var song = SongWith(N("C-4"), new HoldRow());
        var instrument = song.Instruments[0];
        instrument.Wavetable.AddRange([0x81, 0x41, 0x21, 0x11, 0x41]);
        instrument.Filter.Add(FilterStep.Setup(0x1F, 0xF1, 0x40));
        instrument.Filter.Add(FilterStep.Sweep(0x30));
        instrument.Filter.Add(FilterStep.Sweep(0x20));

        // The play-through only ever got to wavetable step 1 and filter step 0.
        var usage = new Dictionary<int, InstrumentUsage> { [1] = new(1, -1, 0) };
        var result = SongOptimizer.Apply(song, usage);

        Assert.Equal(2, instrument.Wavetable.Count);
        Assert.Single(instrument.Filter);
        Assert.Contains(result.Findings, f => f.Description.Contains("wavetable 5 -> 2"));
        Assert.True(result.BytesSaved > 0);
    }

    [Fact]
    public void A_program_that_never_played_is_left_alone()
    {
        // -1 is "no evidence", not "reached nothing" - trimming on it would gut the instrument.
        var song = SongWith(N("C-4"));
        var instrument = song.Instruments[0];
        instrument.Wavetable.AddRange([0x81, 0x41, 0x21]);

        SongOptimizer.Apply(song, new Dictionary<int, InstrumentUsage> { [1] = new(-1, -1, -1) });

        Assert.Equal(3, instrument.Wavetable.Count);
    }

    [Fact]
    public void Without_a_play_through_nothing_is_trimmed()
    {
        var song = SongWith(N("C-4"));
        var instrument = song.Instruments[0];
        instrument.Wavetable.AddRange([0x81, 0x41, 0x21]);

        SongOptimizer.Apply(song);   // no usage at all

        Assert.Equal(3, instrument.Wavetable.Count);
    }

    [Fact]
    public void A_looping_note_program_is_never_trimmed()
    {
        // It cycles, so an unreached step means the loop never came round - shortening the list
        // would move the loop rather than cut dead weight.
        var song = SongWith(N("C-4"));
        var instrument = song.Instruments[0];
        instrument.Arpeggio.AddRange(
            [NoteName.Parse("C-4"), NoteName.Parse("E-4"), NoteName.Parse("G-4")]);
        instrument.ArpeggioEnding = ArpeggioEnding.Loop;

        SongOptimizer.Apply(song, new Dictionary<int, InstrumentUsage> { [1] = new(-1, 0, -1) });

        Assert.Equal(3, instrument.Arpeggio.Count);
    }

    [Fact]
    public void Trimming_a_tail_the_song_really_never_reaches_changes_nothing_audible()
    {
        // The evidence has to come from an actual play-through, which is the whole contract:
        // usage invented by hand can say a step went unreached when the song plays it, and the
        // trim would then cut something real. So this runs the song, takes the coverage the
        // sequencer actually gathered, trims on that, and compares the register writes.
        var original = SongWith(N("C-4"), new HoldRow());
        original.Instruments[0].Wavetable.AddRange([0x81, 0x41, 0x21, 0x11, 0x41, 0x21]);

        var probe = SongCloner.Clone(original);
        var sequencer = new SongSequencer(probe, (_, _) => { });
        for (int i = 0; i < 400; i++)
            sequencer.Frame();

        var coverage = sequencer.CoverageFor(1);
        int highest = -1;
        for (int i = coverage.Wavetable.Length - 1; i >= 0; i--)
        {
            if (coverage.Wavetable[i]) { highest = i; break; }
        }

        Assert.True(highest >= 0, "the instrument has to have played for this to prove anything");
        Assert.True(highest < 5, "the note must be short enough to leave a genuine tail");

        var trimmed = SongCloner.Clone(original);
        var result = SongOptimizer.Apply(trimmed,
            new Dictionary<int, InstrumentUsage> { [1] = new(highest, -1, -1) });

        Assert.Equal(highest + 1, trimmed.Instruments[0].Wavetable.Count);
        Assert.True(result.BytesSaved > 0);
        Assert.Equal(Render(original, 400), Render(trimmed, 400));
    }

    /// <summary>A song whose channel 1 plays pattern 1 <paramref name="runs"/> times in a row,
    /// then pattern 2 once, restarting at <paramref name="restart"/>.</summary>
    private static Song OrderedSong(int runs, int restart = 1)
    {
        var song = SongWith(N("C-4"), new HoldRow(), N("E-4", 2), new HoldRow());
        var second = new Pattern { Number = 2 };
        second.Rows.AddRange([N("G-4", 3), new HoldRow(), new HoldRow(), N("C-5")]);
        song.Patterns.Add(second);

        var order = song.ChannelOrders[0];
        order.Tokens.Clear();
        for (int i = 0; i < runs; i++)
            order.Tokens.Add(new PatternRefToken(1));
        order.Tokens.Add(new PatternRefToken(2));
        order.RestartPosition = restart;

        // The other two channels play pattern 2 only, so pattern 1 lives in channel 1's run alone.
        song.ChannelOrders[1].Tokens[0] = new PatternRefToken(2);
        song.ChannelOrders[2].Tokens[0] = new PatternRefToken(2);
        return song;
    }

    [Fact]
    public void A_pattern_the_order_repeats_five_times_becomes_one_looping_entry()
    {
        var original = OrderedSong(runs: 5);
        var optimized = SongCloner.Clone(original);

        var result = SongOptimizer.Apply(optimized);

        Assert.Equal([new PatternRefToken(1), new PatternRefToken(2)], optimized.ChannelOrders[0].Tokens);
        Assert.IsType<LoopStartRow>(optimized.Patterns[0].Rows[0]);
        Assert.Equal(new LoopEndRow(5), optimized.Patterns[0].Rows[^1]);
        Assert.Contains(result.Findings, f => f.Description.Contains("song order CH1"));
        Assert.True(result.BytesSaved > 0);

        // Well past the first wrap, so the restart is exercised too.
        Assert.Equal(Render(original, 600), Render(optimized, 600));
    }

    [Fact]
    public void Four_in_a_row_cost_as_much_as_a_loop_and_are_left_alone()
    {
        // Three order bytes saved, three LE bytes spent - not a saving, so not a change.
        var song = OrderedSong(runs: 4);
        var original = SongCloner.Clone(song);

        SongOptimizer.Apply(song);

        Assert.Equal(original.ChannelOrders[0].Tokens, song.ChannelOrders[0].Tokens);
        Assert.Equal(original.Patterns[0].Rows, song.Patterns[0].Rows);
    }

    [Fact]
    public void A_pattern_also_played_on_its_own_elsewhere_is_left_alone()
    {
        // Looping the pattern would make that single entry play five times too.
        var song = OrderedSong(runs: 6);
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(1));
        var original = SongCloner.Clone(song);

        SongOptimizer.Apply(song);

        Assert.Equal(original.ChannelOrders[0].Tokens, song.ChannelOrders[0].Tokens);
        Assert.Equal(original.Patterns[0].Rows, song.Patterns[0].Rows);
    }

    [Fact]
    public void A_restart_into_the_middle_of_a_run_is_left_alone()
    {
        // Restarting at the third of six passes has no entry to land on once they are one.
        var song = OrderedSong(runs: 6, restart: 3);
        var original = SongCloner.Clone(song);

        SongOptimizer.Apply(song);

        Assert.Equal(original.ChannelOrders[0].Tokens, song.ChannelOrders[0].Tokens);
    }

    [Fact]
    public void A_restart_after_a_run_moves_with_the_entry_it_pointed_at()
    {
        var original = OrderedSong(runs: 6, restart: 7);   // the pattern 2 entry
        var optimized = SongCloner.Clone(original);

        SongOptimizer.Apply(optimized);

        Assert.Equal(2, optimized.ChannelOrders[0].RestartPosition);
        Assert.Equal(Render(original, 800), Render(optimized, 800));
    }

    [Theory]
    [InlineData("BellaCiao.s")]
    [InlineData("Breakdance.s")]
    [InlineData("Airstrike.s")]
    [InlineData("Magnificent.s")]
    [InlineData("SOS.s")]
    [InlineData("Lightforce.s")]
    [InlineData("CityBomber4k(Megastyle).s")]
    [InlineData("MysData2026 - Clarity.s")]
    public void Real_songs_still_play_identically_after_optimizing(string file)
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "SongData", file));

        // Loudly, not a quiet return: a renamed song once skipped this check without anyone
        // noticing, which is the same as not having it.
        Assert.True(File.Exists(path), $"song file missing: {path}");

        var original = PatternsFileReader.Parse(File.ReadAllText(path), file);
        var optimized = PatternsFileReader.Parse(File.ReadAllText(path), file);
        var result = SongOptimizer.Apply(optimized);

        Assert.True(result.After.TotalBytes <= result.Before.TotalBytes);

        // Long enough for every channel to run through its whole order and restart at least once,
        // which is what the order rewrite needs covering.
        Assert.Equal(Render(original, 12000), Render(optimized, 12000));
    }
}
