using MagSid.Core.Model;

namespace MagSid.Core.Tests;

/// <summary>
/// Covers the pruning the .sid export runs. The renumbering is the risky half: pattern numbers
/// have to stay contiguous from 1 or Startup\Tables.s refuses to assemble, and every order
/// reference has to follow its pattern to the new number - get that wrong and the export builds
/// a song that plays the wrong patterns, which no compiler error would catch.
/// </summary>
public class SongPruningTests
{
    private static Song Build(int patternCount, int[] ch1, int[] ch2, int[] ch3, int restart = 1)
    {
        var song = new Song();
        for (int n = 1; n <= patternCount; n++)
        {
            var pattern = new Pattern { Number = n };
            // A distinguishing row per pattern, so a mis-mapped reference is visible.
            pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), n));
            song.Patterns.Add(pattern);
        }

        int[][] orders = [ch1, ch2, ch3];
        for (int ch = 0; ch < 3; ch++)
        {
            foreach (int n in orders[ch])
            {
                song.ChannelOrders[ch].Tokens.Add(new PatternRefToken(n));
            }
            song.ChannelOrders[ch].RestartPosition = restart;
        }

        return song;
    }

    private static int[] Numbers(ChannelOrder order) =>
        [.. order.Tokens.Select(t => ((PatternRefToken)t).PatternNumber)];

    [Fact]
    public void A_pattern_no_channel_references_is_dropped()
    {
        // PAT02 is unused; PAT03 must slide down into its number.
        var song = Build(3, [1], [3], [1]);

        var pruned = song.WithUnusedPatternsRemoved();

        Assert.Equal([1, 2], pruned.Patterns.Select(p => p.Number));
        Assert.Equal([1], Numbers(pruned.ChannelOrders[0]));
        Assert.Equal([2], Numbers(pruned.ChannelOrders[1])); // was PAT03
        Assert.Equal([1], Numbers(pruned.ChannelOrders[2]));
    }

    [Fact]
    public void Renumbering_carries_each_patterns_own_rows_with_it()
    {
        // The surviving PAT03 becomes PAT02 - its rows must come along, not PAT02's.
        var song = Build(3, [1], [3], [1]);

        var pruned = song.WithUnusedPatternsRemoved();

        var moved = pruned.Patterns.Single(p => p.Number == 2);
        Assert.Equal(3, ((NoteRow)moved.Rows[0]).InstrumentNumber); // tagged with its old number
    }

    [Fact]
    public void A_pattern_used_by_only_one_channel_survives()
    {
        var song = Build(3, [1], [2], [3]);

        var pruned = song.WithUnusedPatternsRemoved();

        Assert.Equal([1, 2, 3], pruned.Patterns.Select(p => p.Number));
    }

    [Fact]
    public void Nothing_changes_when_every_pattern_is_in_use()
    {
        var song = Build(2, [1, 2], [2], [1]);

        var pruned = song.WithUnusedPatternsRemoved();

        Assert.Equal([1, 2], pruned.Patterns.Select(p => p.Number));
        Assert.Equal([1, 2], Numbers(pruned.ChannelOrders[0]));
        Assert.Equal([2], Numbers(pruned.ChannelOrders[1]));
        Assert.Equal([1], Numbers(pruned.ChannelOrders[2]));
    }

    [Fact]
    public void The_song_being_exported_is_left_untouched()
    {
        // The whole point of returning a copy: the user's open song must not lose patterns
        // just because they exported.
        var song = Build(3, [1], [3], [1]);

        song.WithUnusedPatternsRemoved();

        Assert.Equal([1, 2, 3], song.Patterns.Select(p => p.Number));
        Assert.Equal([3], Numbers(song.ChannelOrders[1]));
    }

    [Fact]
    public void Speed_and_instruments_come_along()
    {
        var song = Build(2, [1], [2], [1]);
        song.Speed = 9;
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x41, AttackDecay = 0x12, SustainRelease = 0xAB });

        var pruned = song.WithUnusedPatternsRemoved();

        Assert.Equal(9, pruned.Speed);
        var instrument = Assert.Single(pruned.Instruments);
        Assert.Equal(0x41, instrument.Waveform);
        Assert.Equal(0xAB, instrument.SustainRelease);
        Assert.NotSame(song.Instruments[0], instrument); // copied, not shared
    }

    [Fact]
    public void An_instrument_s_full_program_survives_pruning()
    {
        // The earlier version of this copy only carried Number/Waveform/AD/SR across, silently
        // dropping WAVE/ARP/FLT/PW on every instrument that had one - which is every non-trivial
        // instrument. Caught by ear on a real export ("instrumenten blir fel"), not by this
        // suite, because the prior test above only ever exercised a bare instrument.
        var song = Build(2, [1], [2], [1]);
        var instrument = new Instrument
        {
            Number = 1,
            Waveform = 0x81,
            AttackDecay = 0x08,
            SustainRelease = 0xe8,
            ArpeggioEnding = ArpeggioEnding.Hold,
            ArpeggioLoopPoint = 2,
            PulseWidth = 5,
            PulseSweep = -3,
        };
        instrument.Wavetable.AddRange([0x09, 0x81, 0x41]);
        instrument.Arpeggio.AddRange([NoteName.Parse("C-4"), NoteName.Parse("D-4")]);
        instrument.Filter.Add(FilterStep.Setup(0x1f, 0xf1, 0x08, 1));
        song.Instruments.Add(instrument);

        var pruned = song.WithUnusedPatternsRemoved();

        var copy = Assert.Single(pruned.Instruments);
        Assert.Equal(instrument.Wavetable, copy.Wavetable);
        Assert.Equal(instrument.Arpeggio, copy.Arpeggio);
        Assert.Equal(ArpeggioEnding.Hold, copy.ArpeggioEnding);
        Assert.Equal(2, copy.ArpeggioLoopPoint);
        Assert.Equal(5, copy.PulseWidth);
        Assert.Equal(-3, copy.PulseSweep);
        Assert.Single(copy.Filter);
    }

    [Fact]
    public void A_restart_position_past_the_surviving_entries_is_clamped_into_range()
    {
        // Both entries point at patterns that survive, so nothing is dropped here - but a file
        // hand-edited with a restart beyond the list must still come out assemblable.
        var song = Build(2, [1, 2], [1], [1], restart: 5);

        var pruned = song.WithUnusedPatternsRemoved();

        Assert.Equal(2, pruned.ChannelOrders[0].RestartPosition);
        Assert.Equal(1, pruned.ChannelOrders[1].RestartPosition);
    }
}
