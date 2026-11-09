using System.Text;

namespace MagPlaySid.Export;

/// <summary>
/// A Standard MIDI File, type 1: a first track of the song's name, tempo and time, then a track per
/// voice. Events are added at ticks, in any order; each track is sorted as it is written.
/// </summary>
internal sealed class MidiFile(int ticksPerBeat)
{
    public int TicksPerBeat { get; } = ticksPerBeat;

    public List<MidiTrack> Tracks { get; } = [];

    public MidiTrack AddTrack(string name)
    {
        var track = new MidiTrack();
        track.Text(0, 0x03, name);
        Tracks.Add(track);
        return track;
    }

    public void Write(Stream stream)
    {
        var header = new List<byte>();
        header.AddRange("MThd"u8.ToArray());
        BigEndian(header, 6, 4);
        BigEndian(header, Tracks.Count == 1 ? 0 : 1, 2);
        BigEndian(header, Tracks.Count, 2);
        BigEndian(header, TicksPerBeat, 2);
        stream.Write(header.ToArray());
        foreach (var track in Tracks)
        {
            var body = track.Bytes();
            var chunk = new List<byte>();
            chunk.AddRange("MTrk"u8.ToArray());
            BigEndian(chunk, body.Length, 4);
            stream.Write(chunk.ToArray());
            stream.Write(body);
        }
    }

    internal static void BigEndian(List<byte> bytes, int value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
            bytes.Add((byte)(value >> (8 * i)));
    }
}

/// <summary>A track's events at their ticks.</summary>
internal sealed class MidiTrack
{
    /// <summary>An event: its tick, its order among events at the same tick - offs before ons,
    /// settings before notes - the order added, and its bytes.</summary>
    private readonly List<(long Tick, int Rank, int Added, byte[] Data)> _events = [];

    public int Count => _events.Count;

    private void Add(long tick, int rank, params byte[] data) => _events.Add((Math.Max(0, tick), rank, _events.Count, data));

    /// <summary>A meta text event: 0x01 text, 0x02 copyright, 0x03 the track's name, 0x06 a marker.</summary>
    public void Text(long tick, byte kind, string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        var data = new List<byte> { 0xFF, kind };
        data.AddRange(Quantity(utf8.Length));
        data.AddRange(utf8);
        Add(tick, 0, data.ToArray());
    }

    /// <summary>The tempo from <paramref name="tick"/> on, as microseconds a beat.</summary>
    public void Tempo(long tick, int microsecondsPerBeat)
    {
        int t = Math.Clamp(microsecondsPerBeat, 1, 0xFFFFFF);
        Add(tick, 1, 0xFF, 0x51, 0x03, (byte)(t >> 16), (byte)(t >> 8), (byte)t);
    }

    /// <summary>The time signature: <paramref name="beats"/> a bar of quarter notes.</summary>
    public void TimeSignature(long tick, int beats) => Add(tick, 1, 0xFF, 0x58, 0x04, (byte)beats, 2, 24, 8);

    public void NoteOff(long tick, int channel, int key) => Add(tick, 2, (byte)(0x80 | channel), (byte)key, 0);

    public void Controller(long tick, int channel, int controller, int value) =>
        Add(tick, 3, (byte)(0xB0 | channel), (byte)controller, (byte)Math.Clamp(value, 0, 127));

    /// <summary>The pitch bend, 0-16383 with 8192 none.</summary>
    public void PitchBend(long tick, int channel, int value)
    {
        int v = Math.Clamp(value, 0, 16383);
        Add(tick, 3, (byte)(0xE0 | channel), (byte)(v & 0x7F), (byte)(v >> 7));
    }

    /// <summary>The pitch bend's range, in semitones either way: registered parameter 0.</summary>
    public void BendRange(long tick, int channel, int semitones)
    {
        Controller(tick, channel, 101, 0);
        Controller(tick, channel, 100, 0);
        Controller(tick, channel, 6, semitones);
        Controller(tick, channel, 38, 0);
        Controller(tick, channel, 101, 127);
        Controller(tick, channel, 100, 127);
    }

    public void NoteOn(long tick, int channel, int key, int velocity) =>
        Add(tick, 4, (byte)(0x90 | channel), (byte)Math.Clamp(key, 0, 127), (byte)Math.Clamp(velocity, 1, 127));

    public byte[] Bytes()
    {
        var bytes = new List<byte>();
        long at = 0;
        foreach (var e in _events.OrderBy(e => e.Tick).ThenBy(e => e.Rank).ThenBy(e => e.Added))
        {
            bytes.AddRange(Quantity((int)Math.Min(e.Tick - at, 0x0FFFFFFF)));
            bytes.AddRange(e.Data);
            at = e.Tick;
        }
        bytes.AddRange(Quantity(0));
        bytes.AddRange(new byte[] { 0xFF, 0x2F, 0x00 });
        return bytes.ToArray();
    }

    /// <summary>A number as MIDI writes lengths and times: seven bits a byte, the top bit on in all
    /// but the last.</summary>
    private static byte[] Quantity(int value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7F) };
        for (value >>= 7; value > 0; value >>= 7)
            bytes.Insert(0, (byte)(0x80 | (value & 0x7F)));
        return bytes.ToArray();
    }
}
