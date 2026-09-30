namespace MagSid.Core.Model;

/// <summary>
/// The arithmetic between a MIDI keyboard and this editor: which note a key is, and what a
/// controller's 0..127 means for a knob that runs some other range.
///
/// Here rather than in the MIDI plumbing because both halves decide values that end up in a song,
/// and neither can be checked by playing a keyboard at the screen.
/// </summary>
public static class MidiMapping
{
    /// <summary>MIDI note 12 is C0 in the scientific pitch naming this project uses, so the
    /// player's own 0..95 table index is the note number less twelve. Middle C, MIDI 60, lands on
    /// C-4 - which is what a keyboard's own labelling promises and therefore the only mapping that
    /// would not feel broken.</summary>
    private const int LowestNoteNumber = 12;

    /// <summary>Null for a key outside C-0..B-7, which the player's frequency table cannot reach at
    /// all - silently folding those into the nearest octave would make a five-octave keyboard lie
    /// about what it just played.</summary>
    public static NoteName? NoteFor(int noteNumber)
    {
        int index = noteNumber - LowestNoteNumber;
        return index >= 0 && index < NoteName.AllNotes.Count ? NoteName.AllNotes[index] : null;
    }

    /// <summary>The note number a key would have to send to play this note.</summary>
    public static int NoteNumberOf(NoteName note) => note.TableIndex + LowestNoteNumber;

    /// <summary>
    /// A controller's 0..127 across a knob's own range.
    ///
    /// Rounded rather than truncated, so the middle of a knob's travel sits where the middle of the
    /// controller's does: with truncation a 0..15 knob would spend eight steps on 0..7 and only
    /// seven on 8..15, and the top value would need the controller pinned at 127 exactly.
    /// </summary>
    public static int ScaleController(int controllerValue, int minimum, int maximum)
    {
        if (maximum <= minimum)
            return minimum;

        int clamped = Math.Clamp(controllerValue, 0, 127);
        return minimum + (int)Math.Round(clamped / 127.0 * (maximum - minimum));
    }

    /// <summary>Where a knob's value sits on a controller's scale - for sending a value back to a
    /// surface with motorised or lit rings, and for the round trip to be checkable at all.</summary>
    public static int ControllerFor(int value, int minimum, int maximum)
    {
        if (maximum <= minimum)
            return 0;

        int clamped = Math.Clamp(value, minimum, maximum);
        return (int)Math.Round((clamped - minimum) / (double)(maximum - minimum) * 127.0);
    }
}
