using MagSid.Core.Midi;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// An imported song is only worth having if it exports. This builds one from a MIDI file with
/// chords, rests, off-grid notes and repeated bars, then runs the exported image on the 6502
/// against the sequencer, frame by frame - the same check every song in SongData gets.
/// </summary>
public class MidiImportExportTests
{
    private const int Tpq = 120;

    private static MidiSong TwelveBars()
    {
        var random = new Random(64);
        var melody = new List<MidiNote>();
        var bass = new List<MidiNote>();
        for (int bar = 0; bar < 12; bar++)
        {
            long at = bar * 4L * Tpq;

            // Bars 0-3 come back as 8-11, so there is something to share.
            int seed = bar >= 8 ? bar - 8 : bar;
            var phrase = new Random(seed);
            for (int beat = 0; beat < 8; beat++)
            {
                if (phrase.Next(5) == 0)
                    continue;                                   // a rest
                int key = 60 + phrase.Next(-5, 13);
                long start = at + beat * Tpq / 2 + (bar == 5 ? random.Next(-7, 7) : 0);
                melody.Add(new MidiNote(0, Math.Max(0, start), start + Tpq / 2 - 10, key, 100));
                if (beat % 4 == 0)                              // a third under it: a chord
                    melody.Add(new MidiNote(0, Math.Max(0, start), start + Tpq / 2 - 10, key - 4, 90));
            }

            bass.Add(new MidiNote(1, at, at + 2 * Tpq, 36 + seed % 5, 100));
            bass.Add(new MidiNote(1, at + 2 * Tpq, at + 4 * Tpq, 43 + seed % 3, 100));
        }

        return new MidiSong(Tpq, 500_000, 4, 4, 0,
        [
            new MidiTrack(0, "Lead", melody.OrderBy(n => n.Start).ToList(), new Dictionary<int, int>()),
            new MidiTrack(1, "Bass", bass, new Dictionary<int, int>()),
        ]);
    }

    /// <summary>Kick on 1 and 3, snare on 2 and 4 with a flam, a hi-hat on every 8th, a tom.</summary>
    private static MidiTrack Drums(int bars)
    {
        var hits = new List<MidiNote>();
        for (int bar = 0; bar < bars; bar++)
        {
            long at = bar * 4L * Tpq;
            foreach (int beat in new[] { 0, 2 }) hits.Add(new MidiNote(9, at + beat * Tpq, at + beat * Tpq + 30, 36, 100));
            foreach (int beat in new[] { 1, 3 }) hits.Add(new MidiNote(9, at + beat * Tpq, at + beat * Tpq + 30, 38, 100));
            hits.Add(new MidiNote(9, at + 3 * Tpq - 30, at + 3 * Tpq, 38, 60));
            for (int eighth = 0; eighth < 8; eighth++) hits.Add(new MidiNote(9, at + eighth * Tpq / 2, at + eighth * Tpq / 2 + 20, 42, 80));
            if (bar % 4 == 3) hits.Add(new MidiNote(9, at + 3 * Tpq + Tpq / 2, at + 4 * Tpq, 45, 100));
        }
        return new MidiTrack(2, "Drums", hits.OrderBy(n => n.Start).ToList(), new Dictionary<int, int>());
    }

    [Fact]
    public void An_imported_song_exports_and_the_6502_plays_it_as_the_editor_does()
    {
        var midi = TwelveBars();
        var sources = midi.Sources();
        var (speed, _) = MidiImporter.SuggestSpeed(midi.Bpm, 4);
        var result = MidiImporter.Import(midi, new MidiImportOptions(
            [new VoiceImport(sources[0], VoiceRole.Melody), new VoiceImport(sources[0], VoiceRole.Bass),
             new VoiceImport(sources[1], VoiceRole.Bass, Transpose: 12)], 4, speed));

        Assert.True(result.Report.Patterns < 3 * 12, "repeated bars should share patterns");
        PlaysAsTheEditorDoes(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Drums_and_bass_on_one_voice_export_and_play_as_the_editor_does(bool hiHat)
    {
        var twelve = TwelveBars();
        var midi = twelve with { Tracks = [.. twelve.Tracks, Drums(12)] };
        var sources = midi.Sources();
        var (speed, _) = MidiImporter.SuggestSpeed(midi.Bpm, 4);
        var result = MidiImporter.Import(midi, new MidiImportOptions(
            [new VoiceImport(sources[1], VoiceRole.DrumsAndBass, Drums: sources[2], HiHat: hiHat),
             new VoiceImport(sources[0], VoiceRole.Melody), new VoiceImport(null, VoiceRole.Melody)], 4, speed));

        Assert.Contains(result.Song.Instruments, i => i.Name == "KickBass");
        Assert.Contains(result.Song.Instruments, i => i.Name == "Snare");
        Assert.Equal(hiHat, result.Song.Instruments.Any(i => i.Name == "Hihat"));
        PlaysAsTheEditorDoes(result);
    }

    private static void PlaysAsTheEditorDoes(MidiImportResult result)
    {
        var song = result.Song;
        var image = SongImageBuilder.Build(song);
        Assert.Equal(result.Report.TotalBytes, image.Length);

        var fromCpu = new Dictionary<int, int>();
        var cpu = new PlayerCpu((register, value) => fromCpu[register] = value);
        Array.Copy(image, 0, cpu.Memory, 0x1000, image.Length);
        cpu.Call(0x1000, a: 0);

        var fromSequencer = new Dictionary<int, int>();
        var sequencer = new SongSequencer(song, (register, value) => fromSequencer[register] = value & 0xFF);

        int frames = SongCoverageAnalyzer.PassFrames(song) + 200;
        for (int frame = 0; frame < frames; frame++)
        {
            fromCpu.Clear();
            fromSequencer.Clear();
            cpu.Call(0x1003);
            sequencer.Frame();

            Assert.True(fromCpu.Count == fromSequencer.Count
                        && fromCpu.All(w => fromSequencer.TryGetValue(w.Key, out int v) && v == w.Value),
                $"frame {frame}: the 6502 and the sequencer disagree");
        }
    }
}
