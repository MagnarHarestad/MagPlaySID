using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Core.Tests;

/// <summary>
/// The optional ARP / FLT / PW sections on an INSTRUMENTS: line. These exist first and
/// foremost in Startup\Tables.s; the point of these tests is that the C# grammar accepts and
/// re-emits exactly what the Lua one does, since anything the writer drops is silently lost
/// from the user's song the first time they save it.
/// </summary>
public class InstrumentSectionTests
{
    private static string WrapSong(string instrumentLine) =>
        $"""
         var v_Song_Speed = 6

         INSTRUMENTS:
         {instrumentLine}
         END:

         PAT01:
         "C-1", 01
         END:

         ORDER_CH1:
         1
         END:

         ORDER_CH2:
         1
         END:

         ORDER_CH3:
         1
         END:
         """;

    private static Instrument ReadInstrument(string instrumentLine) =>
        PatternsFileReader.Parse(WrapSong(instrumentLine), "test").Instruments.Single();

    [Fact]
    public void A_bare_instrument_line_still_parses_and_gets_the_default_pulse_settings()
    {
        var instrument = ReadInstrument("01, $21, $00, $ac");

        Assert.Empty(instrument.Arpeggio);
        Assert.Empty(instrument.Filter);
        Assert.Equal(Instrument.DefaultPulseWidth, instrument.PulseWidth);
        Assert.Equal(0, instrument.PulseSweep);
        Assert.True(instrument.HasDefaultPulse);
    }

    [Fact]
    public void An_arpeggio_parses_into_absolute_notes()
    {
        var instrument = ReadInstrument("""01, $21, $00, $ac, ARP "C-1" "D#1" "G-1" """.TrimEnd());

        Assert.Equal(
            [NoteName.Parse("C-1"), NoteName.Parse("D#1"), NoteName.Parse("G-1")],
            instrument.Arpeggio);
    }

    [Fact]
    public void A_filter_program_distinguishes_setup_groups_from_bare_cutoff_steps()
    {
        var instrument = ReadInstrument("01, $21, $00, $ac, FLT ($1f $f1 $08) $10 $20");

        Assert.Equal(3, instrument.Filter.Count);

        Assert.True(instrument.Filter[0].IsSetup);
        Assert.Equal((byte)0x1f, instrument.Filter[0].ModeVolume);
        Assert.Equal((byte)0xf1, instrument.Filter[0].ResonanceRouting);
        Assert.Equal((byte)0x08, instrument.Filter[0].Cutoff);

        Assert.False(instrument.Filter[1].IsSetup);
        Assert.Equal((byte)0x10, instrument.Filter[1].Cutoff);
        Assert.Equal((byte)0x20, instrument.Filter[2].Cutoff);
    }

    [Theory]
    [InlineData("PW 8 +2", 8, 2)]
    [InlineData("PW 2 -3", 2, -3)]
    [InlineData("PW 0 0", 0, 0)]
    public void A_pulse_section_parses_width_and_signed_sweep(string section, int width, int sweep)
    {
        var instrument = ReadInstrument($"01, $21, $00, $ac, {section}");

        Assert.Equal(width, instrument.PulseWidth);
        Assert.Equal(sweep, instrument.PulseSweep);
    }

    [Fact]
    public void Sections_may_appear_in_any_order()
    {
        var instrument = ReadInstrument(
            """01, $41, $00, $ac, PW 2 -1, FLT ($1f $f1 $08), ARP "C-1" "E-1" """.TrimEnd());

        Assert.Equal(2, instrument.Arpeggio.Count);
        Assert.Single(instrument.Filter);
        Assert.Equal(2, instrument.PulseWidth);
        Assert.Equal(-1, instrument.PulseSweep);
    }

    [Fact]
    public void An_instrument_line_with_every_section_round_trips_byte_for_byte()
    {
        // Written in the writer's own canonical order (ARP, FLT, PW) so the comparison is a
        // real round-trip and not just "the reader tolerated it".
        const string line = """01, $41, $00, $ac, ARP "C-1" "D#1", FLT ($1f $f1 $08) $20, PW 2 -3""";

        var song = PatternsFileReader.Parse(WrapSong(line), "test");
        string written = PatternsFileWriter.Render(song, "test");

        Assert.Contains(line, written);
    }

    [Fact]
    public void Default_pulse_settings_are_left_out_so_a_plain_line_round_trips_unchanged()
    {
        var song = PatternsFileReader.Parse(WrapSong("01, $21, $00, $ac"), "test");
        string written = PatternsFileWriter.Render(song, "test");

        // The line itself carries no sections. ("PW" still appears in the header comment above
        // it, which is why this looks at the instrument line rather than the whole file.)
        Assert.Contains("01, $21, $00, $ac\n", written);
        string instrumentLine = written.Split('\n').Single(l => l.StartsWith("01,"));
        Assert.Equal("01, $21, $00, $ac", instrumentLine);
    }

    [Fact]
    public void A_wavetable_parses_into_one_control_byte_per_frame()
    {
        var instrument = ReadInstrument("01, $41, $00, $b8, WAVE $81 $81 $41 $41");

        Assert.Equal<byte[]>([0x81, 0x81, 0x41, 0x41], [.. instrument.Wavetable]);
    }

    [Theory]
    [InlineData("ARP \"C-4\" \"C-3\"", 2, ArpeggioEnding.Loop)]
    [InlineData("ARP \"C-4\" \"C-3\" HOLD", 2, ArpeggioEnding.Hold)]
    [InlineData("ARP \"C-6\" ROOT", 1, ArpeggioEnding.Root)]
    [InlineData("ARP ROOT", 0, ArpeggioEnding.Root)]
    public void A_note_programs_ending_is_read_from_its_last_token(
        string section, int noteCount, ArpeggioEnding ending)
    {
        var instrument = ReadInstrument($"01, $41, $00, $b8, {section}");

        Assert.Equal(noteCount, instrument.Arpeggio.Count);
        Assert.Equal(ending, instrument.ArpeggioEnding);
        Assert.True(instrument.HasArpeggio);
    }

    [Theory]
    [InlineData("01, $41, $00, $a8, WAVE $81 $41, ARP \"C-6\" ROOT")]
    [InlineData("01, $41, $00, $b8, WAVE $81 $81 $41 $41, ARP \"C-4\" \"C-3\" HOLD")]
    [InlineData("01, $21, $00, $ac, ARP \"C-1\" \"D#1\"")]
    public void A_drum_or_bass_instrument_round_trips_byte_for_byte(string line)
    {
        var song = PatternsFileReader.Parse(WrapSong(line), "test");
        string written = PatternsFileWriter.Render(song, "test");

        Assert.Contains(line, written);
    }

    [Fact]
    public void A_LOOP_marker_makes_the_program_cycle_only_its_tail()
    {
        // The drum shape every one of the ported songs uses: strike, then ring out on the last
        // two notes rather than re-striking the attack every time round.
        var instrument = ReadInstrument(
            "01, $81, $00, $c7, ARP \"B-7\" \"B-7\" \"G-3\" LOOP \"E-3\" \"B-7\"");

        Assert.Equal(5, instrument.Arpeggio.Count);       // LOOP is a position, not a step
        Assert.Equal(3, instrument.ArpeggioLoopPoint);
        Assert.Equal(ArpeggioEnding.Loop, instrument.ArpeggioEnding);
    }

    [Theory]
    [InlineData("01, $81, $00, $c7, ARP \"B-7\" \"G-3\" LOOP \"E-3\" \"B-7\"")]
    [InlineData("01, $41, $00, $a8, FLT ($3f $13 $80 +1)")]
    [InlineData("01, $41, $00, $a8, FLT ($3f $13 $80 -2) $40, PW 2 -3")]
    [InlineData("01, $41, $00, $a8, PW 2 +22 CONT")]
    [InlineData("01, $41, $00, $a8, NOHR")]
    [InlineData("01, $41, $00, $a8, PW 6 +24 CONT, NOHR")]
    [InlineData("01, $41, $00, $a8, VIB $32 $10")]
    [InlineData("01, $41, $00, $a8, SLIDE +8")]
    [InlineData("01, $41, $00, $a8, SLIDE -128, VIB $32 $10, NOHR")]
    [InlineData("01, $41, $00, $a8, PW 6 +24, VIB $0f $ff, NOHR")]
    public void The_continuous_pulse_flag_and_the_filter_sweep_rate_round_trip(string line)
    {
        var song = PatternsFileReader.Parse(WrapSong(line), "test");
        string written = PatternsFileWriter.Render(song, "test");

        Assert.Contains(line, written);
    }

    [Fact]
    public void The_continuous_pulse_flag_is_off_by_default()
    {
        var plain = ReadInstrument("01, $41, $00, $a8, PW 2 +22 CONT");
        Assert.True(plain.PulseContinuous);

        var neither = ReadInstrument("01, $41, $00, $a8, PW 2 +22");
        Assert.False(neither.PulseContinuous);
    }

    [Fact]
    public void The_no_hard_restart_flag_is_bare_and_off_by_default()
    {
        var flagged = ReadInstrument("01, $41, $00, $a8, NOHR");
        Assert.True(flagged.NoHardRestart);

        var plain = ReadInstrument("01, $41, $00, $a8");
        Assert.False(plain.NoHardRestart);

        var withPulse = ReadInstrument("01, $41, $00, $a8, PW 2 +22 CONT, NOHR");
        Assert.True(withPulse.NoHardRestart);
        Assert.True(withPulse.PulseContinuous);
    }

    [Fact]
    public void Vibrato_reads_delay_and_speed_from_one_byte_and_depth_from_the_next()
    {
        var vibrato = ReadInstrument("01, $41, $00, $a8, VIB $32 $10");
        Assert.Equal(0x32, vibrato.VibratoDelaySpeed);
        Assert.Equal(0x10, vibrato.VibratoDepth);
        Assert.True(vibrato.HasVibrato);

        Assert.False(ReadInstrument("01, $41, $00, $a8").HasVibrato);
    }

    [Theory]
    [InlineData("01, $41, $00, $a8, VIB $32 $00")]
    [InlineData("01, $41, $00, $a8, VIB $32")]
    [InlineData("01, $41, $00, $a8, VIB $32 $10 $20")]
    [InlineData("01, $41, $00, $a8, VIB $32 $10, VIB $32 $10")]
    public void A_malformed_or_duplicate_VIB_is_rejected(string line)
    {
        Assert.Throws<PatternsFormatException>(() => ReadInstrument(line));
    }

    [Theory]
    [InlineData("01, $41, $00, $a8, SLIDE 0")]
    [InlineData("01, $41, $00, $a8, SLIDE +128")]
    [InlineData("01, $41, $00, $a8, SLIDE")]
    [InlineData("01, $41, $00, $a8, SLIDE +8, SLIDE +8")]
    public void A_malformed_or_duplicate_SLIDE_is_rejected(string line)
    {
        Assert.Throws<PatternsFormatException>(() => ReadInstrument(line));
    }

    [Fact]
    public void A_duplicate_NOHR_is_rejected()
    {
        Assert.Throws<PatternsFormatException>(() => ReadInstrument("01, $41, $00, $a8, NOHR, NOHR"));
    }

    [Fact]
    public void A_filter_group_without_a_rate_means_no_sweep_and_stays_unwritten()
    {
        var instrument = ReadInstrument("01, $41, $00, $a8, FLT ($1f $f1 $08)");

        Assert.Equal(0, instrument.Filter.Single().SweepRate);

        var song = PatternsFileReader.Parse(WrapSong("01, $41, $00, $a8, FLT ($1f $f1 $08)"), "test");
        Assert.Contains("FLT ($1f $f1 $08)", PatternsFileWriter.Render(song, "test"));
    }

    [Theory]
    [InlineData("01, $21, $00, $ac, PW 8 +2 WIDE", "unknown PW option")]
    [InlineData("01, $21, $00, $ac, FLT ($1f $f1 $08 x)", "cannot parse FLT group token")]
    public void Malformed_sweep_options_are_rejected(string instrumentLine, string expectedFragment)
    {
        var ex = Assert.Throws<PatternsFormatException>(() => ReadInstrument(instrumentLine));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("01, $21, $00, $ac, WAVE $00", "ends a program")]
    [InlineData("01, $21, $00, $ac, ARP HOLD", "needs a note before it")]
    [InlineData("01, $21, $00, $ac, ARP ROOT \"C-4\"", "nothing may follow")]
    [InlineData("01, $21, $00, $ac, ARP WOBBLE", "cannot parse ARP token")]
    [InlineData("01, $21, $00, $ac, ARP \"C-4\" LOOP", "at least one note after it")]
    public void Malformed_wavetable_and_ending_tokens_are_rejected(
        string instrumentLine, string expectedFragment)
    {
        var ex = Assert.Throws<PatternsFormatException>(() => ReadInstrument(instrumentLine));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("01, $21, $00, $ac, FLT $00", "cutoff")]        // $00 ends a program
    [InlineData("01, $21, $00, $ac, FLT $ff", "cutoff")]        // $ff marks a setup step
    [InlineData("01, $21, $00, $ac, PW 16 +0", "0..15")]
    [InlineData("01, $21, $00, $ac, ARP \"H-1\"", "not a note")]
    [InlineData("01, $21, $00, $ac, FLT ($1f $f1)", "FLT group")]
    [InlineData("01, $21, $00, $ac, WOBBLE 3", "unknown section")]
    [InlineData("01, $21, $00, $ac, PW 6 +24 NARROW", "unknown PW option")]
    [InlineData("01, $21, $00, $ac, PWT $10 $30", "unknown section")]
    public void Malformed_sections_are_rejected_with_a_message_naming_the_problem(
        string instrumentLine, string expectedFragment)
    {
        var ex = Assert.Throws<PatternsFormatException>(() => ReadInstrument(instrumentLine));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
