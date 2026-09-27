namespace MagSid.Assembler.Tests;

/// <summary>
/// Compares PlayerProgram's output against the player c6510 assembles from Code\musicplayer.s, byte
/// for byte, for every song in the tree.
///
/// This is the whole justification for the port. A transcription slip produces a player that runs
/// and sounds subtly wrong - a wrong envelope register, a branch one instruction off - and that is
/// the worst failure mode available here, because it passes every smoke test and only a listener
/// notices. Reading the port over does not catch it. This does.
///
/// Scope: the player code only, up to where the song tables begin. The tables' own contents are the
/// next step; their addresses are taken from the reference build and fed in as resolved labels, so
/// every absolute reference from the code into them is still compared.
/// </summary>
public class PlayerImageTests
{
    /// <summary>The four compile-time constants Startup\Tables.s derives from the song. Read back
    /// out of the reference image here rather than re-derived, because deriving them is the song-data
    /// emitter's job and is not what this test is about. Everything else is compared, not copied.</summary>
    private readonly record struct SongConstants(int SongSpeed, int Order1, int Order2, int Order3);

    /// <summary>Where the fixture drivers live, as a path from the player root - which is both what
    /// AssembleDriver checks and what c6510 is handed, since it runs with the player root as its
    /// working directory.</summary>
    private const string Fixtures = "../MagSID_Editor/tests/MagSid.Assembler.Tests/Fixtures/";

    [Theory]
    // The real songs. Between them these only reach two of the four feature combinations: five are
    // bare (462 bytes of player) and BellaCiao has vibrato (574).
    [InlineData("export_airstrike.s")]
    [InlineData("export_bellaciao.s")]
    [InlineData("export_breakdance.s")]
    [InlineData("export_lightforce.s")]
    [InlineData("export_magnificent.s")]
    [InlineData("export_megastyle.s")]
    [InlineData("export_sos.s")]
    // Fixtures for the other two. Nothing in SongData uses SLIDE, so without these the slide code
    // and the shared frequency-modulation block with both effects on would never be compared -
    // and a gated feature that no song enables is exactly how a broken vibrato declaration passed
    // a green test suite earlier in this project.
    [InlineData(Fixtures + "export_slide_only.s")]
    [InlineData(Fixtures + "export_slide_and_vib.s")]
    public void Player_code_is_byte_identical_to_c6510(string driver)
    {
        var (reference, symbols) = C6510.AssembleDriver(driver);
        const int origin = 0x1000;

        var byName = symbols
            .GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.First().Address);

        // The code ends where the first song table begins. tbl_rows_pat01 is the first thing
        // Tables.s emits after the player, for every song.
        int codeEnd = byName["tbl_rows_pat01"];
        int codeLength = codeEnd - origin;

        var features = new PlayerFeatures(
            Vibrato: byName.ContainsKey("vib_ds"),
            Slide: byName.ContainsKey("slide_rate"));

        var zp = new ZeroPageLayout(features);
        AssertZeroPageMatches(zp, byName, features);

        var constants = ReadSongConstants(reference, origin, byName);

        var asm = new Emitter6502(origin);
        var tables = ResolvedTables(asm, byName, features);
        PlayerProgram.Emit(
            asm, tables,
            new PlayerParameters(constants.SongSpeed, constants.Order1, constants.Order2, constants.Order3),
            features, zp);

        byte[] mine = asm.Assemble();

        Assert.True(mine.Length == codeLength,
            $"{driver}: emitted {mine.Length} bytes of player code, c6510 produced {codeLength} " +
            $"(features: vibrato={features.Vibrato}, slide={features.Slide}). " +
            Locate(asm, symbols, Math.Min(mine.Length, codeLength), origin));

        for (int i = 0; i < codeLength; i++)
        {
            if (mine[i] != reference[i])
            {
                Assert.Fail(
                    $"{driver}: first difference at ${origin + i:X4} - emitted ${mine[i]:X2}, " +
                    $"c6510 has ${reference[i]:X2}. {Locate(asm, symbols, i, origin)}" +
                    Environment.NewLine +
                    $"  emitted: {Hex(mine, i)}" + Environment.NewLine +
                    $"  c6510:   {Hex(reference, i)}");
            }
        }
    }

    /// <summary>Zero page is checked separately because getting it wrong shows up as dozens of
    /// unrelated-looking byte differences, and because a name declared one line out of order there
    /// moves every variable after it.</summary>
    private static void AssertZeroPageMatches(
        ZeroPageLayout zp, Dictionary<string, int> byName, PlayerFeatures features)
    {
        var expected = new List<(string Name, int Address)>
        {
            ("ZP_lb", zp.ZpLb),
            ("ZP_hb", zp.ZpHb),
            ("ZP_ch", zp.ZpCh),
            ("Song_Speed", zp.SongSpeed),
            ("voice_patternidx", zp.VoicePatternIdx),
            ("voice_event", zp.VoiceEvent),
            ("voice_duration", zp.VoiceDuration),
            ("voice_rowdur", zp.VoiceRowDur),
            ("voice_note", zp.VoiceNote),
            ("voice_wt_pos", zp.VoiceWtPos),
            ("voice_arp_pos", zp.VoiceArpPos),
            ("voice_root", zp.VoiceRoot),
            ("voice_pw_lo", zp.VoicePwLo),
            ("voice_pw_hi", zp.VoicePwHi),
            ("voice_inst", zp.VoiceInst),
            ("voice_prev_pw", zp.VoicePrevPw),
            ("voice_loop_counter", zp.VoiceLoopCounter),
            ("voice_last_waveform", zp.VoiceLastWaveform),
            ("filter_pos", zp.FilterPos),
            ("filter_phase", zp.FilterPhase),
            ("filter_delta", zp.FilterDelta),
        };

        if (features.FreqMod)
        {
            expected.AddRange(new[]
            {
                ("voice_freq_lo", zp.VoiceFreqLo),
                ("voice_freq_hi", zp.VoiceFreqHi),
                ("voice_off_lo", zp.VoiceOffLo),
                ("voice_off_hi", zp.VoiceOffHi),
            });
        }

        if (features.Vibrato)
        {
            expected.AddRange(new[]
            {
                ("voice_vib_dir", zp.VoiceVibDir),
                ("voice_vib_cnt", zp.VoiceVibCnt),
                ("voice_vib_delay", zp.VoiceVibDelay),
            });
        }

        var wrong = expected
            .Where(e => !byName.TryGetValue(e.Name, out int actual) || actual != e.Address)
            .Select(e => byName.TryGetValue(e.Name, out int actual)
                ? $"{e.Name}: layout says ${e.Address:X2}, c6510 says ${actual:X2}"
                : $"{e.Name}: not in the symbol file at all")
            .ToList();

        Assert.True(wrong.Count == 0,
            "Zero-page layout disagrees with c6510:" + Environment.NewLine +
            string.Join(Environment.NewLine, wrong));
    }

    private static SongConstants ReadSongConstants(byte[] reference, int origin, Dictionary<string, int> byName)
    {
        int orderPos = byName["voice_orderpos"] - origin;

        // ONbeat.PlaySong opens with "lda #v_Song_Speed", so the immediate is the byte after it.
        int songSpeed = reference[byName["ONbeat_PlaySong"] - origin + 1];

        return new SongConstants(
            songSpeed,
            reference[orderPos],
            reference[orderPos + 1],
            reference[orderPos + 2]);
    }

    /// <summary>The table labels, already resolved to where the reference build put them. The
    /// emitter treats a label's address as given, so this is exactly how the song-data emitter will
    /// hand them over once it places the tables itself.</summary>
    private static SongTableLabels ResolvedTables(
        Emitter6502 asm, Dictionary<string, int> byName, PlayerFeatures features)
    {
        Label At(string name)
        {
            var label = asm.NewLabel(name);
            label.Address = byName[name];
            return label;
        }

        return new SongTableLabels
        {
            TblRowsLo = At("tbl_rows_lo"),
            TblRowsHi = At("tbl_rows_hi"),
            TblSongOrder = At("tbl_songorder"),
            NoteFreqLow = At("note_freq_low"),
            NoteFreqHigh = At("note_freq_high"),
            AdTable = At("ad_table"),
            SrTable = At("sr_table"),
            WtStart = At("wt_start"),
            WtData = At("wt_data"),
            ArpStart = At("arp_start"),
            ArpData = At("arp_data"),
            FltStart = At("flt_start"),
            FltData = At("flt_data"),
            PwInit = At("pw_init"),
            PwAdd = At("pw_add"),
            VibDs = features.Vibrato ? At("vib_ds") : null,
            VibDepth = features.Vibrato ? At("vib_depth") : null,
            SlideRate = features.Slide ? At("slide_rate") : null,
        };
    }

    /// <summary>Names the routine an offset falls in, from both sides - c6510's symbol file and the
    /// emitter's own labels - so a failure says "in Voice_Automation, 6 bytes in" instead of only
    /// giving an address.</summary>
    private static string Locate(Emitter6502 asm, List<C6510.Symbol> symbols, int offset, int origin)
    {
        int address = origin + offset;

        var theirs = symbols
            .Where(s => s.Address <= address && s.Address >= origin)
            .OrderByDescending(s => s.Address)
            .FirstOrDefault();

        var mine = asm.DefinedLabels
            .Where(l => l.Address is { } a && a <= address)
            .OrderByDescending(l => l.Address!.Value)
            .FirstOrDefault();

        string theirSide = theirs.Name is null
            ? "before any c6510 label"
            : $"c6510: {theirs.Name} + {address - theirs.Address}";
        string mySide = mine is null
            ? "before any emitted label"
            : $"emitter: {mine.Name} + {address - mine.Address!.Value}";

        return $"({theirSide}; {mySide})";
    }

    private static string Hex(byte[] bytes, int around)
    {
        int start = Math.Max(0, around - 4);
        int end = Math.Min(bytes.Length, around + 8);
        return string.Join(" ", Enumerable.Range(start, end - start)
            .Select(i => i == around ? $"[{bytes[i]:X2}]" : $"{bytes[i]:X2}"));
    }
}
