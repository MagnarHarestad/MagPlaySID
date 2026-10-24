using MagSid.Core.Model;

namespace MagSid.Assembler;

/// <summary>
/// The whole thing: player code followed by song data, as the bytes that load at a chosen address.
/// This is what replaces shelling out to c6510 for a .sid export.
///
/// The section order below is not a choice. It was read out of the symbol file of a real build,
/// because the player reaches its tables by absolute address and the .sid is one contiguous block -
/// so while any order would assemble, only this one produces the same bytes the assembler does, and
/// that byte-for-byte match with c6510 is the only evidence the port is right.
/// </summary>
public static class SongImageBuilder
{
    /// <summary>Emits at <paramref name="loadAddress"/>. Nothing is position-fixed - the player has
    /// no alignment requirements and no baked-in addresses - so this is the whole of relocation.</summary>
    public static byte[] Build(Song song, int loadAddress = Core.SidLoadAddress.Default)
    {
        var asm = new Emitter6502(loadAddress);

        var features = new PlayerFeatures(
            Vibrato: song.Instruments.Any(i => i.HasVibrato),
            Slide: Core.SongSizeCalculator.UsesSlide(song),
            SoftHardRestart: song.Instruments.Any(i => i.SoftHardRestart),
            Funk: song.HasFunk);

        var notes = NoteDictionary.Build(song);
        var instrumentTables = InstrumentTables.Build(song, notes);
        var order = SongOrderTable.Build(song);
        var patterns = song.Patterns.OrderBy(p => p.Number).ToList();
        RequireContiguous(patterns, song.Instruments);

        var patternLabels = patterns
            .Select(p => asm.NewLabel($"tbl_rows_pat{p.Number:00}"))
            .ToList();

        var tables = new SongTableLabels
        {
            TblRowsLo = asm.NewLabel("tbl_rows_lo"),
            TblRowsHi = asm.NewLabel("tbl_rows_hi"),
            AdTable = asm.NewLabel("ad_table"),
            SrTable = asm.NewLabel("sr_table"),
            WtStart = asm.NewLabel("wt_start"),
            WtData = asm.NewLabel("wt_data"),
            ArpStart = asm.NewLabel("arp_start"),
            ArpData = asm.NewLabel("arp_data"),
            FltStart = asm.NewLabel("flt_start"),
            FltData = asm.NewLabel("flt_data"),
            PwInit = asm.NewLabel("pw_init"),
            PwAdd = asm.NewLabel("pw_add"),
            VibDs = features.Vibrato ? asm.NewLabel("vib_ds") : null,
            VibDepth = features.Vibrato ? asm.NewLabel("vib_depth") : null,
            TblSongOrder = asm.NewLabel("tbl_songorder"),
            NoteFreqLow = asm.NewLabel("note_freq_low"),
            NoteFreqHigh = asm.NewLabel("note_freq_high"),
            WtHold = instrumentTables.WavetablePool.Length - 1,
        };

        PlayerProgram.Emit(
            asm, tables,
            new PlayerParameters(song.Speed, order.Start1, order.Start2, order.Start3) { FunkSpeed = song.FunkSpeed ?? song.Speed },
            features, new ZeroPageLayout(features));

        // --- song data, in the order a real build puts it ---

        for (int i = 0; i < patterns.Count; i++)
        {
            asm.Define(patternLabels[i]);
            asm.Bytes(PatternEncoder.Encode(patterns[i], notes));
        }

        // One shared pointer pair indexed by 1-based pattern number, so a pattern used by several
        // channels points at the same rows. Entry 0 is a dummy: 0 is the song-order end marker, so
        // a pattern can never be numbered 0 and the table would otherwise need biasing at runtime.
        asm.Define(tables.TblRowsLo);
        asm.Byte(0);
        foreach (var label in patternLabels)
            asm.Byte(LowByte(label));

        asm.Define(tables.TblRowsHi);
        asm.Byte(0);
        foreach (var label in patternLabels)
            asm.Byte(HighByte(label));

        EmitTable(asm, tables.AdTable, instrumentTables.AttackDecay);
        EmitTable(asm, tables.SrTable, instrumentTables.SustainRelease);
        EmitTable(asm, tables.WtStart, instrumentTables.WavetableStart);
        EmitTable(asm, tables.WtData, instrumentTables.WavetablePool);
        EmitTable(asm, tables.ArpStart, instrumentTables.ArpeggioStart);
        EmitTable(asm, tables.ArpData, instrumentTables.ArpeggioPool);
        EmitTable(asm, tables.FltStart, instrumentTables.FilterStart);
        EmitTable(asm, tables.FltData, instrumentTables.FilterPool);
        EmitTable(asm, tables.PwInit, instrumentTables.PulseInit);
        EmitTable(asm, tables.PwAdd, instrumentTables.PulseAdd);

        if (features.Vibrato)
        {
            EmitTable(asm, tables.VibDs!, instrumentTables.VibratoDelaySpeed);
            EmitTable(asm, tables.VibDepth!, instrumentTables.VibratoDepth);
        }

        EmitTable(asm, tables.TblSongOrder, order.Bytes);

        var (freqLow, freqHigh) = notes.FrequencyTables();
        EmitTable(asm, tables.NoteFreqLow, freqLow);
        EmitTable(asm, tables.NoteFreqHigh, freqHigh);

        return asm.Assemble();
    }

    /// <summary>An empty table is still one byte. Startup\Tables.s's EmitByteTable writes ".byte 0"
    /// for an empty list rather than nothing at all, and a label with no bytes under it would put
    /// the next section at the same address.</summary>
    private static void EmitTable(Emitter6502 asm, Label label, byte[] values)
    {
        asm.Define(label);
        if (values.Length == 0)
            asm.Byte(0);
        else
            asm.Bytes(values);
    }

    /// <summary>The pointer tables store the two halves of an address separately, so each entry is
    /// resolved by hand - the emitter's own Word() would put them side by side.</summary>
    private static byte LowByte(Label label) => (byte)(Resolved(label) & 0xFF);

    private static byte HighByte(Label label) => (byte)(Resolved(label) >> 8);

    private static int Resolved(Label label) => label.Address
        ?? throw new InvalidOperationException(
            $"{label.Name} is referenced by a pointer table before its bytes were emitted - the " +
            "patterns have to be laid down before tbl_rows_lo/hi can split their addresses.");

    /// <summary>Both numberings must run 1..N with no gaps. The pointer pair is indexed by pattern
    /// number and the instrument tables by number - 1, so a gap would silently read the wrong
    /// entry rather than fail. Startup\Tables.s asserts the same two things.</summary>
    private static void RequireContiguous(List<Pattern> patterns, List<Instrument> instruments)
    {
        for (int i = 0; i < patterns.Count; i++)
        {
            if (patterns[i].Number != i + 1)
            {
                throw new SongTooLargeException(
                    "Pattern numbers must be contiguous starting at 1 - " +
                    $"expected PAT{i + 1:00} but found PAT{patterns[i].Number:00}.");
            }
        }

        var numbers = instruments.Select(i => i.Number).OrderBy(n => n).ToList();
        for (int i = 0; i < numbers.Count; i++)
        {
            if (numbers[i] != i + 1)
            {
                throw new SongTooLargeException(
                    "Instrument numbers must be contiguous starting at 1 - " +
                    $"expected {i + 1:00} but found {numbers[i]:00}.");
            }
        }

        // The stream selects an instrument with $C0..$CF, so 16 is all the player can reach.
        if (numbers.Count > 16)
        {
            throw new SongTooLargeException(
                $"{numbers.Count} instruments are defined, but a pattern can only select 1..16 " +
                "($C0..$CF) - the rest could never be played and would only cost bytes.");
        }
    }
}
