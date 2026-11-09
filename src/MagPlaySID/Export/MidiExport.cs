using MagPlay.Amiga;
using MagPlay.Score;
using MagSid.Playback;

namespace MagPlaySid.Export;

/// <summary>
/// A song's notes as a MIDI file, a track and a channel for each voice, on the tracker's grid: a row
/// a sixteenth note, four a beat, so the bars line up in a sequencer. A .sid's are its transcription's
/// notes on their rows, as the sheet music has them; a module's are what its player plays - each note struck, its
/// volume as the note's velocity and then as expression, and its slides and vibrato as pitch bends.
/// </summary>
internal static class MidiExport
{
    /// <summary>MIDI's middle C, 60; the app's notes have it as 48.</summary>
    private const int Octave = 12;

    private const int Velocity = 100;

    /// <summary>Semitones either way a pitch bend reaches - enough for most slides.</summary>
    private const int BendRange = 12;

    public static MidiFile FromSid(SidFile file, int song, Score score, ScoreTimeline timeline, string product)
    {
        // Each note on its row, as the sheet music has it - a voice's player can run a call or two
        // apart from another's, which would put every note just off the grid.
        const int TicksPerRow = 24;
        double callsPerStep = score.FunkTempo is { } funk ? (score.Tempo + funk) / 2.0 : Math.Max(1, score.Tempo);
        long Tick(int call) => (long)Math.Round(call / callsPerStep) * TicksPerRow;

        var midi = new MidiFile(TicksPerRow * NoteSheet.StepsPerBeat);
        string title = string.IsNullOrWhiteSpace(file.Name) ? "Untitled" : file.Name.Trim();
        var conductor = midi.AddTrack(file.Songs > 1 ? $"{title} - song {song}" : title);
        if (!string.IsNullOrWhiteSpace(file.Author))
            conductor.Text(0, 0x02, $"{file.Released?.Trim()} {file.Author.Trim()}".Trim());
        conductor.Text(0, 0x01, $"Transcribed by {product} - rows of {(score.FunkTempo is { } f ? $"{score.Tempo}/{f}" : $"{score.Tempo}")} calls, a row a sixteenth note");
        conductor.TimeSignature(0, 4);
        conductor.Tempo(0, (int)Math.Round(NoteSheet.StepsPerBeat * callsPerStep / score.CallsPerSecond * 1e6));

        long end = Tick(timeline.Calls);
        for (int voice = 0; voice < timeline.Voices; voice++)
        {
            string name = timeline.Voices <= 3 ? $"Voice {voice + 1}" : $"SID{voice / 3 + 1} voice {voice % 3 + 1}";
            var track = midi.AddTrack(name);
            int channel = ChannelOf(voice);

            // The voice's notes, from row to row; of two on one row, the later, as it is heard.
            var notes = new List<(long Start, long End, int Key)>();
            int? key = null;
            long struck = 0;

            void Close(long at)
            {
                if (key is { } k && at > struck)
                    notes.Add((struck, at, k));
                key = null;
            }

            foreach (var played in timeline.Rows[voice])
            {
                if (score.PatternNumber(played.Pattern)?.Rows.ElementAtOrDefault(played.Row) is not { } row)
                    continue;
                long at = Math.Min(Tick(played.Call), end);
                if (row.Note.Kind == RowNoteKind.Note)
                {
                    Close(at);
                    key = Math.Clamp(row.Note.Note + played.Transpose + Octave, 0, 127);
                    struck = at;
                }
                else if (row.Note.Kind == RowNoteKind.GateOff)
                    Close(at);
            }
            Close(end);
            foreach (var (start, stop, k) in notes)
            {
                track.NoteOn(start, channel, k, Velocity);
                track.NoteOff(stop, channel, k);
            }
        }
        return midi;
    }

    /// <summary>A module's notes as its player plays them through <paramref name="length"/>.</summary>
    public static MidiFile FromModule(ITrackerModule module, string path, TimeSpan length, string product, CancellationToken cancel = default)
    {
        // States read four thousand times a second: a tick is 20 ms at most trackers' tempo.
        const int Rate = 4000, TicksPerRow = 24, Chunk = 4000;
        var midi = new MidiFile(TicksPerRow * NoteSheet.StepsPerBeat);
        var conductor = midi.AddTrack(SoundExport.FileName(module, path));
        conductor.Text(0, 0x01, $"Exported by {product} from the {module.Kind}, as {module.TrackerName} plays it - a row a sixteenth note");
        conductor.TimeSignature(0, 4);

        int channels = module.Channels;
        var voices = new ModuleVoice[channels];
        for (int c = 0; c < channels; c++)
        {
            voices[c] = new ModuleVoice(midi.AddTrack($"Channel {c + 1}"), ChannelOf(c));
            voices[c].Track.BendRange(0, voices[c].Channel, BendRange);
        }

        var player = module.CreatePlayer(Rate, 0);
        var left = new float[Chunk];
        var right = new float[Chunk];
        var states = new int[channels][];
        for (int c = 0; c < channels; c++)
            states[c] = new int[Chunk];
        var rows = new int[Chunk];

        // A row's events wait for its end, to be placed within it by how far into it they came.
        var pending = new List<(long Sample, Action<long> Write)>();
        long rowStart = 0, sample = 0, total = (long)(length.TotalSeconds * Rate);
        int rowIndex = -1, place = -1, tempo = 0;
        double midiMicroseconds = 0;

        void EndRow(long at)
        {
            if (rowIndex < 0)
                return;
            long duration = Math.Max(1, at - rowStart);
            long first = (long)rowIndex * TicksPerRow;
            foreach (var (s, write) in pending)
                write(first + Math.Min(TicksPerRow - 1, (s - rowStart) * TicksPerRow / duration));
            pending.Clear();
            // The tempo that ends the row where the sound does - changed only when it is out by more
            // than a hundredth, so a steady tempo stays one event and the time never drifts.
            double wanted = (at * 1e6 / Rate - midiMicroseconds) * NoteSheet.StepsPerBeat;
            if (tempo == 0 || Math.Abs(wanted - tempo) > tempo * 0.01)
            {
                tempo = (int)Math.Round(wanted);
                conductor.Tempo(first, tempo);
            }
            midiMicroseconds += (double)tempo / NoteSheet.StepsPerBeat;
        }

        while (sample < total && !player.Stopped)
        {
            cancel.ThrowIfCancellationRequested();
            int n = (int)Math.Min(Chunk, total - sample);
            player.Render(left.AsSpan(0, n), right.AsSpan(0, n), null, states, rows);
            for (int i = 0; i < n; i++, sample++)
            {
                int at = rows[i] & 0xFFFF;
                if (at != place)
                {
                    EndRow(sample);
                    place = at;
                    rowIndex++;
                    rowStart = sample;
                }
                for (int c = 0; c < channels; c++)
                    voices[c].Read(states[c][i], sample, pending);
            }
        }
        foreach (var voice in voices)
            voice.Stop(sample, pending);
        EndRow(sample);
        return midi;
    }

    /// <summary>A voice's MIDI channel: one each, around channel 10 - General MIDI's drums.</summary>
    private static int ChannelOf(int voice)
    {
        int c = voice % 15;
        return c < 9 ? c : c + 1;
    }

    /// <summary>A module channel's notes as they play: struck - or, starting silent, heard - bent,
    /// louder and softer, and stopped. A note is written once it ends: its velocity is the loudest it
    /// got, and its volume on the way is expression against that.</summary>
    private sealed class ModuleVoice(MidiTrack track, int channel)
    {
        public MidiTrack Track { get; } = track;
        public int Channel { get; } = channel;

        private sealed class Note(int key)
        {
            public int Key { get; } = key;
            public long OnTick { get; set; }
            public int Loudest { get; set; }
            public List<(long Tick, int Volume)> Volumes { get; } = [];
        }

        private Note? _note;
        private int _base, _bend = 8192, _expression = 127;
        private int _lastState = -1;
        private bool _wasStruck;

        public void Read(int state, long sample, List<(long Sample, Action<long> Write)> pending)
        {
            if (state == _lastState)
                return;
            var (instrument, pitch, volume, sounding) = TrackerModules.Unpack(state);
            bool struck = TrackerModules.Struck(state);
            var last = _lastState < 0 ? default : TrackerModules.Unpack(_lastState);
            bool strikes = struck && sounding && (!_wasStruck || instrument != last.Sample || Math.Round(pitch) != Math.Round(last.Note));
            _lastState = state;
            _wasStruck = struck;

            if (strikes || (_note is null && sounding))
            {
                Release(sample, pending);
                _base = (int)Math.Round(pitch);
                var note = new Note(Math.Clamp(_base + Octave, 0, 127)) { Loudest = volume };
                _note = note;
                Bend(pitch, sample, pending);
                pending.Add((sample, tick =>
                {
                    note.OnTick = tick;
                    note.Volumes.Add((tick, volume));
                }));
            }
            else if (_note is { } note)
            {
                if (!sounding)
                {
                    Release(sample, pending);
                    return;
                }
                Bend(pitch, sample, pending);
                note.Loudest = Math.Max(note.Loudest, volume);
                pending.Add((sample, tick => note.Volumes.Add((tick, volume))));
            }
        }

        public void Stop(long sample, List<(long Sample, Action<long> Write)> pending) => Release(sample, pending);

        private void Bend(double pitch, long sample, List<(long Sample, Action<long> Write)> pending)
        {
            int bend = Math.Clamp(8192 + (int)Math.Round((pitch - _base) / BendRange * 8192), 0, 16383);
            if (bend == _bend)
                return;
            _bend = bend;
            pending.Add((sample, tick => Track.PitchBend(tick, Channel, bend)));
        }

        private void Release(long sample, List<(long Sample, Action<long> Write)> pending)
        {
            if (_note is not { } note)
                return;
            _note = null;
            pending.Add((sample, tick =>
            {
                int loudest = Math.Max(1, note.Loudest);
                foreach (var (at, volume) in note.Volumes)
                {
                    int expression = Math.Clamp(volume * 127 / loudest, 0, 127);
                    if (expression != _expression)
                    {
                        _expression = expression;
                        Track.Controller(at, Channel, 11, expression);
                    }
                }
                Track.NoteOn(note.OnTick, Channel, note.Key, loudest * 127 / 64);
                // A note lasts a tick at least, or its end would sort before its start.
                Track.NoteOff(Math.Max(tick, note.OnTick + 1), Channel, note.Key);
            }));
        }
    }
}
