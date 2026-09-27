using MagSid.Core;
using MagSid.Core.Model;

namespace MagSid.Assembler;

/// <summary>
/// The song's note dictionary: every distinct note it uses, numbered from 1.
///
/// Order is the whole point, and it is not free to choose. Startup\Tables.s registers notes in
/// first-occurrence order - all pattern rows first, walking patterns by ascending number and rows
/// top to bottom, and only then the arpeggio steps of instruments 1..N. Its own comment says why
/// the arpeggios come second: so whether an instrument happens to arpeggiate cannot change how the
/// patterns encode.
///
/// Get this order wrong and every note in the song still assembles, at the same size, playing the
/// wrong pitch. SongSizeCalculator only ever needed the count, which is why the order lives here
/// rather than there.
/// </summary>
public sealed class NoteDictionary
{
    private readonly Dictionary<NoteName, int> _ids = [];
    private readonly List<NoteName> _notes = [];

    private NoteDictionary()
    {
    }

    /// <summary>In id order, so index 0 is id 1.</summary>
    public IReadOnlyList<NoteName> Notes => _notes;

    public int Count => _notes.Count;

    /// <summary>1-based, matching the pattern stream where $01..$7F is a note id.</summary>
    public int IdOf(NoteName note) => _ids.TryGetValue(note, out int id)
        ? id
        : throw new ArgumentException($"{note} is not in the dictionary - it was built from a different song.", nameof(note));

    public static NoteDictionary Build(Song song)
    {
        var dictionary = new NoteDictionary();

        foreach (var pattern in song.Patterns.OrderBy(p => p.Number))
            foreach (var row in pattern.Rows)
                if (row is NoteRow note)
                    dictionary.Register(note.Note);

        foreach (var instrument in song.Instruments.OrderBy(i => i.Number))
            foreach (var step in instrument.Arpeggio)
                dictionary.Register(step);

        // The pattern stream keeps $80..$FF for commands, so an id has to stay under it.
        if (dictionary.Count > 127)
        {
            throw new SongTooLargeException(
                $"The song uses {dictionary.Count} distinct notes; the pattern stream can address " +
                "127, because $80..$FF are command bytes.");
        }

        return dictionary;
    }

    /// <summary>note_freq_low and note_freq_high, each with the dummy entry 0 that makes a 1-based
    /// id index them directly.</summary>
    public (byte[] Low, byte[] High) FrequencyTables()
    {
        var low = new byte[_notes.Count + 1];
        var high = new byte[_notes.Count + 1];

        for (int i = 0; i < _notes.Count; i++)
        {
            var (lo, hi) = NotePalTable.GetFreq(_notes[i]);
            low[i + 1] = lo;
            high[i + 1] = hi;
        }

        return (low, high);
    }

    private void Register(NoteName note)
    {
        if (_ids.ContainsKey(note))
            return;

        _notes.Add(note);
        _ids[note] = _notes.Count;
    }
}

/// <summary>Thrown when a song cannot be expressed in the player's format at all - a pool past 256
/// bytes, a pattern past 255, more notes than the stream can address. Every one of these is a limit
/// Startup\Tables.s asserts too, and hitting one is a real "this song will not fit", not a bug.</summary>
public sealed class SongTooLargeException(string message) : Exception(message);
