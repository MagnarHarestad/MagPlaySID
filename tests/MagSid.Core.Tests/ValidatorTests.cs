using MagSid.Core.Model;
using MagSid.Core.Validation;

namespace MagSid.Core.Tests;

public class ValidatorTests
{
    private static Song ValidSong()
    {
        var song = new Song();
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xaa });

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-5"), 1));
        song.Patterns.Add(pattern);

        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        return song;
    }

    [Fact]
    public void A_well_formed_song_has_no_issues()
    {
        Assert.Empty(SongValidator.Validate(ValidSong()));
    }

    [Fact]
    public void Sixteen_instruments_is_still_fine()
    {
        var song = ValidSong();
        for (int n = 2; n <= SongValidator.MaxInstrumentNumber; n++)
            song.Instruments.Add(Instrument.CreateDefault(n));

        Assert.Empty(SongValidator.Validate(song));
    }

    [Fact]
    public void Flags_an_instrument_past_the_sixteen_a_pattern_can_select()
    {
        // A 17th instrument is not malformed - it assembles, it just can never be named by a
        // row's $C0..$CF command, so it silently costs bytes in all seven per-instrument tables
        // and in the wavetable/arpeggio/filter pools. Startup\Tables.s asserts the same limit.
        var song = ValidSong();
        for (int n = 2; n <= SongValidator.MaxInstrumentNumber + 1; n++)
            song.Instruments.Add(Instrument.CreateDefault(n));

        var issues = SongValidator.Validate(song);
        Assert.Contains(issues, i => i.Message.Contains("can never be played"));
    }

    [Fact]
    public void Flags_missing_patterns()
    {
        var song = new Song();
        var issues = SongValidator.Validate(song);
        Assert.Contains(issues, i => i.Message.Contains("No patterns defined"));
    }

    [Fact]
    public void Flags_non_contiguous_pattern_numbers()
    {
        var song = ValidSong();
        song.Patterns.Add(new Pattern { Number = 3 });

        var issues = SongValidator.Validate(song);
        Assert.Contains(issues, i => i.Message.Contains("missing PAT02"));
    }

    [Fact]
    public void Flags_non_contiguous_instrument_numbers()
    {
        var song = ValidSong();
        song.Instruments.Add(new Instrument { Number = 3 });

        var issues = SongValidator.Validate(song);
        Assert.Contains(issues, i => i.Message.Contains("missing instrument 02"));
    }

    [Fact]
    public void Allows_zero_instruments_when_no_note_row_needs_one()
    {
        var song = new Song();
        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteOffRow());
        song.Patterns.Add(pattern);
        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        Assert.Empty(SongValidator.Validate(song));
    }

    [Fact]
    public void Flags_a_note_row_referencing_an_undefined_instrument()
    {
        var song = ValidSong();
        song.Patterns[0].Rows.Add(new NoteRow(NoteName.Parse("D-4"), 99));

        var issues = SongValidator.Validate(song);
        Assert.Contains(issues, i => i.Message.Contains("instrument 99 is not defined"));
    }

    [Fact]
    public void Flags_an_order_referencing_an_undefined_pattern()
    {
        var song = ValidSong();
        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(42));

        var issues = SongValidator.Validate(song);
        Assert.Contains(issues, i => i.Message.Contains("references undefined pattern 42"));
    }

    [Fact]
    public void Flags_an_empty_order_list()
    {
        var song = ValidSong();
        song.ChannelOrders[0].Tokens.Clear();

        var issues = SongValidator.Validate(song);
        Assert.Contains(issues, i => i.Message.Contains("ORDER list must contain at least one pattern"));
    }

    [Fact]
    public void Flags_a_restart_position_out_of_range()
    {
        var song = ValidSong();
        song.ChannelOrders[0].RestartPosition = 5;

        var issues = SongValidator.Validate(song);
        Assert.Contains(issues, i => i.Message.Contains("'restart' target 5 is out of range"));
    }

    [Fact]
    public void Default_restart_position_of_1_is_always_valid()
    {
        Assert.Empty(SongValidator.Validate(ValidSong()));
    }

    [Fact]
    public void In_pattern_LS_LE_has_no_uniqueness_constraint()
    {
        var song = ValidSong();
        song.Patterns[0].Rows.Add(new LoopStartRow());
        song.Patterns[0].Rows.Add(new NoteRow(NoteName.Parse("D-4"), 1));
        song.Patterns[0].Rows.Add(new LoopEndRow());
        song.Patterns[0].Rows.Add(new LoopStartRow());
        song.Patterns[0].Rows.Add(new LoopEndRow());

        Assert.Empty(SongValidator.Validate(song));
    }
}
