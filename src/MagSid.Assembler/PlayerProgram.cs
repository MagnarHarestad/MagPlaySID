namespace MagSid.Assembler;

using static Operand;

/// <summary>The song tables the player reads. They are emitted after the code, so they arrive here
/// as labels the caller has not placed yet - the reason the emitter resolves references late.
/// VibDs/VibDepth are null exactly when vibrato is off.</summary>
public sealed class SongTableLabels
{
    public required Label TblRowsLo { get; init; }
    public required Label TblRowsHi { get; init; }
    public required Label TblSongOrder { get; init; }
    public required Label NoteFreqLow { get; init; }
    public required Label NoteFreqHigh { get; init; }
    public required Label AdTable { get; init; }
    public required Label SrTable { get; init; }
    public required Label WtStart { get; init; }
    public required Label WtData { get; init; }
    public required Label ArpStart { get; init; }
    public required Label ArpData { get; init; }
    public required Label FltStart { get; init; }
    public required Label FltData { get; init; }
    public required Label PwInit { get; init; }
    public required Label PwAdd { get; init; }

    public Label? VibDs { get; init; }
    public Label? VibDepth { get; init; }

    /// <summary>wt_hold - wt_data: the wavetable pool's last byte, always the $00 that ends the
    /// last program. A note off parks voice_wt_pos there.</summary>
    public required int WtHold { get; init; }
}

/// <summary>The per-song constants baked into the player code: v_Song_Speed and the three
/// v_ord_startN values that seed voice_orderpos.</summary>
public sealed record PlayerParameters(int SongSpeed, int OrderStart1, int OrderStart2, int OrderStart3)
{
    /// <summary>v_channels. Three throughout this project; named rather than inlined because the
    /// assembly writes "ldx #v_channels-1" and reading 2 there invites the off-by-one.</summary>
    public int Channels { get; init; } = 3;
}

/// <summary>
/// Code\musicplayer.s, emitted as bytes.
///
/// A line-for-line port, kept in the source's own order because that order is load-bearing: branch
/// distances depend on it, and so does the fall-through from Filter_Play into the sweep and from
/// the command dispatch into .loopend. Where the assembly uses a library macro (move8, m_setptr_x)
/// the expansion is written out, since that is what the macro emits.
///
/// The musicshower blocks are left out. They exist for the demo's on-screen display, which a .sid
/// export has no use for, and the export driver does not define musicshower either - so including
/// them would produce an image the assembler never builds.
///
/// PlayerImageTests compares the result byte for byte against what c6510 assembles from the real
/// source. That comparison is the only reason to trust any of this: a transcription slip here
/// yields a player that runs and sounds subtly wrong, which no amount of reading catches.
/// </summary>
public static class PlayerProgram
{
    // Pattern-stream command bytes. Same values as the vars at the top of musicplayer.s.
    private const int CmdDur = 0x80;
    private const int CmdInst = 0xC0;
    private const int CmdOff = 0xD0;
    private const int LoopEnd = 0xD2;
    private const int CmdSlide = 0xD3;
    private const int RowWait = 0xD1;

    /// <summary>voice_note holds a note id or one of these, deliberately the same bytes the stream
    /// uses so Voice_Tick can store the command straight from A.</summary>
    private const int NoteOff = CmdOff;

    // SID registers, written through the per-channel offset in Y.
    private const int SidFreqLo = 0xD400;
    private const int SidFreqHi = 0xD401;
    private const int SidPwLo = 0xD402;
    private const int SidPwHi = 0xD403;
    private const int SidControl = 0xD404;
    private const int SidAttackDecay = 0xD405;
    private const int SidSustainRelease = 0xD406;
    private const int SidFilterCutoffHi = 0xD416;
    private const int SidFilterResonance = 0xD417;
    private const int SidFilterModeVolume = 0xD418;

    public static void Emit(
        Emitter6502 asm,
        SongTableLabels tables,
        PlayerParameters parameters,
        PlayerFeatures features,
        ZeroPageLayout zp)
    {
        RequireTables(tables, features);

        // Every label up front, because the code references most of them before defining them and
        // grouping them here keeps the emit sequence readable as assembly.
        var musicInit = asm.NewLabel("music.init");
        var musicPlay = asm.NewLabel("music.play");
        var voiceNoteOff = asm.NewLabel("Voice_NoteOff");
        var voiceAdvanceOrder = asm.NewLabel("Voice_AdvanceOrder");
        var voiceSetFreq = asm.NewLabel("Voice_SetFreq");
        var voicePlayCurrent = asm.NewLabel("Voice_PlayCurrent");
        var voiceAutomation = asm.NewLabel("Voice_Automation");
        var voiceTick = asm.NewLabel("Voice_Tick");
        var onBeatPlaySong = asm.NewLabel("ONbeat.PlaySong");
        var voiceOrderPos = asm.NewLabel("voice_orderpos");
        var sidChannelOffset = asm.NewLabel("SID_channel_offset");

        // --- JMPs for Init and Play ---
        asm.Jmp(musicInit);
        asm.Jmp(musicPlay);

        EmitVoiceNoteOff(asm, tables, zp, voiceNoteOff, sidChannelOffset);
        EmitVoiceAdvanceOrder(asm, tables, zp, voiceAdvanceOrder, voiceOrderPos);
        EmitVoiceSetFreq(asm, tables, features, zp, voiceSetFreq, sidChannelOffset);
        EmitVoicePlayCurrent(asm, tables, features, zp, voicePlayCurrent, voiceNoteOff, voiceSetFreq, sidChannelOffset);
        EmitVoiceAutomation(asm, tables, features, zp, voiceAutomation, voiceSetFreq, sidChannelOffset);
        EmitMusicInit(asm, parameters, zp, musicInit, voiceAdvanceOrder);
        EmitPlayerData(asm, parameters, voiceOrderPos, sidChannelOffset);
        EmitMusicPlay(asm, tables, parameters, zp, musicPlay, voiceAutomation, voiceTick, onBeatPlaySong);
        EmitVoiceTick(asm, tables, features, zp, voiceTick, voicePlayCurrent, voiceAdvanceOrder);
    }

    private static void RequireTables(SongTableLabels tables, PlayerFeatures features)
    {
        if (features.Vibrato && (tables.VibDs is null || tables.VibDepth is null))
            throw new ArgumentException("A song with vibrato needs both vib_ds and vib_depth.", nameof(tables));
    }

    /// <summary>Clears the gate bit, keeping the waveform-select bits - the oscillator would go
    /// instantly silent otherwise, masking the envelope's release. The wavetable is parked on a
    /// $00 hold rather than stopped, so it never puts the gate back on while pulse, note program,
    /// vibrato and slide run on through the release.</summary>
    private static void EmitVoiceNoteOff(Emitter6502 asm, SongTableLabels tables, ZeroPageLayout zp, Label entry, Label sidChannelOffset)
    {
        asm.Define(entry);
        asm.Ldy(MemX(sidChannelOffset));
        asm.Lda(MemX(zp.VoiceLastWaveform));
        asm.And(Imm(0xFE));
        asm.Sta(MemY(SidControl));
        asm.Move8(Imm(tables.WtHold), MemX(zp.VoiceWtPos));
        asm.Rts();
    }

    /// <summary>Song order: $00 ends a list and the next byte is the absolute restart position. All
    /// three channels share one array, so voice_orderpos indexes it absolutely.</summary>
    private static void EmitVoiceAdvanceOrder(
        Emitter6502 asm, SongTableLabels tables, ZeroPageLayout zp, Label entry, Label voiceOrderPos)
    {
        asm.Define(entry);
        var read = asm.DefineHere(".read");
        asm.Ldy(MemX(voiceOrderPos));
        asm.Lda(MemY(tables.TblSongOrder));
        var endOfList = asm.NewLabel(".endoflist");
        asm.Beq(endOfList);                         // the common path falls through
        asm.Sta(MemX(zp.VoicePatternIdx));
        // Not iny/tya/sta: Y is dead from here, so incrementing the array in place is cheaper.
        asm.Inc(MemX(voiceOrderPos));
        asm.Lda(Imm(0));
        asm.Sta(MemX(zp.VoiceEvent));
        asm.Sta(MemX(zp.VoiceLoopCounter));
        asm.Rts();

        asm.Define(endOfList);
        asm.Iny();
        asm.Lda(MemY(tables.TblSongOrder));         // absolute restart position
        asm.Sta(MemX(voiceOrderPos));
        asm.Jmp(read);
    }

    /// <summary>A = note dictionary id, X = channel. Writes the frequency and returns with Y
    /// holding that channel's SID offset, which both callers go on to use.</summary>
    private static void EmitVoiceSetFreq(
        Emitter6502 asm, SongTableLabels tables, PlayerFeatures features, ZeroPageLayout zp,
        Label entry, Label sidChannelOffset)
    {
        asm.Define(entry);
        asm.Tay();                                  // Y = note id
        asm.Lda(MemY(tables.NoteFreqLow));
        asm.Pha();                                  // the stack carries the lo byte across the Y
        asm.Lda(MemY(tables.NoteFreqHigh));         // switch from note id to SID channel offset
        asm.Ldy(MemX(sidChannelOffset));
        asm.Sta(MemY(SidFreqHi));

        // The SID's frequency registers cannot be read back, and vibrato/slide write base + offset
        // over whatever a note or arpeggio step just wrote - so the base is kept here, the one
        // place every frequency write passes through.
        if (features.FreqMod)
            asm.Sta(MemX(zp.VoiceFreqHi));

        asm.Pla();
        asm.Sta(MemY(SidFreqLo));

        if (features.FreqMod)
            asm.Sta(MemX(zp.VoiceFreqLo));

        asm.Rts();
    }

    private static void EmitVoicePlayCurrent(
        Emitter6502 asm, SongTableLabels tables, PlayerFeatures features, ZeroPageLayout zp,
        Label entry, Label voiceNoteOff, Label voiceSetFreq, Label sidChannelOffset)
    {
        asm.Define(entry);
        var leaveSounding = asm.NewLabel(".leavesounding");

        asm.Lda(MemX(zp.VoiceNote));
        asm.Cmp(Imm(NoteOff));
        asm.Beq(voiceNoteOff);
        // bcs, not a second cmp: the compare above already left C=1 for everything above $D0, so
        // one branch separates all three cases. A wait row lets whatever is playing keep going.
        asm.Bcs(leaveSounding);

        // The row's own note, kept for a ROOT step in the note program. voice_note cannot serve: a
        // later wait row overwrites it with ROW_WAIT, which is not an index into the table.
        asm.Sta(MemX(zp.VoiceRoot));

        // NOHR check before Voice_SetFreq clobbers A/Y: bit 5 of the pw_init byte belonging to the
        // instrument ALREADY sounding here, which voice_prev_pw keeps. The result rides the stack.
        asm.Tay();                                  // park the note in Y rather than reloading it
        asm.Lda(MemX(zp.VoicePrevPw));
        asm.And(Imm(0x20));
        asm.Pha();
        asm.Tya();

        asm.Jsr(voiceSetFreq);                      // leaves Y = this channel's SID offset

        // Hard restart unless NOHR: zero AD, release 7, gate off (waveform kept, or $08 - see
        // MagSid.Core.HardRestart). Release 7 and not 0
        // because the envelope rate counter is never reset by a write - a period below where it
        // sits wraps it through 15 bits and the next attack stalls.
        asm.Pla();
        var skipHardReset = asm.NewLabel(".skiphardreset");
        asm.Bne(skipHardReset);
        // A is already $00 - it is the "and #$20" result the bne just proved to be zero, so the
        // usual "lda #$00" in front of this is dead.
        asm.Sta(MemY(SidAttackDecay));
        asm.Lda(Imm(0x07));
        asm.Sta(MemY(SidSustainRelease));
        // See MagSid.Core.HardRestart: gate off with the waveform kept, or $08.
        if (Core.HardRestart.KeepsWaveform)
        {
            asm.Lda(MemX(zp.VoiceLastWaveform));
            asm.And(Imm(0xFE));
        }
        else
        {
            // SOFTHR is bit 7 of the outgoing instrument's pw_init, which voice_prev_pw holds -
            // the same byte NOHR is read from. Only assembled when some instrument has it.
            var writeControl = asm.NewLabel(".writecontrol");
            var hardControl = asm.NewLabel(".hardcontrol");
            if (features.SoftHardRestart)
            {
                asm.Lda(MemX(zp.VoicePrevPw));
                asm.Bpl(hardControl);
                asm.Lda(MemX(zp.VoiceLastWaveform));
                asm.And(Imm(0xFE));
                asm.Bne(writeControl);              // a last waveform of nothing falls back to $08
            }

            asm.Define(hardControl);
            asm.Lda(Imm(0x08));
            asm.Define(writeControl);
        }

        asm.Sta(MemY(SidControl));
        asm.Define(skipHardReset);

        asm.Lda(MemX(zp.VoiceInst));
        asm.Tax();                                  // X = instrument nr, channel parked in ZP_ch
        asm.Lda(MemX(tables.PwInit));
        asm.Pha();                                  // A and Y are both busy carrying wt_start and
        asm.Lda(MemX(tables.WtStart));              // arp_start down to the stores below
        asm.Ldy(MemX(tables.FltStart));
        var noFilter = asm.NewLabel(".nofilter");
        asm.Beq(noFilter);                          // instrument leaves the filter alone
        asm.Sty(Mem(zp.FilterPos));
        asm.Define(noFilter);
        asm.Ldy(MemX(tables.ArpStart));             // 0 for an instrument with no arpeggio
        asm.Ldx(Mem(zp.ZpCh));
        asm.Sta(MemX(zp.VoiceWtPos));
        asm.Sty(MemX(zp.VoiceArpPos));              // zp,x - A is reloaded on every path below

        if (features.Vibrato)
        {
            // Arm this note's vibrato: delay from vib_ds's top nibble (x4 frames), first run half a
            // speed long (low nibble >> 1), which centres the wave on the note.
            asm.Ldy(MemX(zp.VoiceInst));
            asm.Lda(MemY(tables.VibDs!));
            asm.Asr(Imm(0xF0));                     // ALR: and #$f0 + lsr, same flags
            asm.Lsr();
            asm.Sta(MemX(zp.VoiceVibDelay));
            asm.Lda(MemY(tables.VibDs!));
            asm.Asr(Imm(0x0F));                     // ALR: and #$0f + lsr
            asm.Sta(MemX(zp.VoiceVibCnt));
            asm.Lda(Imm(0));
            asm.Sta(MemX(zp.VoiceVibDir));
        }
        else if (features.Slide)
        {
            asm.Lda(Imm(0));
        }

        if (features.FreqMod)
        {
            asm.Sta(MemX(zp.VoiceOffLo));           // vibrato and slide both start from the bare note
            asm.Sta(MemX(zp.VoiceOffHi));
        }

        if (features.Slide)
            asm.Sta(MemX(zp.VoiceSlide));           // and a new note starts without a slide

        asm.Pla();
        asm.Tay();                                  // park pw_init while testing it
        // Stashed for the *next* trigger's NOHR test: this is the instrument that will be sounding
        // when it runs. sta, not sty - STY has no absolute,X mode.
        asm.Sta(MemX(zp.VoicePrevPw));
        asm.And(Imm(0x40));
        // bit 6 set: continuous - the sweep is per channel, so leave it running across the trigger.
        asm.Bne(leaveSounding);
        asm.Tya();
        asm.Sta(MemX(zp.VoicePwHi));                // a note restarts the sweep from the
        asm.Lda(Imm(0));                            // instrument's own starting width
        asm.Sta(MemX(zp.VoicePwLo));
        asm.Define(leaveSounding);
        asm.Rts();
    }

    /// <summary>One wavetable step plus ADSR for channel X, every frame rather than every tick - an
    /// attack transient needs frame resolution. Pool offset 0 is a dummy $00, so voice_wt_pos = 0
    /// doubles as "nothing played yet". A released note sits on wt_hold and runs on.</summary>
    private static void EmitVoiceAutomation(
        Emitter6502 asm, SongTableLabels tables, PlayerFeatures features, ZeroPageLayout zp,
        Label entry, Label voiceSetFreq, Label sidChannelOffset)
    {
        asm.Define(entry);
        var idle = asm.NewLabel(".idle");
        var freqMod = asm.NewLabel(".freqmod");
        // Where the arpeggio paths go when there is no note program, and where the frequency-
        // modulation block hangs off when there is one.
        Label afterArp = features.FreqMod ? freqMod : idle;

        asm.Ldy(MemX(zp.VoiceWtPos));
        var running = asm.NewLabel(".running");
        asm.Bne(running);
        asm.Rts();                                  // an rts of its own is 2 bytes cheaper than a
        asm.Define(running);                        // long branch to .idle below

        asm.Lda(MemY(tables.WtData));
        var adsr = asm.NewLabel(".adsr");
        asm.Beq(adsr);                              // $00 = hold on the current waveform
        asm.Ldy(MemX(sidChannelOffset));
        asm.Sta(MemY(SidControl));
        asm.Sta(MemX(zp.VoiceLastWaveform));        // remembered for Voice_NoteOff
        asm.Inc(MemX(zp.VoiceWtPos));
        asm.Define(adsr);

        asm.Ldy(MemX(sidChannelOffset));
        asm.Stx(Mem(zp.ZpCh));
        asm.Lda(MemX(zp.VoiceInst));
        asm.Tax();                                  // X = instrument nr, channel parked in ZP_ch
        asm.Lda(MemX(tables.AdTable));
        asm.Sta(MemY(SidAttackDecay));
        asm.Lda(MemX(tables.SrTable));
        asm.Sta(MemY(SidSustainRelease));

        // Pulse width. $D402/$D403 are write-only, so the width lives in a per-channel shadow and
        // is rewritten every frame. The delta is sign-extended into all 12 bits, so the sweep
        // travels the whole register and wraps. X is still the instrument index here.
        asm.Lda(MemX(tables.PwAdd));
        asm.Ldx(Mem(zp.ZpCh));
        asm.Pha();
        asm.Clc();
        asm.Adc(MemX(zp.VoicePwLo));
        asm.Sta(MemX(zp.VoicePwLo));
        asm.Sta(MemY(SidPwLo));
        asm.Pla();                                  // the delta again. None of pla/and/beq/lda
        asm.And(Imm(0x80));                         // touches the carry flag either
        var pwHigh = asm.NewLabel(".pwhigh");
        asm.Beq(pwHigh);
        asm.Lda(Imm(0xFF));                         // sign extension - a downward sweep
        asm.Define(pwHigh);
        asm.Adc(MemX(zp.VoicePwHi));
        asm.And(Imm(0x0F));
        asm.Sta(MemX(zp.VoicePwHi));
        asm.Sta(MemY(SidPwHi));

        // Note program: one note per frame alongside the wavetable. Absolute note ids, except ROOT
        // which plays the row's own note - that pair is what makes a drum or bass transient
        // expressible.
        asm.Ldy(MemX(zp.VoiceArpPos));
        asm.Beq(afterArp);                          // instrument has no note program

        var arpStep = asm.DefineHere(".arpstep");
        asm.Lda(MemY(tables.ArpData));
        var arpLoop = asm.NewLabel(".arploop");
        asm.Beq(arpLoop);                           // $00 = end marker, next byte says where to resume
        var arpNote = asm.NewLabel(".arpnote");
        asm.Bpl(arpNote);                           // $01..$7F = a note dictionary id
                                                    // else bit 7 set = exactly $80 = ROOT
        asm.Lda(MemX(zp.VoiceRoot));
        EmitArpFreqWrite(asm, features, voiceSetFreq, freqMod);

        asm.Define(arpNote);
        asm.Inc(MemX(zp.VoiceArpPos));
        EmitArpFreqWrite(asm, features, voiceSetFreq, freqMod);

        asm.Define(arpLoop);
        asm.Iny();
        asm.Lda(MemY(tables.ArpData));
        asm.Sta(MemX(zp.VoiceArpPos));
        asm.Tay();
        asm.Bne(arpStep);                           // a target of 0 would stop the arpeggio instead
        asm.Define(idle);
        asm.Rts();

        if (features.FreqMod)
            EmitFreqMod(asm, tables, features, zp, freqMod, idle, sidChannelOffset);
    }

    /// <summary>Without frequency modulation this is a tail call - Voice_SetFreq ends in the rts
    /// the caller needs. With it, the offset still has to be applied afterwards.</summary>
    private static void EmitArpFreqWrite(Emitter6502 asm, PlayerFeatures features, Label voiceSetFreq, Label freqMod)
    {
        if (features.FreqMod)
        {
            asm.Jsr(voiceSetFreq);
            asm.Jmp(freqMod);
        }
        else
        {
            asm.Jmp(voiceSetFreq);
        }
    }

    /// <summary>Slide and vibrato, last in Voice_Automation because the arpeggio rewrites the bare
    /// note every frame. Both move one signed 16-bit offset; slide adds the rate a pattern's slide
    /// row left in voice_slide a frame, vibrato traces a triangle from vib_ds (delay/speed) and
    /// vib_depth.</summary>
    private static void EmitFreqMod(
        Emitter6502 asm, SongTableLabels tables, PlayerFeatures features, ZeroPageLayout zp,
        Label entry, Label idle, Label sidChannelOffset)
    {
        asm.Define(entry);
        var fmWrite = asm.NewLabel(".fmwrite");
        var noSlide = asm.NewLabel(".noslide");

        // Only vibrato reads the instrument's tables; the slide lives with the voice.
        if (features.Vibrato)
            asm.Ldy(MemX(zp.VoiceInst));

        // With slides in the song the note plus its offset is written every frame, slide or no
        // slide: a note program rewrites the bare note each frame, and only the offset added back
        // on top keeps a slide that "100" stopped where it got to.
        if (features.Slide)
        {
            asm.Lda(MemX(zp.VoiceSlide));
            asm.Beq(features.Vibrato ? noSlide : fmWrite);

            // offset += rate, sign-extended. None of pla/and/beq/lda touches the carry the adc
            // produced, the same idiom the pulse-width sweep uses.
            asm.Pha();
            asm.Clc();
            asm.Adc(MemX(zp.VoiceOffLo));
            asm.Sta(MemX(zp.VoiceOffLo));
            asm.Pla();
            asm.And(Imm(0x80));
            var slideHigh = asm.NewLabel(".slidehigh");
            asm.Beq(slideHigh);
            asm.Lda(Imm(0xFF));
            asm.Define(slideHigh);
            asm.Adc(MemX(zp.VoiceOffHi));
            asm.Sta(MemX(zp.VoiceOffHi));
        }

        if (features.Vibrato)
        {
            asm.Define(noSlide);
            asm.Lda(MemY(tables.VibDepth!));
            asm.Beq(features.Slide ? fmWrite : idle);   // slide moved the offset: still write it
            asm.Pha();
            asm.Lda(MemX(zp.VoiceVibDelay));
            var vibRun = asm.NewLabel(".vibrun");
            asm.Beq(vibRun);
            asm.Dec(MemX(zp.VoiceVibDelay));
            asm.Pla();
            asm.Jmp(fmWrite);

            asm.Define(vibRun);
            // offset += depth (dir $00) or -= depth (dir $ff). depth EOR dir is depth or ~depth,
            // and the carry - set by the compare exactly when dir is $ff - supplies the +1 that
            // turns ~depth into -depth; dir itself is the sign extension for the high byte.
            asm.Lda(MemX(zp.VoiceVibDir));
            asm.Cmp(Imm(1));
            asm.Pla();
            asm.Eor(MemX(zp.VoiceVibDir));
            asm.Adc(MemX(zp.VoiceOffLo));
            asm.Sta(MemX(zp.VoiceOffLo));
            asm.Lda(MemX(zp.VoiceVibDir));
            asm.Adc(MemX(zp.VoiceOffHi));
            asm.Sta(MemX(zp.VoiceOffHi));
            asm.Dec(MemX(zp.VoiceVibCnt));
            asm.Bpl(fmWrite);
            asm.Lda(MemX(zp.VoiceVibDir));          // run finished: turn round and reload the speed
            asm.Eor(Imm(0xFF));
            asm.Sta(MemX(zp.VoiceVibDir));
            asm.Lda(MemY(tables.VibDs!));          // Y is still voice_inst from .freqmod
            asm.And(Imm(0x0F));
            asm.Sta(MemX(zp.VoiceVibCnt));
        }

        asm.Define(fmWrite);
        asm.Lda(MemX(zp.VoiceFreqLo));
        asm.Clc();
        asm.Adc(MemX(zp.VoiceOffLo));
        asm.Pha();
        asm.Lda(MemX(zp.VoiceFreqHi));
        asm.Adc(MemX(zp.VoiceOffHi));
        asm.Ldy(MemX(sidChannelOffset));
        asm.Sta(MemY(SidFreqHi));
        asm.Pla();
        asm.Sta(MemY(SidFreqLo));
        asm.Rts();
    }

    private static void EmitMusicInit(
        Emitter6502 asm, PlayerParameters parameters, ZeroPageLayout zp, Label entry, Label voiceAdvanceOrder)
    {
        asm.Define(entry);

        // ZP is not part of the loaded image, so state that used to be initialised by its own
        // ".byte 0,0,0" is cleared here - before the Voice_AdvanceOrder loop, which writes
        // voice_event and voice_loop_counter.
        asm.Lda(Imm(0));
        asm.Ldx(Imm(zp.ByteCount - 1));
        var zeroState = asm.DefineHere(".zerostate");
        asm.Sta(MemX(zp.Base));
        asm.Dex();
        asm.Bpl(zeroState);

        asm.Move8(Imm(0x0F), Mem(SidFilterModeVolume));

        asm.Ldx(Imm(parameters.Channels - 1));
        var initChannel = asm.DefineHere(".initchannel");
        asm.Jsr(voiceAdvanceOrder);                 // preserves X
        asm.Dex();
        asm.Bpl(initChannel);
        asm.Rts();
    }

    /// <summary>The only per-channel arrays carrying real initial values; the rest is in zero page
    /// and zeroed by music.init.</summary>
    private static void EmitPlayerData(
        Emitter6502 asm, PlayerParameters parameters, Label voiceOrderPos, Label sidChannelOffset)
    {
        asm.Define(voiceOrderPos);
        asm.Bytes(parameters.OrderStart1, parameters.OrderStart2, parameters.OrderStart3);

        asm.Define(sidChannelOffset);
        asm.Bytes(0, 7, 14);
    }

    private static void EmitMusicPlay(
        Emitter6502 asm, SongTableLabels tables, PlayerParameters parameters, ZeroPageLayout zp,
        Label entry, Label voiceAutomation, Label voiceTick, Label onBeatPlaySong)
    {
        asm.Define(entry);

        // Wavetables first and on every frame - that is what gives them frame resolution. Counting
        // down saves the cpx; it reverses the write order within a frame, but the writes and the
        // per-frame register state are identical and the voices are independent, so it is inaudible.
        asm.Ldx(Imm(parameters.Channels - 1));
        var nextVoice = asm.DefineHere(".nextvoice");
        asm.Jsr(voiceAutomation);
        asm.Dex();
        asm.Bpl(nextVoice);

        EmitFilterPlay(asm, tables, zp, onBeatPlaySong);

        // --- ONbeat.PlaySong ---
        asm.Define(onBeatPlaySong);
        asm.Move8(Imm(parameters.SongSpeed), Mem(zp.SongSpeed));

        // Tick all channels. Filter_Play above ran by fallthrough, not a jsr, so the trailing rts
        // below is this routine's own.
        asm.Ldx(Imm(parameters.Channels - 1));      // descending, same reasoning as above
        var nextChannel = asm.DefineHere(".nextchannel");
        asm.Jsr(voiceTick);                         // preserves X
        asm.Dex();
        asm.Bpl(nextChannel);
        asm.Rts();
    }

    /// <summary>Filter, once per frame and before the row fetch, so a program claimed this frame
    /// takes its first step next frame. Byte stream: $01..$FE steps the cutoff, $FF introduces a
    /// four-byte setup (mode+volume, resonance+routing, sweep rate, cutoff), $00 ends. Falls
    /// through from music.play, no jsr.</summary>
    private static void EmitFilterPlay(
        Emitter6502 asm, SongTableLabels tables, ZeroPageLayout zp, Label onBeatPlaySong)
    {
        var filterDone = asm.NewLabel(".filterdone");
        var filterCut = asm.NewLabel(".filtercut");

        asm.Ldx(Mem(zp.FilterPos));
        asm.Beq(filterDone);                        // nothing has claimed the filter yet
        asm.Lda(MemX(tables.FltData));
        asm.Beq(filterDone);                        // program finished - hold the last cutoff
        asm.Cmp(Imm(0xFF));
        asm.Bne(filterCut);
        asm.Inx();
        asm.Lda(MemX(tables.FltData));
        asm.Sta(Mem(SidFilterModeVolume));          // filter mode + master volume
        asm.Inx();
        asm.Lda(MemX(tables.FltData));
        asm.Sta(Mem(SidFilterResonance));           // resonance + which voices are routed through
        asm.Inx();
        asm.Lda(MemX(tables.FltData));
        asm.Sta(Mem(zp.FilterDelta));               // sweep rate, 0 to stop a running sweep
        asm.Inx();
        asm.Lda(MemX(tables.FltData));
        asm.Define(filterCut);
        asm.Sta(Mem(SidFilterCutoffHi));
        asm.Inx();
        asm.Stx(Mem(zp.FilterPos));
        asm.Define(filterDone);

        // Filter sweep, every frame. A free-running triangle: filter_phase advances by a signed
        // rate and wraps, the cutoff folds the top half back down and doubles. That fold is what
        // makes it ping-pong without bounds checks, at the cost of a fixed $00..$FE range.
        asm.Lda(Mem(zp.FilterDelta));
        var sweepDone = asm.NewLabel(".sweepdone");
        asm.Beq(sweepDone);                         // 0 = no sweep running
        asm.Clc();
        asm.Adc(Mem(zp.FilterPhase));
        asm.Sta(Mem(zp.FilterPhase));
        var sweepUp = asm.NewLabel(".sweepup");
        asm.Bpl(sweepUp);
        asm.Eor(Imm(0xFF));                         // past the top: fold back down
        asm.Define(sweepUp);
        asm.Asl();
        asm.Sta(Mem(SidFilterCutoffHi));
        asm.Define(sweepDone);

        asm.Dec(Mem(zp.SongSpeed));
        asm.Bmi(onBeatPlaySong);
        asm.Rts();                                  // between ticks: the filter already stepped
    }

    /// <summary>Per-channel tick. X = channel on entry and preserved on every exit. One subroutine
    /// indexed by X rather than three macro copies, which used to cost 592 of the player's 794
    /// bytes. Rows are variable length: voice_event is a byte offset, .nextcmd consumes commands.</summary>
    private static void EmitVoiceTick(
        Emitter6502 asm, SongTableLabels tables, PlayerFeatures features, ZeroPageLayout zp,
        Label entry, Label voicePlayCurrent, Label voiceAdvanceOrder)
    {
        asm.Define(entry);
        var fetch = asm.NewLabel(".fetch");
        var nextCmd = asm.NewLabel(".nextcmd");
        var playRow = asm.NewLabel(".playrow");
        var setDuration = asm.NewLabel(".setduration");
        var setInstrument = asm.NewLabel(".setinstrument");
        var advance = asm.NewLabel(".advance");
        var loopBack = asm.NewLabel(".loopback");
        var endOfPattern = asm.NewLabel(".endofpattern");
        var slide = asm.NewLabel(".slide");

        asm.Stx(Mem(zp.ZpCh));

        // Count down first and test the result rather than testing for zero then decrementing: a
        // row with N ticks left spends exactly N of them here. The $ff is never seen - .playrow
        // reloads voice_duration from voice_rowdur first.
        asm.Dec(MemX(zp.VoiceDuration));
        asm.Bmi(fetch);
        asm.Rts();

        asm.Define(fetch);
        asm.Ldy(MemX(zp.VoicePatternIdx));          // Y = pattern index, so X stays the channel -
        asm.Lda(MemY(tables.TblRowsLo));            // 3 bytes less than borrowing X and putting it
        asm.Sta(Mem(zp.ZpLb));                      // back from ZP_ch
        asm.Lda(MemY(tables.TblRowsHi));
        asm.Sta(Mem(zp.ZpHb));

        // .fetchrow in the assembly - a label nothing jumps to, so there is nothing to define.
        asm.Ldy(MemX(zp.VoiceEvent));

        asm.Define(nextCmd);
        asm.Lda(IndY(zp.ZpLb));
        asm.Beq(endOfPattern);
        asm.Bpl(playRow);                           // $01..$7F is a note id - store it as it stands
        asm.Cmp(Imm(CmdInst));
        asm.Bcc(setDuration);                       // $80..$BF
        asm.Cmp(Imm(CmdOff));
        asm.Bcc(setInstrument);                     // $C0..$CF
        asm.Cmp(Imm(LoopEnd));
        // $D0/$D1 - note off and wait are stored as the command byte itself, which is what
        // NOTE_OFF/ROW_WAIT are defined to be. Falls through on $D2 into .loopend, which is why
        // that block has to stay right here.
        if (features.Slide)
        {
            asm.Bcc(playRow);
            asm.Bne(slide);                         // $D3, only in a song with slide rows
        }
        else
        {
            asm.Bne(playRow);
        }

        // --- .loopend ---
        // $D2, then the repeat count (0 = bare LE, loops forever), then the byte offset to jump
        // back to (the most recent LS, or pattern start if none).
        asm.Iny();
        asm.Lda(IndY(zp.ZpLb));
        asm.Beq(loopBack);
        // ISC (illegal $F7 zp,x) is INC then SBC, so this one instruction is the inc + cmp pair it
        // replaces: with carry set, SBC gives A - counter, zero exactly when equal. CARRY MUST BE
        // SET - it is, only because the sole way in is the falling-through cmp #LOOP_END above.
        asm.Isc(MemX(zp.VoiceLoopCounter));
        asm.Bne(loopBack);
        // A is already 0 - the difference the isc just computed, and this path only runs when that
        // difference was zero.
        asm.Sta(MemX(zp.VoiceLoopCounter));
        asm.Iny();                                  // step past the target byte to the next row
        asm.Iny();
        // Y is already the new offset, so there is no need to store it and have it loaded straight
        // back - nothing between here and .playrow reads voice_event, and both write it themselves.
        asm.Jmp(nextCmd);

        asm.Define(loopBack);
        asm.Iny();
        asm.Lda(IndY(zp.ZpLb));
        asm.Tay();                                  // same reasoning - jump straight in with Y set
        asm.Jmp(nextCmd);

        if (features.Slide)
        {
            // $D3, then the signed rate: a wait row that also sets the voice's slide, so it leaves
            // as ROW_WAIT with Y on the rate byte - .playrow steps past it.
            asm.Define(slide);
            asm.Iny();
            asm.Lda(IndY(zp.ZpLb));
            asm.Sta(MemX(zp.VoiceSlide));
            asm.Lda(Imm(RowWait));
            asm.Bne(playRow);                       // always - ROW_WAIT is not zero
        }

        // Masking rather than subtracting: the dispatch above already proved which range the byte
        // is in, so the low bits are the value and a single AND isolates them - one byte less than
        // sec/sbc, and it leaves no carry to think about.
        asm.Define(setDuration);
        asm.And(Imm(0x3F));                         // $80..$BF -> 0..63 further ticks, so a row
        asm.Sta(MemX(zp.VoiceRowDur));              // lasts 1..64
        // Always taken - "and #$3f" cannot set N, and the sta between does not touch flags.
        asm.Bpl(advance);

        asm.Define(setInstrument);
        asm.And(Imm(0x0F));                         // $C0..$CF -> 0..15
        asm.Sta(MemX(zp.VoiceInst));
        asm.Define(advance);
        asm.Iny();
        asm.Bne(nextCmd);                           // always - a pattern never reaches offset 0 here

        asm.Define(playRow);
        asm.Sta(MemX(zp.VoiceNote));
        asm.Iny();
        asm.Tya();
        asm.Sta(MemX(zp.VoiceEvent));
        asm.Lda(MemX(zp.VoiceRowDur));              // the row takes however long is current
        asm.Sta(MemX(zp.VoiceDuration));
        asm.Jmp(voicePlayCurrent);                  // tail call - its rts returns to our caller

        asm.Define(endOfPattern);
        asm.Jsr(voiceAdvanceOrder);                 // reuses ZP_lb/hb, so re-derive the row pointer
        asm.Jmp(fetch);
    }
}
