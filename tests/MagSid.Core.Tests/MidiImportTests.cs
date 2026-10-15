using MagSid.Core.Midi;
using MagSid.Core.Model;
using MagSid.Core.Validation;
using Xunit;

namespace MagSid.Core.Tests;

public class MidiImportTests
{
    private const int Tpq = 96;          // ticks per quarter in every file built here
    private const int Row = Tpq / 4;     // one 16th - a row at the 16th grid

    /// <summary>Builds a standard MIDI file from absolute-time events, so a test reads as the
    /// notes it plays.</summary>
    private sealed class Smf
    {
        private readonly List<List<(long Tick, byte[] Bytes)>> _tracks = [];
        private List<(long Tick, byte[] Bytes)> Current => _tracks[^1];

        public Smf Track(string? name = null)
        {
            _tracks.Add([]);
            if (name is not null)
                Meta(0, 0x03, System.Text.Encoding.ASCII.GetBytes(name));
            return this;
        }

        public Smf Meta(long tick, int type, byte[] body)
        {
            Current.Add((tick, [0xFF, (byte)type, (byte)body.Length, .. body]));
            return this;
        }

        public Smf Tempo(int bpm)
        {
            int us = 60_000_000 / bpm;
            return Meta(0, 0x51, [(byte)(us >> 16), (byte)(us >> 8), (byte)us]);
        }

        public Smf Note(long start, long length, int key, int channel = 0, int velocity = 100)
        {
            Current.Add((start, [(byte)(0x90 | channel), (byte)key, (byte)velocity]));
            Current.Add((start + length, [(byte)(0x80 | channel), (byte)key, 0]));
            return this;
        }

        public Smf Raw(long tick, params byte[] bytes)
        {
            Current.Add((tick, bytes));
            return this;
        }

        public byte[] Bytes(int format = 1)
        {
            var file = new List<byte>();
            file.AddRange("MThd"u8.ToArray());
            file.AddRange(BigEndian(6, 4));
            file.AddRange(BigEndian(format, 2));
            file.AddRange(BigEndian(_tracks.Count, 2));
            file.AddRange(BigEndian(Tpq, 2));

            foreach (var track in _tracks)
            {
                var body = new List<byte>();
                long last = 0;
                // Offs before ons at the same tick, as a sequencer writes them.
                foreach (var (tick, bytes) in track.OrderBy(e => e.Tick).ThenBy(e => (e.Bytes[0] & 0xF0) == 0x90 ? 1 : 0))
                {
                    body.AddRange(VarLen(tick - last));
                    body.AddRange(bytes);
                    last = tick;
                }
                body.AddRange([0x00, 0xFF, 0x2F, 0x00]);
                file.AddRange("MTrk"u8.ToArray());
                file.AddRange(BigEndian(body.Count, 4));
                file.AddRange(body);
            }

            return file.ToArray();
        }

        private static byte[] BigEndian(int value, int bytes) =>
            Enumerable.Range(0, bytes).Select(i => (byte)(value >> (8 * (bytes - 1 - i)))).ToArray();

        private static byte[] VarLen(long value)
        {
            var bytes = new List<byte> { (byte)(value & 0x7F) };
            while ((value >>= 7) > 0)
                bytes.Insert(0, (byte)(0x80 | (value & 0x7F)));
            return bytes.ToArray();
        }
    }

    private static MidiSong Read(Smf smf) => MidiFileReader.Read(smf.Bytes());

    private static MidiImportResult Import(MidiSong midi, VoiceImport? v1 = null, VoiceImport? v2 = null,
                                           VoiceImport? v3 = null, int rowsPerBeat = 4, int speed = 5) =>
        MidiImporter.Import(midi, new MidiImportOptions(
            [v1 ?? new VoiceImport(null, VoiceRole.Melody), v2 ?? new VoiceImport(null, VoiceRole.Melody),
             v3 ?? new VoiceImport(null, VoiceRole.Bass)], rowsPerBeat, speed));

    private static string Rows(Song song, int channel) =>
        string.Join(" ", song.ChannelOrders[channel].Tokens.OfType<PatternRefToken>()
            .SelectMany(t => song.Patterns[t.PatternNumber - 1].Rows)
            .Select(r => r switch { NoteRow n => n.Note.ToString(), NoteOffRow => "---", _ => "." }));

    // ---------------------------------------------------------------------------- the file reader

    [Fact]
    public void Reads_tracks_names_tempo_signature_and_notes()
    {
        var midi = Read(new Smf()
            .Track("Conductor").Tempo(140).Meta(0, 0x58, [3, 2, 24, 8])
            .Track("Lead").Raw(0, 0xC0, 81).Note(0, Tpq, 60).Note(Tpq, Tpq, 62));

        Assert.Equal(Tpq, midi.TicksPerQuarter);
        Assert.Equal(140, midi.Bpm, 0);
        Assert.Equal((3, 4), (midi.BeatsPerBar, midi.BeatUnit));
        Assert.Equal(["Conductor", "Lead"], midi.Tracks.Select(t => t.Name));

        var source = Assert.Single(midi.Sources());
        Assert.Equal("Lead", source.Name);
        Assert.Equal(81, source.Program);
        Assert.Equal([(0L, (long)Tpq, 60), (Tpq, 2 * Tpq, 62)], source.Notes.Select(n => (n.Start, n.End, n.Key)));
    }

    [Fact]
    public void Running_status_and_a_zero_velocity_note_on_end_a_note()
    {
        // 90 3C 64, then running status: 3C 00 ends it, 3E 64 / 3E 00 is a second note.
        var midi = Read(new Smf().Track()
            .Raw(0, 0x90, 60, 100).Raw(Tpq, 60, 0).Raw(Tpq, 62, 100).Raw(2 * Tpq, 62, 0));

        Assert.Equal([(0L, (long)Tpq, 60), (Tpq, 2 * Tpq, 62)],
            midi.Sources().Single().Notes.Select(n => (n.Start, n.End, n.Key)));
    }

    [Fact]
    public void A_format_0_file_offers_each_channel_as_its_own_source()
    {
        var midi = MidiFileReader.Read(new Smf().Track()
            .Note(0, Tpq, 72, channel: 0).Note(0, Tpq, 36, channel: 1).Note(0, Tpq, 38, channel: 9).Bytes(format: 0));

        Assert.Equal([0, 1, 9], midi.Sources().Select(s => s.Channel));
        Assert.True(midi.Sources()[2].IsDrums);
    }

    [Theory]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46 }, "Not a MIDI file")]
    [InlineData(new byte[] { 0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 1, 0, 1 }, "ends in the middle")]
    public void A_broken_file_is_refused_with_a_reason(byte[] data, string reason)
    {
        var ex = Assert.Throws<MidiFileException>(() => MidiFileReader.Read(data));
        Assert.Contains(reason, ex.Message);
    }

    // ---------------------------------------------------------------------------- the conversion

    [Fact]
    public void A_melody_becomes_notes_holds_and_a_note_off_on_the_16th_grid()
    {
        // Quarter C4, eighth D4, rest an eighth, half E4. MIDI 60 is the player's C-4.
        var midi = Read(new Smf().Track().Note(0, Tpq, 60).Note(Tpq, Tpq / 2, 62).Note(2 * Tpq, 2 * Tpq, 64));
        var source = midi.Sources().Single();

        var result = Import(midi, new VoiceImport(source, VoiceRole.Melody));

        Assert.Equal("C-4 . . . D-4 . --- . E-4 . . . . . . .", Rows(result.Song, 0));
        Assert.Equal(1, result.Report.Bars);
        Assert.Equal(3, result.Report.NotesPlaced);
        Assert.Empty(SongValidator.Validate(result.Song));
    }

    [Fact]
    public void A_melody_keeps_the_top_of_a_chord_and_a_bass_the_bottom()
    {
        var midi = Read(new Smf().Track().Note(0, Tpq, 60).Note(0, Tpq, 64).Note(0, Tpq, 67));
        var source = midi.Sources().Single();

        var result = Import(midi, new VoiceImport(source, VoiceRole.Melody), v3: new VoiceImport(source, VoiceRole.Bass));

        Assert.StartsWith("G-4", Rows(result.Song, 0));
        Assert.StartsWith("C-4", Rows(result.Song, 2));
        Assert.Equal(2 + 2, result.Report.NotesDropped);   // each voice drops two of the three
    }

    [Fact]
    public void A_higher_note_takes_over_and_the_held_note_comes_back_after_it()
    {
        // C4 held a whole bar, a grace E4 on the second beat.
        var midi = Read(new Smf().Track().Note(0, 4 * Tpq, 60).Note(Tpq, Row, 64));

        var result = Import(midi, new VoiceImport(midi.Sources().Single(), VoiceRole.Melody));

        Assert.Equal("C-4 . . . E-4 C-4 . . . . . . . . . .", Rows(result.Song, 0));
    }

    [Fact]
    public void Notes_off_the_grid_snap_to_the_nearest_row_and_are_counted()
    {
        var midi = Read(new Smf().Track().Note(Row + 3, Row, 60).Note(2 * Row, Row, 62));

        var result = Import(midi, new VoiceImport(midi.Sources().Single(), VoiceRole.Melody));

        Assert.StartsWith(". C-4 D-4 ---", Rows(result.Song, 0));
        Assert.Equal(1, result.Report.NotesMoved);
    }

    [Fact]
    public void A_note_outside_the_players_range_moves_by_octaves_and_is_counted()
    {
        var midi = Read(new Smf().Track().Note(0, Tpq, 120).Note(Tpq, Tpq, 5));

        var result = Import(midi, new VoiceImport(midi.Sources().Single(), VoiceRole.Melody));

        Assert.StartsWith("C-7 . . . F-0", Rows(result.Song, 0));
        Assert.Equal(2, result.Report.NotesShifted);
    }

    [Fact]
    public void Transpose_moves_a_voice()
    {
        var midi = Read(new Smf().Track().Note(0, Tpq, 60));

        var result = Import(midi, new VoiceImport(midi.Sources().Single(), VoiceRole.Melody, Transpose: -12));

        Assert.StartsWith("C-3", Rows(result.Song, 0));
    }

    [Fact]
    public void A_bar_that_repeats_is_stored_once()
    {
        var smf = new Smf().Track();
        for (int bar = 0; bar < 8; bar++)
            smf.Note(bar * 4 * Tpq, Tpq, bar % 2 == 0 ? 60 : 67);
        var midi = Read(smf);

        var result = Import(midi, new VoiceImport(midi.Sources().Single(), VoiceRole.Melody));

        // Two different bars plus the silent voices' pattern, however long the patterns are.
        Assert.Equal(8, result.Report.Bars);
        Assert.True(result.Song.Patterns.Count <= 3);
        Assert.Equal(8 * 16, Rows(result.Song, 0).Split(' ').Length);
    }

    [Fact]
    public void The_pattern_length_is_whichever_makes_the_smallest_song()
    {
        // A bar of 16ths on one pitch each bar, all different: nothing repeats, so longer patterns
        // only save pointers and order bytes.
        var smf = new Smf().Track();
        for (int bar = 0; bar < 8; bar++)
            smf.Note(bar * 4 * Tpq, 4 * Tpq, 48 + bar);
        var midi = Read(smf);

        var result = Import(midi, new VoiceImport(midi.Sources().Single(), VoiceRole.Melody));

        Assert.Equal(4, result.Report.BarsPerPattern);
        Assert.Equal(result.Report.TotalBytes, SongSizeCalculator.Measure(result.Song).TotalBytes);
    }

    [Fact]
    public void Only_voices_that_play_get_an_instrument()
    {
        var midi = Read(new Smf().Track().Note(0, Tpq, 40));
        var bass = Assert.Single(midi.Sources());

        var result = Import(midi, v3: new VoiceImport(bass, VoiceRole.Bass));

        var instrument = Assert.Single(result.Song.Instruments);
        Assert.Equal((1, "Bass"), (instrument.Number, instrument.Name));
        Assert.StartsWith("E-2", Rows(result.Song, 2));
        Assert.Equal(new NoteRow(NoteName.Parse("E-2"), 1), result.Song.Patterns
            .SelectMany(p => p.Rows).OfType<NoteRow>().Single());
    }

    [Fact]
    public void A_bar_range_imports_only_those_bars()
    {
        var smf = new Smf().Track();
        for (int bar = 0; bar < 4; bar++)
            smf.Note(bar * 4 * Tpq, Tpq, 60 + bar);
        var midi = Read(smf);
        var voices = new[] { new VoiceImport(midi.Sources().Single(), VoiceRole.Melody),
                             new VoiceImport(null, VoiceRole.Melody), new VoiceImport(null, VoiceRole.Bass) };

        var result = MidiImporter.Import(midi, new MidiImportOptions(voices, 4, 5, FirstBar: 2, BarCount: 1));

        Assert.Equal("D-4 . . . --- . . . . . . . . . . .", Rows(result.Song, 0));   // bar 3 is key 62
    }

    [Fact]
    public void Every_voice_silent_is_refused()
    {
        var midi = Read(new Smf().Track().Note(0, Tpq, 60));

        Assert.Throws<MidiImportException>(() => Import(midi));
    }

    // ---------------------------------------------------------------------------- drums and bass

    private static string Named(Song song, int channel) =>
        string.Join(" ", song.ChannelOrders[channel].Tokens.OfType<PatternRefToken>()
            .SelectMany(t => song.Patterns[t.PatternNumber - 1].Rows)
            .Select(r => r switch
            {
                NoteRow n => $"{n.Note}:{song.Instruments.Single(i => i.Number == n.InstrumentNumber).Name}",
                NoteOffRow => "---",
                _ => ".",
            }));

    /// <summary>Bar 2 of the Drax transcription: kick on 1 and 3, the snare on 2 and 4 as flams,
    /// a hi-hat on most rows, and a 16th bass line jumping octaves.</summary>
    private static MidiSong DraxBar()
    {
        var smf = new Smf().Track("drums");
        foreach (int row in new[] { 0, 8 }) smf.Note(row * Row, Row, 36);
        foreach (int row in new[] { 3, 4, 11, 12 }) smf.Note(row * Row, Row, 38);
        foreach (int row in new[] { 0, 1, 2, 6, 7, 8, 9, 10, 12, 14 }) smf.Note(row * Row, Row / 2, 42);
        smf.Track("bass");
        foreach (var (row, key) in new[] { (1, 41), (2, 39), (3, 29), (5, 39), (6, 41), (7, 29), (9, 29),
                                           (10, 27), (12, 32), (13, 34), (14, 36), (15, 39) })
            smf.Note(row * Row, Row, key);
        return Read(smf);
    }

    [Fact]
    public void Kick_snare_and_bass_share_one_voice_the_way_the_demo_songs_do()
    {
        var midi = DraxBar();
        var (drums, bass) = (midi.Sources()[0], midi.Sources()[1]);

        var result = Import(midi, new VoiceImport(bass, VoiceRole.DrumsAndBass, Drums: drums));

        // The kick takes the bass note and the bass is not struck again under it (rows 1, 9); the
        // flams land on the beat (4, 12); the snare carries the bass note; no hi-hat.
        Assert.Equal(
            "F-2:KickBass . D#2:Bass F-1:Bass F-1:Snare D#2:Bass F-2:Bass F-1:Bass " +
            "F-1:KickBass . D#1:Bass --- G#1:Snare A#1:Bass C-2:Bass D#2:Bass",
            Named(result.Song, 0));
        Assert.Equal(["KickBass", "Bass", "Snare"], result.Song.Instruments.Select(i => i.Name));
        Assert.Equal(4, result.Report.DrumHits);
        Assert.Equal(10 + 2, result.Report.DrumsLeftOut);    // every hi-hat, and the flams' first hits
        Assert.Equal(1, result.Report.NotesDropped);         // G#1, under the snare
        Assert.Empty(SongValidator.Validate(result.Song));
    }

    [Fact]
    public void With_the_hi_hat_on_it_takes_only_rows_the_bass_has_left()
    {
        var smf = new Smf().Track("drums").Note(0, Row, 36);
        for (int row = 0; row < 6; row++) smf.Note(row * Row, Row / 2, 42);
        smf.Track("bass").Note(0, 2 * Row, 36);
        var midi = Read(smf);

        var result = Import(midi, new VoiceImport(midi.Sources()[1], VoiceRole.DrumsAndBass,
                                                  Drums: midi.Sources()[0], HiHat: true));

        Assert.StartsWith("C-2:KickBass . C-2:Hihat C-2:Hihat C-2:Hihat C-2:Hihat .", Named(result.Song, 0));
    }

    [Fact]
    public void Without_a_bass_part_the_drums_still_play_and_toms_are_left_out()
    {
        var midi = Read(new Smf().Track("Percussion").Note(0, Row, 36).Note(4 * Row, Row, 40).Note(8 * Row, Row, 45));
        var drums = midi.Sources().Single();
        Assert.True(drums.LooksLikeDrums);

        var result = Import(midi, new VoiceImport(null, VoiceRole.DrumsAndBass, Drums: drums));

        Assert.StartsWith("C-2:KickBass . . . C-2:Snare . . . .", Named(result.Song, 0));
        Assert.Equal((2, 1), (result.Report.DrumHits, result.Report.DrumsLeftOut));
    }

    // ---------------------------------------------------------------------------- chords

    private static double[] Weights(params (int Pc, double Weight)[] notes)
    {
        var weights = new double[12];
        foreach (var (pc, weight) in notes) weights[pc] += weight;
        return weights;
    }

    [Theory]
    [InlineData(0, 4, 7)]     // C E G
    [InlineData(4, 7, 0)]     // E G C
    [InlineData(7, 0, 4)]     // G C E
    public void Every_inversion_of_a_chord_is_the_same_chord(int first, int second, int third)
    {
        // The first note is the lowest, so it carries the bass bonus - the inversion's own root.
        var chord = ChordAnalyzer.Identify(Weights((first, 6), (second, 4), (third, 4)), null);

        Assert.Equal(new Chord(0, ChordQuality.Major), chord);
        Assert.Equal("C", chord!.Name);
    }

    [Fact]
    public void Minor_seventh_and_plain_triads_are_told_apart()
    {
        Assert.Equal("Am", ChordAnalyzer.Identify(Weights((9, 4), (0, 4), (4, 4)), null)!.Name);
        Assert.Equal("G7", ChordAnalyzer.Identify(Weights((7, 4), (11, 4), (2, 4), (5, 4)), null)!.Name);

        // A seventh heard only in passing does not make a four-note chord.
        Assert.Equal("G", ChordAnalyzer.Identify(Weights((7, 4), (11, 4), (2, 4), (5, 0.5)), null)!.Name);
    }

    [Fact]
    public void A_chord_never_has_more_than_four_notes()
    {
        // Every note of C major 9 and more.
        var chord = ChordAnalyzer.Identify(Weights((0, 4), (4, 4), (7, 4), (11, 4), (2, 4), (5, 2)), null)!;

        Assert.InRange(chord.Notes.Count, 3, 4);
    }

    [Fact]
    public void Two_notes_of_the_chord_already_playing_keep_it()
    {
        var c = new Chord(0, ChordQuality.Major);

        Assert.Equal(c, ChordAnalyzer.Identify(Weights((0, 4), (7, 4)), c));
        Assert.Null(ChordAnalyzer.Identify(new double[12], c));
    }

    [Fact]
    public void A_chord_is_an_arpeggio_instrument_around_the_middle_of_the_range()
    {
        var instrument = MidiImporter.ChordInstrument(new Chord(9, ChordQuality.Minor));

        Assert.Equal("Chord Am", instrument.Name);
        Assert.Equal(["A-3", "C-4", "E-4"], instrument.Arpeggio.Select(n => n.ToString()));
        Assert.Equal(ArpeggioEnding.Loop, instrument.ArpeggioEnding);
    }

    /// <summary>A bar each of C, F, G and C again - the last as E-G-C, held and broken - with a
    /// tune on top.</summary>
    private static MidiSong ChordsAndTune()
    {
        var smf = new Smf().Track("Tune");
        for (int beat = 0; beat < 16; beat++)
            smf.Note(beat * Tpq, Tpq, 72 + beat % 3 * 2);
        smf.Track("Piano");
        int[][] bars = [[60, 64, 67], [65, 69, 72], [67, 71, 74], [64, 67, 72]];
        for (int bar = 0; bar < 4; bar++)
            foreach (int key in bars[bar])
                smf.Note(bar * 4 * Tpq, 4 * Tpq, key);
        return Read(smf);
    }

    [Fact]
    public void Chords_play_on_their_voice_one_instrument_per_chord_whatever_the_inversion()
    {
        var midi = ChordsAndTune();
        var (tune, piano) = (midi.Sources()[0], midi.Sources()[1]);

        var result = Import(midi, new VoiceImport(tune, VoiceRole.Melody), new VoiceImport(piano, VoiceRole.Chords));

        Assert.Equal(
            "C-4:Chord C . . . . . . . . . . . . . . . " +
            "F-4:Chord F . . . . . . . . . . . . . . . " +
            "G-3:Chord G . . . . . . . . . . . . . . . " +
            "C-4:Chord C . . . . . . . . . . . . . . .",
            Named(result.Song, 1));
        Assert.Equal((3, 3), (result.Report.ChordsFound, result.Report.ChordsKept));
        Assert.Empty(SongValidator.Validate(result.Song));
    }

    [Fact]
    public void More_chords_than_instruments_play_their_nearest_kept_chord()
    {
        // Twenty different major and minor chords, one a bar, the first ones longest.
        var smf = new Smf().Track("Tune");
        smf.Note(0, Tpq, 84);
        smf.Track("Piano");
        long at = 0;
        for (int i = 0; i < 20; i++)
        {
            int root = 48 + i % 12, third = i < 12 ? 4 : 3;
            long length = (i < 4 ? 8 : 4) * Tpq;
            foreach (int key in new[] { root, root + third, root + 7 })
                smf.Note(at, length, key);
            at += length;
        }
        var midi = Read(smf);

        var result = Import(midi, new VoiceImport(midi.Sources()[0], VoiceRole.Melody),
                            new VoiceImport(midi.Sources()[1], VoiceRole.Chords));

        Assert.Equal(20, result.Report.ChordsFound);
        Assert.Equal(15, result.Report.ChordsKept);       // 16 less the melody's instrument
        Assert.Equal(16, result.Song.Instruments.Count);
        Assert.Empty(SongValidator.Validate(result.Song));
    }

    [Fact]
    public void A_part_that_carries_chords_is_suggested_for_voice_2()
    {
        var voices = MidiImporter.SuggestVoices(ChordsAndTune());

        Assert.Equal(VoiceRole.Chords, voices[1].Role);
        Assert.Equal("Piano", voices[1].Source!.Name);
        Assert.Equal("Tune", voices[0].Source!.Name);
    }

    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(1, 0x02)]
    [InlineData(2, 0x04)]
    public void The_kits_filter_is_routed_to_the_channel_it_plays_on(int channel, int voiceBit)
    {
        var kick = MidiImporter.Kit("KickBass", channel);

        Assert.All(kick.Filter.Where(f => f.IsSetup), f => Assert.Equal((byte)(0xF0 | voiceBit), f.ResonanceRouting!.Value));
    }

    [Theory]
    [InlineData(120, 4, 5, 125.0)]
    [InlineData(100, 4, 7, 93.75)]
    [InlineData(120, 8, 2, 125.0)]
    [InlineData(150, 4, 4, 150.0)]
    public void The_suggested_speed_is_the_nearest_whole_frame_row(double bpm, int rowsPerBeat, int speed, double plays)
    {
        Assert.Equal((speed, plays), MidiImporter.SuggestSpeed(bpm, rowsPerBeat));
    }
}
