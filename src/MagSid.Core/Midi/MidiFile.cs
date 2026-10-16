namespace MagSid.Core.Midi;

/// <summary>One note as the file plays it, in the file's own ticks.</summary>
public sealed record MidiNote(int Channel, long Start, long End, int Key, int Velocity);

/// <summary>One track of a standard MIDI file. A format 0 file is a single track holding every
/// channel, so the notes carry their channel rather than the track being assumed to be one.</summary>
public sealed record MidiTrack(int Index, string Name, IReadOnlyList<MidiNote> Notes,
                               IReadOnlyDictionary<int, int> Programs);

/// <summary>
/// What the import needs from a standard MIDI file: the notes, and the first tempo and time
/// signature. Tempo and signature changes after the first are only counted - MagSID plays a song
/// at one speed.
/// </summary>
public sealed record MidiSong(
    int TicksPerQuarter,
    int MicrosecondsPerQuarter,
    int BeatsPerBar,
    int BeatUnit,
    int TempoChanges,
    IReadOnlyList<MidiTrack> Tracks)
{
    public double Bpm => 60_000_000.0 / MicrosecondsPerQuarter;

    /// <summary>Every track and channel pair that has notes - what a voice can be fed from.</summary>
    public IReadOnlyList<MidiSource> Sources() =>
        Tracks.SelectMany(track => track.Notes
                .GroupBy(note => note.Channel)
                .OrderBy(group => group.Key)
                .Select(group => MidiSource.From(track, group.Key, group.ToList())))
            .ToList();

    /// <summary>
    /// Where a source stands in <see cref="Sources"/>, or -1 for none. By track and channel, not by
    /// equality: each call to Sources builds new note lists, so a source from one call never equals
    /// its twin from another - and a list found nothing, leaving every suggested voice silent.
    /// </summary>
    public int IndexOf(MidiSource? source) =>
        source is null ? -1 : Sources().ToList().FindIndex(s => s.Track == source.Track && s.Channel == source.Channel);
}

/// <summary>One channel of one track, summarised so it can be recognised in a list.</summary>
public sealed record MidiSource(
    int Track,
    int Channel,
    string Name,
    int? Program,
    int NoteCount,
    int LowestKey,
    int HighestKey,
    double AverageKey,
    int MostAtOnce,
    IReadOnlyList<MidiNote> Notes)
{
    /// <summary>General MIDI puts percussion on channel 10.</summary>
    public bool IsDrums => Channel == 9;

    /// <summary>Channel 10, or a track named for its drums - plenty of files, transcriptions
    /// especially, keep them on another channel.</summary>
    public bool LooksLikeDrums => IsDrums
        || new[] { "drum", "perc", "kit" }.Any(word => Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    internal static MidiSource From(MidiTrack track, int channel, IReadOnlyList<MidiNote> notes)
    {
        // The most notes sounding together: +1 at each start, -1 at each end, ends first on a tie.
        int most = 0, now = 0;
        foreach (var (_, delta) in notes.SelectMany(n => new[] { (n.Start, 1), (n.End, -1) })
                     .OrderBy(e => e.Item1).ThenBy(e => e.Item2))
        {
            now += delta;
            most = Math.Max(most, now);
        }

        return new MidiSource(track.Index, channel, track.Name,
            track.Programs.TryGetValue(channel, out int program) ? program : null,
            notes.Count, notes.Min(n => n.Key), notes.Max(n => n.Key), notes.Average(n => n.Key),
            most, notes);
    }
}

public sealed class MidiFileException(string message) : Exception(message);

/// <summary>
/// Reads a standard MIDI file (format 0 or 1). Only what the import uses is kept: notes, program
/// changes, track names, tempo and time signature. Everything else - controllers, pitch bend,
/// SysEx - is read past.
/// </summary>
public static class MidiFileReader
{
    private const int DefaultTempo = 500_000;    // 120 BPM, what a file without a tempo plays at

    public static MidiSong Read(byte[] data)
    {
        var reader = new Reader(data);

        if (reader.Tag() != "MThd")
            throw new MidiFileException("Not a MIDI file - it does not start with MThd.");
        int headerLength = (int)reader.UInt32();
        int format = reader.UInt16();
        int trackCount = reader.UInt16();
        int division = reader.UInt16();
        reader.Skip(headerLength - 6);

        if (format > 1)
            throw new MidiFileException($"MIDI format {format} is not supported - only 0 and 1.");
        if ((division & 0x8000) != 0)
            throw new MidiFileException("This MIDI file counts time in SMPTE frames, not beats.");
        if (division == 0)
            throw new MidiFileException("The MIDI file says a beat has 0 ticks.");

        var tempos = new List<(long Tick, int Tempo)>();
        (int Beats, int Unit)? signature = null;
        var tracks = new List<MidiTrack>();

        for (int index = 0; index < trackCount && !reader.AtEnd; index++)
        {
            string tag = reader.Tag();
            int length = (int)reader.UInt32();
            if (tag != "MTrk")
            {
                reader.Skip(length);              // an unknown chunk is skipped, as the format asks
                index--;
                continue;
            }

            tracks.Add(ReadTrack(reader.Slice(length), tracks.Count, tempos, ref signature));
        }

        tempos.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        int tempo = tempos.Count > 0 ? tempos[0].Tempo : DefaultTempo;
        int changes = tempos.Select(t => t.Tempo).Distinct().Count() - 1;

        return new MidiSong(division, tempo, signature?.Beats ?? 4, signature?.Unit ?? 4,
            Math.Max(0, changes), tracks);
    }

    private static MidiTrack ReadTrack(Reader reader, int index, List<(long, int)> tempos,
                                       ref (int Beats, int Unit)? signature)
    {
        string name = "";
        var notes = new List<MidiNote>();
        var programs = new Dictionary<int, int>();

        // A note-on waits here for its note-off; the same key struck twice before it is released
        // ends in the order it was struck.
        var open = new Dictionary<(int Channel, int Key), Queue<(long Start, int Velocity)>>();

        long tick = 0;
        int status = 0;
        while (!reader.AtEnd)
        {
            tick += reader.VarLen();
            int first = reader.Byte();
            if (first >= 0x80)
            {
                status = first;
                if (first < 0xF0)
                    first = reader.Byte();
            }
            else if (status is 0 or >= 0xF0)
            {
                throw new MidiFileException("Running status with no status byte before it.");
            }

            int kind = status & 0xF0, channel = status & 0x0F;
            switch (kind)
            {
                case 0x80:
                case 0x90:
                {
                    int key = first & 0x7F, velocity = reader.Byte() & 0x7F;
                    if (kind == 0x90 && velocity > 0)
                    {
                        if (!open.TryGetValue((channel, key), out var queue))
                            open[(channel, key)] = queue = new Queue<(long, int)>();
                        queue.Enqueue((tick, velocity));
                    }
                    else if (open.TryGetValue((channel, key), out var queue) && queue.Count > 0)
                    {
                        var (start, struck) = queue.Dequeue();
                        notes.Add(new MidiNote(channel, start, tick, key, struck));
                    }
                    break;
                }

                case 0xA0:
                case 0xB0:
                case 0xE0:
                    reader.Byte();
                    break;

                case 0xC0:
                    programs.TryAdd(channel, first & 0x7F);
                    break;

                case 0xD0:
                    break;

                default:
                    if (status == 0xFF)
                    {
                        int type = reader.Byte();
                        byte[] body = reader.Bytes((int)reader.VarLen());
                        if (type == 0x03 && name.Length == 0)
                            name = System.Text.Encoding.Latin1.GetString(body).Trim();
                        else if (type == 0x51 && body.Length == 3)
                            tempos.Add((tick, body[0] << 16 | body[1] << 8 | body[2]));
                        else if (type == 0x58 && body.Length >= 2 && signature is null)
                            signature = (body[0], 1 << body[1]);
                        else if (type == 0x2F)
                            reader.Skip(reader.Remaining);
                    }
                    else if (status is 0xF0 or 0xF7)
                    {
                        reader.Skip((int)reader.VarLen());
                    }
                    else
                    {
                        throw new MidiFileException($"Unexpected status byte ${status:X2}.");
                    }

                    // A meta or SysEx event cancels running status.
                    status = 0;
                    break;
            }
        }

        // A note still held when the track ends stops there.
        foreach (var ((channel, key), queue) in open)
            foreach (var (start, velocity) in queue)
                notes.Add(new MidiNote(channel, start, Math.Max(tick, start + 1), key, velocity));

        notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Key.CompareTo(b.Key));
        return new MidiTrack(index, name, notes, programs);
    }

    /// <summary>Big-endian reads that fail with a MidiFileException instead of running off the end.</summary>
    private sealed class Reader(byte[] data, int position = 0, int end = -1)
    {
        private readonly int _end = end < 0 ? data.Length : end;
        private int _position = position;

        public bool AtEnd => _position >= _end;
        public int Remaining => _end - _position;

        public int Byte()
        {
            if (_position >= _end)
                throw new MidiFileException("The MIDI file ends in the middle of an event.");
            return data[_position++];
        }

        public byte[] Bytes(int count)
        {
            if (count < 0 || count > Remaining)
                throw new MidiFileException("The MIDI file ends in the middle of an event.");
            var bytes = data.AsSpan(_position, count).ToArray();
            _position += count;
            return bytes;
        }

        public void Skip(int count) => Bytes(count);

        public int UInt16() => Byte() << 8 | Byte();
        public uint UInt32() => (uint)(Byte() << 24 | Byte() << 16 | Byte() << 8 | Byte());
        public string Tag() => System.Text.Encoding.ASCII.GetString(Bytes(4));

        public long VarLen()
        {
            long value = 0;
            for (int i = 0; i < 4; i++)
            {
                int b = Byte();
                value = value << 7 | (uint)(b & 0x7F);
                if ((b & 0x80) == 0)
                    return value;
            }
            throw new MidiFileException("A length in the MIDI file runs past four bytes.");
        }

        public Reader Slice(int length)
        {
            if (length < 0 || length > Remaining)
                throw new MidiFileException("A track is longer than the file.");
            var slice = new Reader(data, _position, _position + length);
            _position += length;
            return slice;
        }
    }
}
