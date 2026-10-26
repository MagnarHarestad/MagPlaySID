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
    private static SidFile Tune(byte[] code, int play = 0, bool rsid = false)
    {
        var bytes = new byte[0x7C + code.Length];
        (rsid ? "RSID"u8 : "PSID"u8).CopyTo(bytes);
        void Word(int at, int value)
        {
            bytes[at] = (byte)(value >> 8);
            bytes[at + 1] = (byte)value;
        }

        Word(0x04, 2);
        Word(0x06, 0x7C);
        Word(0x08, 0x1000);
        Word(0x0A, 0x1000);
        Word(0x0C, play);
        Word(0x0E, 1);
        Word(0x10, 1);
        Word(0x76, 0x14);
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
    public void A_players_notes_become_rows_on_one_instrument_and_play_back_exactly()
    {
        var recording = Recording.Record(Arpeggiator(), 1, 2.0);
        var (score, report) = Transcriber.Transcribe(recording, Music.Of(recording));

        Assert.True(Agreement.Measure(recording, score).Exact);
        Assert.Equal(1.0, report.Music);
        Assert.Single(score.Instruments);
        var played = score.Patterns[0].Rows.Where(r => r.Note.Kind == RowNoteKind.Note).ToList();
        Assert.Equal([C4, E4, G4, C5, C4, E4], played.Take(6).Select(r => r.Note.Note));
        Assert.All(played.SkipLast(1), row => Assert.Equal(8, row.Calls));
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
