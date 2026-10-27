using MagPlay.C64;
using MagPlay.Score;
using MagSid.Playback;

namespace MagPlay.Transcribe.Tests;

/// <summary>
/// Tunes written for the test, recorded on the C64 and transcribed: the score always plays them
/// back exactly, and the music makes what a player plays as notes.
/// </summary>
public class TranscriberTests
{
    private static readonly int[] Notes = NoteTables.MagSidPal();
    private const int C4 = 48, E4 = 52, G4 = 55, C5 = 60;

    /// <summary>A .sid around <paramref name="code"/>, which loads at $1000.</summary>
    private static SidFile Tune(byte[] code, int play = 0, bool rsid = false, int secondSid = 0)
    {
        var bytes = new byte[0x7C + code.Length];
        (rsid ? "RSID"u8 : "PSID"u8).CopyTo(bytes);
        void Word(int at, int value)
        {
            bytes[at] = (byte)(value >> 8);
            bytes[at + 1] = (byte)value;
        }

        Word(0x04, secondSid != 0 ? 3 : 2);
        Word(0x06, 0x7C);
        Word(0x08, 0x1000);
        Word(0x0A, 0x1000);
        Word(0x0C, play);
        Word(0x0E, 1);
        Word(0x10, 1);
        Word(0x76, 0x14);
        bytes[0x7A] = (byte)(secondSid >> 4);
        code.CopyTo(bytes, 0x7C);
        return SidFile.Parse(bytes);
    }

    /// <summary>
    /// A player that plays C4, E4, G4, C5 round and round, a note every 8 calls: the frequency
    /// from its table, the gate off and on again in the same call, a pulse wave held.
    /// </summary>
    private static SidFile Arpeggiator()
    {
        var code = new byte[0x80];
        // init: lda #$0f / sta $d418 / lda #$09 / sta $d405 / lda #$a0 / sta $d406 / rts
        byte[] init = [0xA9, 0x0F, 0x8D, 0x18, 0xD4, 0xA9, 0x09, 0x8D, 0x05, 0xD4, 0xA9, 0xA0, 0x8D, 0x06, 0xD4, 0x60];
        // play: ldx $c000 / inx / cpx #8 / bne hold / ldx #0 / ldy $c001 / lda lo,y / sta $d400 /
        //       lda hi,y / sta $d401 / lda #$40 / sta $d404 / lda #$41 / sta $d404 / iny / tya /
        //       and #3 / sta $c001 / hold: stx $c000 / rts
        byte[] play =
        [
            0xAE, 0x00, 0xC0, 0xE8, 0xE0, 0x08, 0xD0, 0x22, 0xA2, 0x00, 0xAC, 0x01, 0xC0,
            0xB9, 0x60, 0x10, 0x8D, 0x00, 0xD4, 0xB9, 0x64, 0x10, 0x8D, 0x01, 0xD4,
            0xA9, 0x40, 0x8D, 0x04, 0xD4, 0xA9, 0x41, 0x8D, 0x04, 0xD4, 0xC8, 0x98,
            0x29, 0x03, 0x8D, 0x01, 0xC0, 0x8E, 0x00, 0xC0, 0x60,
        ];
        init.CopyTo(code, 0);
        play.CopyTo(code, 0x10);
        int[] notes = [C4, E4, G4, C5];
        for (int i = 0; i < 4; i++)
        {
            code[0x60 + i] = (byte)Notes[notes[i]];
            code[0x64 + i] = (byte)(Notes[notes[i]] >> 8);
        }
        return Tune(code, play: 0x1010);
    }

    [Fact]
    public void A_kick_and_bass_is_one_instrument_its_notes_the_bass_it_settles_on()
    {
        // The arpeggiator's notes as a bass under a kick: each note's first call on C-6 whatever
        // the note, the bass from the next call on. Its very first call, before any note, sets
        // C-4 and moves on, so the notes start from E-4.
        var code = new byte[0x80];
        Array.Copy(Arpeggiator().Data, code, 0x10);
        int kick = Notes[C4 + 24];
        byte[] play =
        [
            0xAE, 0x00, 0xC0, 0xE8, 0xE0, 0x08, 0xD0, 0x19, 0xA2, 0x00,   // ldx $c000 / inx / cpx #8 / bne more / ldx #0
            0xA9, (byte)kick, 0x8D, 0x00, 0xD4, 0xA9, (byte)(kick >> 8), 0x8D, 0x01, 0xD4,
            0xA9, 0x40, 0x8D, 0x04, 0xD4, 0xA9, 0x41, 0x8D, 0x04, 0xD4,
            0x4C, 0x4B, 0x10,                                           // jmp store
            0xE0, 0x01, 0xD0, 0x16,                                     // more: cpx #1 / bne store
            0xAC, 0x01, 0xC0, 0xB9, 0x60, 0x10, 0x8D, 0x00, 0xD4, 0xB9, 0x64, 0x10, 0x8D, 0x01, 0xD4,
            0xC8, 0x98, 0x29, 0x03, 0x8D, 0x01, 0xC0,
            0x8E, 0x00, 0xC0, 0x60,                                     // store: stx $c000 / rts
        ];
        play.CopyTo(code, 0x10);
        Array.Copy(Arpeggiator().Data, 0x60, code, 0x60, 8);
        var recording = Recording.Record(Tune(code, play: 0x1010), 1, 2.0);
        var (score, _) = Transcriber.Transcribe(recording, Music.Of(recording));

        Assert.True(Agreement.Measure(recording, score).Exact);
        Assert.Single(score.Instruments);
        var played = score.Voices[0].Order
            .SelectMany(e => Enumerable.Repeat(score.PatternNumber(e.Pattern)!.Rows.Select(r => r.Note.Kind == RowNoteKind.Note ? r.Note.Note + e.Transpose : -1), e.Repeat))
            .SelectMany(n => n).Where(n => n >= 0).ToList();
        Assert.Equal([E4, G4, C5, C4, E4, G4], played.Take(6));
    }

    [Fact]
    public void A_score_that_is_all_raw_plays_the_tune_back_exactly()
    {
        var recording = Recording.Record(Arpeggiator(), 1, 2.0);
        var (score, _) = Transcriber.Transcribe(recording);
        var agreement = Agreement.Measure(recording, score);

        Assert.True(agreement.Exact);
        Assert.Empty(score.Instruments);
        Assert.Equal(recording.Between, score.Raw.Between);
    }

    [Fact]
    public void A_players_notes_become_rows_of_its_tempo_on_one_instrument_and_play_back_exactly()
    {
        var recording = Recording.Record(Arpeggiator(), 1, 2.0);
        var (score, report) = Transcriber.Transcribe(recording, Music.Of(recording));

        Assert.True(Agreement.Measure(recording, score).Exact);
        Assert.Equal(1.0, report.Music);
        Assert.Single(score.Instruments);
        var played = score.Voices[0].Order
            .SelectMany(e => Enumerable.Repeat(score.PatternNumber(e.Pattern)!.Rows.Select(r => r.Note.Kind == RowNoteKind.Note ? r with { Note = RowNote.Play(r.Note.Note + e.Transpose) } : r), e.Repeat))
            .SelectMany(r => r).Where(r => r.Note.Kind == RowNoteKind.Note).ToList();
        Assert.Equal([C4, E4, G4, C5, C4, E4], played.Take(6).Select(r => r.Note.Note));
        Assert.Equal(8, score.Tempo);
        Assert.Null(score.FunkTempo);
        Assert.All(played, row => Assert.Null(row.Calls));
        Assert.All(played, row => Assert.Empty(row.Effects));
    }

    [Fact]
    public void The_grid_is_on_the_phase_that_delays_notes_least_its_first_row_on_the_first_note()
    {
        // Rows of 6 from call 3, two notes a call late - delayed by one - and one a call early,
        // on the row before, by five. Before the first row, a row of its own.
        List<int>[] rises = [[3, 9, 15, 22, 27, 33, 40, 45, 51, 57], [8, 21, 39], []];
        var grid = Music.Grid.Of(rises);

        Assert.Equal((6, 6, 3), (grid.A, grid.B, grid.Origin));
        var rows = grid.Lay([.. rises[0].Select(s => (s, Row.Of(C4)))], 60).Select(r => r.Row).ToList();
        Assert.Equal(3, rows[0].Calls);
        Assert.All(rows.Skip(1), r => Assert.Null(r.Calls));
        Assert.Equal([0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0], rows.Select(Delay));
        var early = grid.Lay([.. rises[1].Select(s => (s, Row.Of(C4)))], 60).Select(r => r.Row).ToList();
        Assert.Equal((RowNoteKind.Note, 5), (early[1].Note.Kind, Delay(early[1])));
        Assert.Equal(3, early.Count(r => r.Note.Kind == RowNoteKind.Note));

        static int Delay(Row row) => row.Effects.OfType<NoteDelay>().Select(d => d.Calls).DefaultIfEmpty(0).First();
    }

    [Fact]
    public void A_2sid_tune_is_transcribed_on_both_chips_and_plays_back_exactly()
    {
        // The arpeggiator, and after it a note on the SID at $D420 whenever it starts one: the
        // next note of its table, the gate off and on again.
        var arpeggiator = Arpeggiator();
        var code = new byte[0x100];
        Array.Copy(arpeggiator.Data, code, arpeggiator.Data.Length);
        byte[] second =
        [
            0x20, 0x10, 0x10,                                     // jsr play
            0xAC, 0x01, 0xC0, 0xB9, 0x60, 0x10, 0x8D, 0x20, 0xD4,  // ldy $c001 / lda lo,y / sta $d420
            0xB9, 0x64, 0x10, 0x8D, 0x21, 0xD4,                    // lda hi,y / sta $d421
            0xAD, 0x00, 0xC0, 0xD0, 0x0A,                          // lda $c000 / bne done
            0xA9, 0x40, 0x8D, 0x24, 0xD4, 0xA9, 0x21, 0x8D, 0x24, 0xD4, // gate off, saw and gate on
            0x60,                                                  // done: rts
        ];
        second.CopyTo(code, 0x80);
        var recording = Recording.Record(Tune(code, play: 0x1080, secondSid: 0xD420), 1, 2.0);
        var (score, report) = Transcriber.Transcribe(recording, Music.Of(recording));

        Assert.Equal(2, score.Chips);
        Assert.True(Agreement.Measure(recording, score).Exact);
        Assert.Contains(recording.CallWrites.SelectMany(c => c), w => w.Register == 0x24);
        Assert.NotEmpty(score.Voices[3].Order);
        Assert.True(report.Music > 0.9);
    }

    [Fact]
    public void A_tune_that_plays_from_its_main_loop_has_its_bursts_of_writes_as_calls()
    {
        // An RSID whose init never returns: the arpeggiator's setup, then a loop that waits for
        // raster line $80 and calls its play routine once a frame - no interrupt in it.
        var arpeggiator = Arpeggiator();
        var code = new byte[0xA0];
        Array.Copy(arpeggiator.Data, code, arpeggiator.Data.Length);
        byte[] jump = [0x4C, 0x70, 0x10];
        byte[] loop =
        [
            0xA9, 0x0F, 0x8D, 0x18, 0xD4, 0xA9, 0x09, 0x8D, 0x05, 0xD4, 0xA9, 0xA0, 0x8D, 0x06, 0xD4,
            0xAD, 0x12, 0xD0, 0xC9, 0x80, 0xD0, 0xF9,   // wait: lda $d012 / cmp #$80 / bne wait
            0x20, 0x10, 0x10,                           // jsr play
            0xAD, 0x12, 0xD0, 0xC9, 0x80, 0xF0, 0xF9,   // lda $d012 / cmp #$80 / beq - off the line
            0x4C, 0x7F, 0x10,                           // jmp wait
        ];
        jump.CopyTo(code, 0);
        loop.CopyTo(code, 0x70);
        var recording = Recording.Record(Tune(code, rsid: true), 1, 2.0);
        var (score, report) = Transcriber.Transcribe(recording, Music.Of(recording));

        Assert.Equal(WriteContext.Main, recording.Player);
        Assert.InRange(recording.Calls.Count, 98, 101);
        Assert.True(Agreement.Measure(recording, score).Exact);
        Assert.True(report.Music > 0.9);
        Assert.Equal(8, score.Tempo);
    }

    [Fact]
    public void A_whole_tune_is_recorded_to_its_loop_with_its_player_only()
    {
        var (recording, length) = Recording.RecordWhole(Arpeggiator(), 1);

        Assert.NotNull(length);
        Assert.Equal(WriteContext.Call, recording.Player);
        Assert.Empty(recording.Between.Where(w => w.Register != 0x18 && w.Register != 0x05 && w.Register != 0x06));
        Assert.InRange(recording.Calls.Count, length.TotalFrames, length.TotalFrames + 60);
    }

    [Fact]
    public void A_tunes_init_is_kept_before_the_first_call_on_its_cycles()
    {
        var recording = Recording.Record(Arpeggiator(), 1, 0.5);
        var (score, _) = Transcriber.Transcribe(recording, Music.Of(recording));

        Assert.Equal([0x18, 0x05, 0x06], recording.Between.Select(w => w.Register));
        Assert.Equal(recording.Between, new ScorePlayer(score).Init);
    }

    [Fact]
    public void A_digi_streamed_from_an_nmi_is_kept_write_for_write_on_its_cycles()
    {
        // init: nmi vector $0318 -> $1020 / cia 2 timer a 123 / its interrupt on / started / rts
        // nmi: pha / lda $dd0d / inc $c001 / lda $c001 / and #$0f / sta $d418 / pla / rti
        var code = new byte[0x40];
        byte[] init =
        [
            0xA9, 0x20, 0x8D, 0x18, 0x03, 0xA9, 0x10, 0x8D, 0x19, 0x03,
            0xA9, 123, 0x8D, 0x04, 0xDD, 0xA9, 0x00, 0x8D, 0x05, 0xDD,
            0xA9, 0x81, 0x8D, 0x0D, 0xDD, 0xA9, 0x11, 0x8D, 0x0E, 0xDD, 0x60,
        ];
        byte[] nmi = [0x48, 0xAD, 0x0D, 0xDD, 0xEE, 0x01, 0xC0, 0xAD, 0x01, 0xC0, 0x29, 0x0F, 0x8D, 0x18, 0xD4, 0x68, 0x40];
        init.CopyTo(code, 0);
        nmi.CopyTo(code, 0x20);
        var recording = Recording.Record(Tune(code, rsid: true), 1, 0.2);
        var (score, _) = Transcriber.Transcribe(recording, Music.Of(recording));
        var agreement = Agreement.Measure(recording, score);

        Assert.InRange(recording.Between.Count, 1500, 1700);
        Assert.True(agreement.Exact);
        Assert.Equal(recording.Between.Count, agreement.BetweenSame);
    }
}
