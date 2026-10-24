using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Core.SidImport;
using MagSid.Playback;

namespace MagSid.Playback.Tests;

/// <summary>
/// Import SID, judged the only way that means anything: the imported song is played through
/// MagSID's own sequencer and its notes are compared with the original's (SidTranscriber.Agreement)
/// - same voice, same note, within half a row. The demo songs are the clearest test, since every
/// one of their sounds can be said exactly in MagSID's format.
/// </summary>
public class SidImportTests
{
    private static string SongDataDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SongData");

    private static (SidTranscriber.Analysis Original, SidImportResult Result, double Agreement) RoundTrip(SidTrace trace)
    {
        var original = SidTranscriber.Analyze(trace);
        var options = new SidImportOptions(SidTranscriber.SuggestFramesPerRow(original), 0, original.FrameCount);
        var result = SidTranscriber.Import(original, options);
        var replayed = SidTranscriber.Analyze(SidImportTrace.Replay(result.Song, options.FrameCount));
        return (original, result, SidTranscriber.Agreement(original, options, replayed));
    }

    [Theory]
    [InlineData("BellaCiao.s", 0.95)]
    [InlineData("Lightforce.s", 0.95)]
    [InlineData("Breakdance.s", 0.9)]
    public void A_demo_song_comes_back_with_its_notes(string file, double atLeast)
    {
        var song = PatternsFileReader.Read(Path.Combine(SongDataDir, file));
        var (original, result, agreement) = RoundTrip(SidImportTrace.Replay(song, 50 * 40));

        Assert.True(original.NoteCount > 100);
        Assert.True(agreement >= atLeast, $"{file}: {agreement:P1} of the notes came back");
        Assert.InRange(result.Song.Instruments.Count, 1, 16);
    }

    [Fact]
    public void A_gate_held_while_the_pitch_moves_is_a_legato_note()
    {
        // One gate-on, then C-4 for 10 frames, E-4 for 10, G-4 for 10: three notes.
        var frames = new List<IReadOnlyList<(int, int)>>();
        foreach (var name in new[] { "C-4", "E-4", "G-4" })
        {
            var (lo, hi) = MagSid.Core.NotePalTable.GetFreq(NoteName.Parse(name));
            for (int i = 0; i < 10; i++)
            {
                var writes = new List<(int, int)> { (0, lo), (1, hi), (5, 0x00), (6, 0xF0) };
                if (frames.Count == 0)
                    writes.Add((4, 0x21));
                frames.Add(writes);
            }
        }

        var analysis = SidTranscriber.Analyze(new SidTrace([], frames));

        Assert.Equal(3, analysis.Notes[0].Count);
        Assert.Equal([false, true, true], analysis.Notes[0].Select(n => n.Legato));

        // The sound before a legato note is NoHardRestart, so MagSID plays it legato too.
        var result = SidTranscriber.Import(analysis, new SidImportOptions(10, 0, analysis.FrameCount));
        Assert.Contains(result.Song.Instruments, i => i.NoHardRestart);
    }

    /// <summary>One voice with the gate on throughout: C-4 for 10 frames, a frame with the test bit,
    /// then <paramref name="after"/> for 10 - as Laxity's player starts a note without the gate.</summary>
    private static SidTranscriber.Analysis HeldThroughATestBit(string after)
    {
        var frames = new List<IReadOnlyList<(int, int)>>();
        for (int i = 0; i < 21; i++)
        {
            var (lo, hi) = MagSid.Core.NotePalTable.GetFreq(NoteName.Parse(i < 11 ? "C-4" : after));
            var writes = new List<(int, int)> { (0, lo), (1, hi), (5, 0x00), (6, 0xF0) };
            if (i == 0) writes.Add((4, 0x41));
            if (i == 10) writes.Add((4, 0x49));
            if (i == 11) writes.Add((4, 0x41));
            frames.Add(writes);
        }

        return SidTranscriber.Analyze(new SidTrace([], frames));
    }

    [Fact]
    public void A_note_restarted_with_the_test_bit_and_the_gate_held_is_a_new_note()
    {
        var analysis = HeldThroughATestBit("G-4");

        Assert.Equal([0, 10], analysis.Notes[0].Select(n => n.Start));
    }

    [Fact]
    public void A_test_bit_on_the_same_pitch_is_no_new_note()
    {
        // A drum resetting its noise: the pitch goes on where it was.
        var analysis = HeldThroughATestBit("C-4");

        Assert.Single(analysis.Notes[0]);
    }

    [Fact]
    public void A_hard_restart_before_the_next_note_is_not_a_note_off()
    {
        // Gate on 8 frames, off for 2 (the tune's hard restart), on again.
        var frames = new List<IReadOnlyList<(int, int)>>();
        for (int note = 0; note < 4; note++)
        {
            for (int i = 0; i < 10; i++)
            {
                frames.Add(i switch
                {
                    0 => [(0, 0x00), (1, 0x10), (4, 0x41)],
                    8 => [(4, 0x40)],
                    _ => [],
                });
            }
        }

        var analysis = SidTranscriber.Analyze(new SidTrace([], frames));
        var result = SidTranscriber.Import(analysis, new SidImportOptions(10, 0, analysis.FrameCount));

        Assert.All(analysis.Notes[0].Take(3), n => Assert.Null(n.GateOff));
        Assert.DoesNotContain(result.Song.Patterns.SelectMany(p => p.Rows), r => r is NoteOffRow);
    }

    /// <summary>Notes on one voice, each 24 frames: the gate on, and the frequency
    /// <paramref name="pitch"/> gives for each frame into the note.</summary>
    private static SidTranscriber.Analysis Notes(int count, Func<int, int> pitch)
    {
        var frames = new List<IReadOnlyList<(int, int)>>();
        for (int note = 0; note < count; note++)
        {
            for (int i = 0; i < 24; i++)
            {
                int frequency = pitch(i);
                var writes = new List<(int, int)> { (0, frequency & 0xFF), (1, frequency >> 8) };
                if (i == 0)
                    writes.AddRange([(5, 0x00), (6, 0xF0), (4, 0x41)]);
                else if (i == 22)
                    writes.Add((4, 0x40));
                frames.Add(writes);
            }
        }

        return SidTranscriber.Analyze(new SidTrace([], frames));
    }

    private static int Frequency(string note)
    {
        var (lo, hi) = MagSid.Core.NotePalTable.GetFreq(NoteName.Parse(note));
        return lo | (hi << 8);
    }

    [Fact]
    public void A_pitch_that_swings_wider_than_a_vibrato_is_not_made_one()
    {
        // A bass bending up and down by four semitones, as Viva Las Vegas's does: imported as a
        // vibrato of depth 255 it dragged every bass note off its pitch.
        // As Viva Las Vegas's bass does, frame by frame: tuned a third of a semitone off the table,
        // an octave strike on its second frame, then a small wobble in its tail. That turns often
        // enough to look like a vibrato, but its swing is an octave.
        int d2 = (int)(Frequency("D#2") * 1.02);
        var analysis = Notes(4, i => i switch
        {
            1 => 2 * d2,
            8 => d2 + 16,
            10 => d2 - 16,
            >= 11 => d2 - 32,
            _ => d2,
        });

        var result = SidTranscriber.Import(analysis, new SidImportOptions(24, 0, analysis.FrameCount));

        Assert.All(result.Song.Instruments, i => Assert.Equal(0, i.VibratoDepth));
        Assert.All(result.Song.Patterns.SelectMany(p => p.Rows).OfType<NoteRow>(), r => Assert.Equal("D#2", r.Note.ToString()));
    }

    [Fact]
    public void Heard_is_what_the_import_sounds_not_what_it_thinks()
    {
        // A-4 notes imported right sound right; the same song a semitone up sounds wrong on every
        // frame, whatever notes it believes it plays.
        var analysis = Notes(4, _ => Frequency("A-4"));
        var options = new SidImportOptions(24, 0, analysis.FrameCount);
        var result = SidTranscriber.Import(analysis, options);
        var right = SidTranscriber.Heard(analysis, options, SidTranscriber.Analyze(SidImportTrace.Replay(result.Song, analysis.FrameCount)));
        Assert.True(right.Share >= 0.9, $"{right.Share:P0}");

        foreach (var pattern in result.Song.Patterns)
            for (int r = 0; r < pattern.Rows.Count; r++)
                if (pattern.Rows[r] is NoteRow note)
                    pattern.Rows[r] = note with { Note = NoteName.Parse("A#4") };
        var wrong = SidTranscriber.Heard(analysis, options, SidTranscriber.Analyze(SidImportTrace.Replay(result.Song, analysis.FrameCount)));
        Assert.True(wrong.Share < 0.1, $"{wrong.Share:P0}");
    }

    [Fact]
    public void The_measured_choice_keeps_a_swing_that_sounds_as_well_and_costs_less()
    {
        // Rows of 8 and 5 frames: one frame a row would follow them too, at about twice the bytes.
        var analysis = Swinging(8, 5, 96);
        var choice = SidImportChoice.Best(analysis, 0, analysis.FrameCount);

        Assert.NotNull(choice);
        Assert.Equal((8, (int?)5), (choice.Options.FramesPerRow, choice.Options.FunkFramesPerRow));
        Assert.True(choice.Heard.Share >= 0.9, $"{choice.Heard.Share:P0}");
    }

    [Fact]
    public void A_real_vibrato_is_kept()
    {
        // A-4, wobbling a little either side every two frames after the first four.
        int a4 = Frequency("A-4");
        var analysis = Notes(4, i => i < 4 ? a4 : a4 + (i / 2 % 2 == 0 ? 40 : -40));

        var result = SidTranscriber.Import(analysis, new SidImportOptions(24, 0, analysis.FrameCount));

        Assert.Contains(result.Song.Instruments, i => i.VibratoDepth > 0);
        Assert.All(result.Song.Patterns.SelectMany(p => p.Rows).OfType<NoteRow>(), r => Assert.Equal("A-4", r.Note.ToString()));
    }

    [Fact]
    public void A_wavetable_never_gets_a_zero_step()
    {
        // $00 ends a wavetable in MagSID's format; a tune that writes it mid-sound must not end
        // the instrument's file there.
        var frames = new List<IReadOnlyList<(int, int)>>();
        for (int note = 0; note < 3; note++)
        {
            for (int i = 0; i < 12; i++)
            {
                frames.Add(i switch
                {
                    0 => [(0, 0x00), (1, 0x20), (4, 0x81)],
                    1 => [(4, 0x00)],
                    2 => [(4, 0x41)],
                    10 => [(4, 0x40)],
                    _ => [],
                });
            }
        }

        var analysis = SidTranscriber.Analyze(new SidTrace([], frames));
        var result = SidTranscriber.Import(analysis, new SidImportOptions(12, 0, analysis.FrameCount));

        Assert.All(result.Song.Instruments, i => Assert.DoesNotContain((byte)0, i.Wavetable));
        Assert.All(result.Song.Instruments, i => InstrumentFile.Read(InstrumentFile.Write(i)));
    }

    [Fact]
    public void A_tune_too_long_for_the_order_list_imports_as_much_as_fits()
    {
        // A different note every row on all three voices for twelve minutes: nothing repeats, so
        // the three order lists outgrow the player's 255 bytes.
        var frames = new List<IReadOnlyList<(int, int)>>();
        var random = new Random(7);
        for (int row = 0; row < 50 * 720 / 6; row++)
        {
            for (int i = 0; i < 6; i++)
            {
                var writes = new List<(int, int)>();
                for (int v = 0; v < 3; v++)
                {
                    if (i == 0)
                        writes.AddRange([(v * 7, random.Next(256)), (v * 7 + 1, random.Next(8, 64)), (v * 7 + 4, 0x41)]);
                    else if (i == 4)
                        writes.Add((v * 7 + 4, 0x40));
                }
                frames.Add(writes);
            }
        }

        var analysis = SidTranscriber.Analyze(new SidTrace([], frames));
        var options = new SidImportOptions(6, 0, analysis.FrameCount);
        Assert.Throws<SidImportException>(() => SidTranscriber.Import(analysis, options));

        int? fits = SidTranscriber.LongestThatFits(analysis, options);
        Assert.NotNull(fits);
        Assert.InRange(fits.Value, 50 * 4, analysis.FrameCount - 1);
        SidTranscriber.Import(analysis, options with { FrameCount = fits.Value });
    }

    // ------------------------------------------------------------------------------ roles

    /// <summary>A tune of 16-row bars at 6 frames a row: each note a voice, a row, and its frames as
    /// (control, frequency) while the gate is on - the gate goes off after them.</summary>
    private static SidImportResult Tune(int bars, params (int Voice, Func<int, int?> Row, (int Control, int Frequency)[] Frames)[] parts)
    {
        const int framesPerRow = 6;
        int frameCount = bars * 16 * framesPerRow;
        var frames = Enumerable.Range(0, frameCount).Select(_ => new List<(int, int)>()).ToList();
        foreach (var (voice, rowOf, sound) in parts)
        {
            for (int bar = 0; bar < bars; bar++)
            {
                for (int step = 0; step < 16; step++)
                {
                    if (rowOf(bar * 16 + step) is not { } frequencyOverride)
                        continue;

                    int start = (bar * 16 + step) * framesPerRow;
                    for (int i = 0; i < sound.Length && start + i < frameCount; i++)
                    {
                        int frequency = sound[i].Frequency == 0 ? frequencyOverride : sound[i].Frequency;
                        var writes = frames[start + i];
                        writes.AddRange([(voice * 7, frequency & 0xFF), (voice * 7 + 1, frequency >> 8), (voice * 7 + 4, sound[i].Control)]);
                        if (i == 0)
                            writes.AddRange([(voice * 7 + 5, 0x00), (voice * 7 + 6, 0xA9)]);
                    }
                    if (start + sound.Length < frameCount)
                        frames[start + sound.Length].Add((voice * 7 + 4, sound[^1].Control & 0xFE));
                }
            }
        }

        var analysis = SidTranscriber.Analyze(new SidTrace([], [.. frames]));
        return SidTranscriber.Import(analysis, new SidImportOptions(framesPerRow, 0, analysis.FrameCount));
    }

    private static (int, int)[] Sound(params (int Control, string? Note)[] frames) =>
        [.. frames.Select(f => (f.Control, f.Note is null ? 0 : Frequency(f.Note)))];

    private static readonly (int, int)[] SnareSound = Sound((0x81, "B-7"), (0x41, "A#3"), (0x41, "G#3"), (0x81, "C-7"), (0x81, "C-7"));

    [Fact]
    public void A_snare_on_the_backbeat_is_found_and_comes_first()
    {
        var result = Tune(4,
            (1, row => row % 16 is 4 or 12 ? 1 : null, SnareSound),
            (0, row => row % 4 == 0 ? Frequency("C-2") : null, Sound((0x41, null), (0x41, null), (0x41, null))),
            (2, row => row % 8 == 2 ? Frequency("A-4") : null, Sound((0x21, null), (0x21, null), (0x21, null), (0x21, null))));

        Assert.Equal("Snare", result.Song.Instruments[0].Name);
        Assert.Contains(result.Song.Instruments, i => i.Name == "Bass");
        Assert.Contains(result.Song.Instruments, i => i.Name == "Lead");
        Assert.Contains("1 snare", result.Report.Roles);
    }

    [Fact]
    public void A_bass_with_a_click_is_one_instrument_whatever_its_notes()
    {
        // A frame of pulse at B-7, then the bass note - BellaCiao's bass. The click and the note had
        // a frame each, and every bass note became an instrument of its own.
        string[] notes = ["E-1", "G-1", "A-1", "B-1", "D-2", "E-2", "G-2", "A-2"];
        var result = Tune(4, (0, row => row % 2 == 0 ? Frequency(notes[row / 2 % notes.Length]) : null,
            Sound((0x09, "B-7"), (0x41, "B-7"), (0x41, null))));

        var bass = Assert.Single(result.Song.Instruments);
        Assert.Equal("Bass", bass.Name);
        Assert.Equal(ArpeggioEnding.Root, bass.ArpeggioEnding);
        Assert.Equal(notes.Length, result.Song.Patterns.SelectMany(p => p.Rows).OfType<NoteRow>().Select(r => r.Note).Distinct().Count());
    }

    [Fact]
    public void A_kick_on_fixed_notes_keeps_one_row_note_when_a_note_is_cut_short()
    {
        // We-Come-in-Peace's kick: noise at C-6, then fixed notes falling to A#1 - the same on
        // every note. Cut after six frames by the next kick, a note's longest step was E-3 or
        // G#2 instead, and it was written on that.
        string[] kick = ["C-6", "E-3", "E-3", "G#2", "G#2", "A#1", "A#1", "A#1", "A#1", "A#1", "A#1", "A#1", "A#1", "A#1"];
        var frames = Enumerable.Range(0, 8 * 24).Select(_ => new List<(int, int)>()).ToList();
        for (int bar = 0; bar < 8; bar++)
        {
            // One kick of five frames and a frame of hard restart, then one of all fourteen.
            foreach (var (start, length) in new[] { (bar * 24, 5), (bar * 24 + 6, kick.Length) })
            {
                for (int i = 0; i < length; i++)
                {
                    int f = Frequency(kick[i]);
                    frames[start + i].AddRange([(0, f & 0xFF), (1, f >> 8), (4, i == 0 ? 0x81 : 0x41)]);
                    if (i == 0)
                        frames[start].AddRange([(5, 0x00), (6, 0xA9)]);
                }

                frames[start + length].Add((4, 0x40));
            }
        }

        var analysis = SidTranscriber.Analyze(new SidTrace([], [.. frames]));
        var result = SidTranscriber.Import(analysis, new SidImportOptions(6, 0, analysis.FrameCount));

        var notes = result.Song.Patterns.SelectMany(p => p.Rows).OfType<NoteRow>().Select(r => r.Note.ToString()).Distinct();
        Assert.Equal(["A#1"], notes);
        Assert.Single(result.Song.Instruments);
    }

    [Fact]
    public void A_noise_strike_falling_to_a_low_root_is_a_kick_bass()
    {
        var result = Tune(4, (0, row => row % 4 == 0 ? Frequency(row % 8 == 0 ? "C-2" : "G-1") : null,
            Sound((0x81, "B-7"), (0x41, "C-4"), (0x41, "C-3"), (0x41, null), (0x41, null), (0x41, null))));

        Assert.Equal("KickBass", Assert.Single(result.Song.Instruments).Name);
    }

    // ------------------------------------------------------------------- GoatTracker tunes

    /// <summary>A note as GoatTracker plays it: its table is at A-4 = 440 Hz, a quarter of a
    /// semitone and a bit above MagSID's - Viva Las Vegas measures +0.28.</summary>
    private static int GoatTracker(string note) => Math.Min(0xFFFF, (int)Math.Round(Frequency(note) * Math.Pow(2, 0.28 / 12)));

    private static (int, int)[] GoatSound(params (int Control, string? Note)[] frames) =>
        [.. frames.Select(f => (f.Control, f.Note is null ? 0 : GoatTracker(f.Note)))];

    [Fact]
    public void A_GoatTracker_kick_keeps_its_strike_and_plays_its_note()
    {
        // Viva Las Vegas's Basskick: the test bit, noise at B-7, pulse at C-3 and E-2 - fixed
        // notes - then the row's note. Out of MagSID's tuning no frame was on a note but the
        // strike, so every kick came out as a B-7 with no note program, and was called a snare.
        string[] roots = ["D#2", "C#2", "B-1", "A#1"];
        var result = Tune(4, (0, row => row % 4 == 0 ? GoatTracker(roots[row / 4 % roots.Length]) : null,
            GoatSound((0x09, null), (0x81, "B-7"), (0x41, "C-3"), (0x41, "E-2"), (0x41, null), (0x41, null), (0x41, null), (0x41, null))));

        var kick = Assert.Single(result.Song.Instruments);
        Assert.Equal("KickBass", kick.Name);
        Assert.Equal(ArpeggioEnding.Root, kick.ArpeggioEnding);
        Assert.Equal(["B-7", "C-3", "E-2"], kick.Arpeggio.Select(n => n.ToString()).Distinct());
        Assert.Equal(roots, result.Song.Patterns.SelectMany(p => p.Rows).OfType<NoteRow>().Take(4).Select(r => r.Note.ToString()));
    }

    [Fact]
    public void A_GoatTracker_snare_plays_its_own_notes_on_into_the_release()
    {
        // Viva Las Vegas's Snare: noise at F#6, pulse at F#3, then with the gate off pulse at C-3
        // and noise at D#7 in turn for as long as it rings. Read only while the gate was on, and
        // out of tune, it played its waveforms on the last note's pitch.
        var turns = Enumerable.Range(0, 29).Select(i => i % 2 == 0 ? (0x40, "C-3") : (0x80, "D#7"));
        var snare = GoatSound([(0x09, null), (0x81, "F#6"), (0x41, "F#3"), .. turns]);
        // Not in the last bar: the song's end would cut the last one's ringing short. A bass on
        // another voice holds its notes long enough for the tune's tuning to be read.
        var result = Tune(4,
            (0, row => row % 16 is 4 or 12 && row < 48 ? 1 : null, snare),
            (1, row => row % 4 == 0 ? GoatTracker("C-2") : null, GoatSound([.. Enumerable.Repeat<(int, string?)>((0x41, null), 12)])));

        var instrument = Assert.Single(result.Song.Instruments, i => i.Name.StartsWith("Snare"));
        Assert.Equal("Snare", instrument.Name);
        Assert.Equal(ArpeggioEnding.Loop, instrument.ArpeggioEnding);
        Assert.Equal(["F#6", "F#3", "C-3", "D#7"], instrument.Arpeggio.Select(n => n.ToString()).Distinct());
        Assert.Equal(["C-3", "D#7"], instrument.Arpeggio.Skip(instrument.ArpeggioLoopPoint).Select(n => n.ToString()));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(12)]
    public void A_noise_strike_ringing_on_pulse_on_the_backbeat_is_a_snare(int barRows)
    {
        // Most GoatTracker snares never go back to noise: noise at C-7, then pulse at G-3 - which,
        // with nothing else to go by, was a lead. Half a bar in, every bar, it is the snare: rows
        // 4 and 12 of sixteen, or row 6 of twelve in a 12/8 tune, as in MSI-FrogBog.
        // The song starts on its first note, the bass on the one.
        Func<int, int?> snare = barRows == 16 ? row => row % 16 is 4 or 12 ? 1 : null : row => row % 12 == 6 ? 1 : null;
        int beat = barRows == 16 ? 8 : 12;
        var result = Tune(4,
            (0, snare, Sound((0x81, "C-7"), (0x41, "G-3"), (0x41, "G-3"), (0x41, "G-3"), (0x41, "G-3"))),
            (1, row => row % beat == 0 ? Frequency("C-2") : null, Sound((0x41, null), (0x41, null), (0x41, null))));

        Assert.Single(result.Song.Instruments, i => i.Name == "Snare");
    }

    /// <summary>Notes on every row of a grid whose rows are <paramref name="a"/> and
    /// <paramref name="b"/> frames in turn - a different pitch each, some a frame late, as
    /// GoatTracker plays them.</summary>
    private static SidTranscriber.Analysis Swinging(int a, int b, int rows)
    {
        var frames = Enumerable.Range(0, rows / 2 * (a + b) + a).Select(_ => new List<(int, int)>()).ToList();
        string[] tune = ["C-3", "E-3", "G-3", "C-4", "A-3", "F-3"];
        int frame = 0;
        for (int row = 0; row < rows; row++)
        {
            int at = frame + (row % 3 == 2 ? 1 : 0);
            int f = Frequency(tune[row % tune.Length]);
            frames[at].AddRange([(0, f & 0xFF), (1, f >> 8), (5, 0x00), (6, 0xF0), (4, 0x41)]);
            frames[at + 3].Add((4, 0x40));
            frame += row % 2 == 0 ? a : b;
        }

        return SidTranscriber.Analyze(new SidTrace([], [.. frames]));
    }

    [Fact]
    public void A_tune_that_swings_is_imported_with_funk_speed()
    {
        // Rows of 8 and 5 frames, as Drax50yrsBday: one row length fits none of it but a frame.
        var analysis = Swinging(8, 5, 96);
        Assert.Equal((8, (int?)5), SidTranscriber.SuggestGrid(analysis));

        var options = new SidImportOptions(8, 0, analysis.FrameCount, FunkFramesPerRow: 5);
        var result = SidTranscriber.Import(analysis, options);
        Assert.Equal(7, result.Song.Speed);
        Assert.Equal(4, result.Song.FunkSpeed);
        Assert.Equal((96, 0, 0), (result.Report.NotesPlaced, result.Report.NotesDropped, result.Report.NotesMoved));

        var replayed = SidTranscriber.Analyze(SidImportTrace.Replay(result.Song, analysis.FrameCount));
        Assert.True(SidTranscriber.Agreement(analysis, options, replayed) >= 0.95);
    }

    [Fact]
    public void A_straight_tune_keeps_one_speed()
    {
        // Through a frame's tolerance a straight 6 once passed for 11/7.
        var analysis = Swinging(6, 6, 96);
        Assert.Equal((6, (int?)null), SidTranscriber.SuggestGrid(analysis));
    }

    [Fact]
    public void The_first_note_is_on_the_first_row()
    {
        // Viva Las Vegas's first kick comes seven frames in; the grid's phase put it on row 1.
        var frames = Enumerable.Range(0, 7 + 6 * 32).Select(_ => new List<(int, int)>()).ToList();
        for (int row = 0; row < 32; row += 2)
        {
            int f = 7 + row * 6;
            frames[f].AddRange([(0, 0x00), (1, 0x10), (5, 0x00), (6, 0xF0), (4, 0x41)]);
            frames[f + 4].Add((4, 0x40));
        }

        var analysis = SidTranscriber.Analyze(new SidTrace([], [.. frames]));
        var result = SidTranscriber.Import(analysis, new SidImportOptions(6, 0, analysis.FrameCount));

        var first = result.Song.Patterns.First(p => p.Number == ((PatternRefToken)result.Song.ChannelOrders[0].Tokens[0]).PatternNumber);
        Assert.IsType<NoteRow>(first.Rows[0]);
    }

    [Fact]
    public void A_volume_fade_does_not_make_an_instrument_of_every_step()
    {
        // Viva Las Vegas fades in with the master volume, and every filtered sound came out five
        // times over, once for each volume its notes set up the filter with.
        var frames = new List<IReadOnlyList<(int, int)>>();
        for (int note = 0; note < 16; note++)
        {
            int volume = Math.Min(15, 4 + note);
            for (int i = 0; i < 12; i++)
            {
                frames.Add(i switch
                {
                    0 => [(0, 0x00), (1, 0x10), (5, 0x00), (6, 0xF0), (4, 0x41), (0x16, 0x40), (0x17, 0xF1), (0x18, 0x10 | volume)],
                    1 => [(0x16, 0x30)],
                    10 => [(4, 0x40)],
                    _ => [],
                });
            }
        }

        var analysis = SidTranscriber.Analyze(new SidTrace([], frames));
        var result = SidTranscriber.Import(analysis, new SidImportOptions(12, 0, analysis.FrameCount));

        var instrument = Assert.Single(result.Song.Instruments);
        Assert.Equal((byte?)0x1F, instrument.Filter[0].ModeVolume);
    }

    [Fact]
    public void Past_sixteen_sounds_the_snare_and_the_bass_keep_their_places()
    {
        // Twenty lead sounds - saw for 1, 2, 3... frames, then pulse - each played four times, and a
        // snare and a bass played less than any of them: by use alone both would have been dropped.
        var parts = new List<(int, Func<int, int?>, (int, int)[])>
        {
            (1, row => row % 256 == 12 ? 1 : null, SnareSound),
            (0, row => row % 512 == 0 ? Frequency("C-2") : null, Sound((0x41, null), (0x41, null), (0x41, null))),
        };
        for (int lead = 0; lead < 20; lead++)
        {
            int which = lead;
            parts.Add((2, row => row % 128 == which * 6 ? Frequency("A-4") : null,
                [.. Enumerable.Repeat((0x21, 0), which + 1), (0x41, 0), (0x41, 0)]));
        }

        var result = Tune(32, [.. parts]);

        Assert.Equal(16, result.Song.Instruments.Count);
        Assert.Contains(result.Song.Instruments, i => i.Name == "Snare");
        Assert.Contains(result.Song.Instruments, i => i.Name == "Bass");
    }
}
