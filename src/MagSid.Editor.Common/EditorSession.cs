using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Core.Validation;

namespace MagSid.Editor.App;

/// <summary>
/// Owns the in-memory <see cref="Song"/> plus the structural edit operations (add/duplicate/
/// delete pattern or instrument) that must keep cross-references consistent - pattern and
/// instrument numbers stay contiguous by construction (renumbered on delete), and every
/// reference to a renumbered or deleted item is fixed up in the same operation, so the model
/// coming out of any UI action is always one SongValidator (and the real assembler) accepts.
///
/// Two separate events, deliberately: <see cref="StructureChanged"/> fires only when the set of
/// patterns/instruments (or the loaded file) changes shape - MainForm repopulates every control
/// from <see cref="Song"/> on it. <see cref="DirtyChanged"/> fires on every value-only edit
/// (a note, an order token, an instrument byte) and only updates the dirty flag/status bar -
/// it must NOT trigger a full repopulation, since those edits are reported by the very control
/// (e.g. TrackerGridControl) that would be re-populated, and doing so mid-event risks
/// re-entrant control mutation.
/// </summary>
public sealed class EditorSession
{
    public Song Song { get; private set; } = CreateNewSong();
    public string? FilePath { get; private set; }

    /// <summary>What the song is called before it has a file: the MIDI file it was imported from.
    /// Its file's name once it has one.</summary>
    public string? Name => FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : _importedName;
    private string? _importedName;
    public bool IsDirty { get; private set; }

    public event Action? StructureChanged;
    public event Action? DirtyChanged;

    /// <summary>Fires whenever undo/redo availability changes, so the menu can enable itself.</summary>
    public event Action? HistoryChanged;

    // Snapshots rather than per-action inverses - see SongHistory for why. The events that
    // drive capture fire after the model has already changed, which is exactly what SongHistory
    // is built around.
    private readonly SongHistory _history;

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    public EditorSession() => _history = new SongHistory(Song);

    public void Undo()
    {
        if (_history.Undo() is { } restored)
            Restore(restored);
    }

    public void Redo()
    {
        if (_history.Redo() is { } restored)
            Restore(restored);
    }

    private void Restore(Song restored)
    {
        Song = restored;
        IsDirty = true;
        StructureChanged?.Invoke();
        DirtyChanged?.Invoke();
        HistoryChanged?.Invoke();
    }

    public void New()
    {
        Song = CreateNewSong();
        FilePath = null;
        _importedName = null;
        IsDirty = false;
        _history.Reset(Song);
        HistoryChanged?.Invoke();
        StructureChanged?.Invoke();
    }

    public void Load(string path)
    {
        Song = PatternsFileReader.Read(path);
        FilePath = path;
        IsDirty = false;
        _history.Reset(Song);
        HistoryChanged?.Invoke();
        StructureChanged?.Invoke();
    }

    /// <summary>A song made in the editor rather than read from a file - an import. It has no
    /// path yet and is unsaved, so closing asks before it is lost.</summary>
    public void LoadImported(Song song, string? name = null)
    {
        Song = song;
        FilePath = null;
        _importedName = name;
        IsDirty = true;
        _history.Reset(Song);
        HistoryChanged?.Invoke();
        StructureChanged?.Invoke();
        DirtyChanged?.Invoke();
    }

    public void Save(string? path = null)
    {
        string target = path ?? FilePath ?? throw new InvalidOperationException("No file path set.");
        PatternsFileWriter.Write(Song, target);
        FilePath = target;
        IsDirty = false;
        DirtyChanged?.Invoke();
    }

    // Patterns are created straight on the Song by the tracker grid (type one past the last
    // number - see TrackerGridControl.EnterPatternNumberDigit) and are never deleted: a song
    // order holds references only, so dropping an entry leaves the pattern reachable by typing
    // its number again. Anything the song has stopped playing is pruned on export instead, by
    // SongPruning, which works on a copy so the open song keeps everything.

    /// <summary>Adds a new instrument, or returns null when the song already holds every
    /// instrument a pattern can name ($C0..$CF, so 16 - see SongValidator.MaxInstrumentNumber).
    /// Refusing here rather than letting the number climb keeps the song always exportable:
    /// a 17th instrument assembles perfectly well and simply never plays, so nothing would
    /// surface the problem until the assembler rejected it at .sid export.</summary>
    public Instrument? AddInstrument()
    {
        if (NextInstrumentNumber() is not { } next)
            return null;

        var instrument = Instrument.CreateDefault(next);
        Song.Instruments.Add(instrument);
        RaiseStructureChanged();
        return instrument;
    }

    /// <summary>Gives <paramref name="target"/> the sound of <paramref name="sound"/> - every
    /// setting and program, and its name - keeping its number, so the notes on it play the new
    /// sound. The sound bank's "change sound": a new instrument starts as a plain Flute, and this
    /// is how it becomes a bank sound without adding a second one and deleting the first.</summary>
    public void ReplaceInstrumentSound(Instrument target, Instrument sound)
    {
        target.CopySoundFrom(MagSid.Core.Serialization.InstrumentFile.Read(MagSid.Core.Serialization.InstrumentFile.Write(sound)));
        RaiseStructureChanged();
    }

    /// <summary>Adds a new instrument that starts as an exact copy of <paramref name="source"/> -
    /// every field and program (Wavetable/Arpeggio/Filter cloned, not shared - editing the copy
    /// must never reach back into the original) - at the next available number, or returns null
    /// under the same "song already has all 16" condition <see cref="AddInstrument"/> refuses
    /// under. Name copies verbatim rather than getting a "(copy)" suffix or similar: renaming is
    /// one click in the Instruments list's own already-inline-editable Name column, so there is
    /// nothing this needs to guess at on the caller's behalf.</summary>
    public Instrument? CopyInstrument(Instrument source)
    {
        if (NextInstrumentNumber() is not { } next)
            return null;

        var copy = new Instrument
        {
            Number = next,
            Name = source.Name,
            Waveform = source.Waveform,
            AttackDecay = source.AttackDecay,
            SustainRelease = source.SustainRelease,
            ArpeggioEnding = source.ArpeggioEnding,
            ArpeggioLoopPoint = source.ArpeggioLoopPoint,
            PulseWidth = source.PulseWidth,
            PulseSweep = source.PulseSweep,
            PulseContinuous = source.PulseContinuous,
            NoHardRestart = source.NoHardRestart,
            SoftHardRestart = source.SoftHardRestart,
            VibratoDelaySpeed = source.VibratoDelaySpeed,
            VibratoDepth = source.VibratoDepth,
        };
        // FilterStep is an immutable readonly record struct and Wavetable/Arpeggio hold plain
        // byte/NoteName values, so AddRange here copies content, never aliases the source's own
        // lists - the copy is safe to edit independently from the moment it exists.
        copy.Wavetable.AddRange(source.Wavetable);
        copy.Arpeggio.AddRange(source.Arpeggio);
        copy.Filter.AddRange(source.Filter);

        Song.Instruments.Add(copy);
        RaiseStructureChanged();
        return copy;
    }

    /// <summary>Adds an instrument read from a file at the next free number, or returns null when
    /// the song already has all <see cref="SongValidator.MaxInstrumentNumber"/>.</summary>
    public Instrument? ImportInstrument(Instrument loaded)
    {
        if (NextInstrumentNumber() is not { } next)
            return null;

        loaded.Number = next;
        Song.Instruments.Add(loaded);
        RaiseStructureChanged();
        return loaded;
    }

    /// <summary>The number a freshly added or copied instrument gets, or null when the song
    /// already holds all <see cref="SongValidator.MaxInstrumentNumber"/> a pattern can select.
    /// Shared by <see cref="AddInstrument"/> and <see cref="CopyInstrument"/> so the two can
    /// never drift on what "room for one more" means.</summary>
    private int? NextInstrumentNumber()
    {
        int next = Song.Instruments.Count == 0 ? 1 : Song.Instruments.Max(i => i.Number) + 1;
        return next <= SongValidator.MaxInstrumentNumber ? next : null;
    }

    /// <summary>Moves <paramref name="instrument"/> to 1-based position <paramref name="toNumber"/>
    /// among the song's other instruments, shifting everything between its old and new position
    /// by one and renumbering every pattern's NoteRow references to follow - the drag-to-reorder
    /// gesture in the Instruments list, one row-crossing at a time (see
    /// InstrumentListControl.MoveRequested). <paramref name="toNumber"/> is clamped into
    /// 1..Count rather than rejected out of range, so dragging into the list's own empty-slot
    /// rows below the last real instrument just means "move to the end" instead of a no-op -
    /// there is no meaningful difference between those empty rows anyway, since instrument
    /// numbers are always contiguous. A no-op (including a same-position "move") never raises
    /// StructureChanged, so a drag that crosses back over its own starting row doesn't spam
    /// pointless rebuilds.</summary>
    public void MoveInstrument(Instrument instrument, int toNumber)
    {
        var ordered = Song.Instruments.OrderBy(i => i.Number).ToList();
        int fromIndex = ordered.IndexOf(instrument);
        if (fromIndex < 0)
            return;

        int toIndex = Math.Clamp(toNumber, 1, ordered.Count) - 1;
        if (toIndex == fromIndex)
            return;

        ordered.RemoveAt(fromIndex);
        ordered.Insert(toIndex, instrument);

        RenumberTo(ordered);
        RaiseStructureChanged();
    }

    public void DeleteInstrument(Instrument instrument)
    {
        Song.Instruments.Remove(instrument);

        // A note that used the deleted instrument has no safe instrument left to fall back to -
        // turn it into a note-off instead of silently leaving a dangling reference.
        foreach (var pattern in Song.Patterns)
        {
            for (int i = 0; i < pattern.Rows.Count; i++)
            {
                if (pattern.Rows[i] is NoteRow noteRow && noteRow.InstrumentNumber == instrument.Number)
                    pattern.Rows[i] = new NoteOffRow();
            }
        }

        RenumberTo(Song.Instruments.OrderBy(i => i.Number).ToList());
        RaiseStructureChanged();
    }

    /// <summary>Assigns 1..N numbers to <paramref name="ordered"/> in the sequence given, and
    /// rewrites every pattern's NoteRow references to follow along - shared by
    /// <see cref="DeleteInstrument"/>'s renumber-after-removal and <see cref="MoveInstrument"/>'s
    /// reorder, so the two can never drift on how a renumber actually propagates through pattern
    /// data.</summary>
    private void RenumberTo(List<Instrument> ordered)
    {
        var renumber = new Dictionary<int, int>();
        for (int i = 0; i < ordered.Count; i++)
        {
            renumber[ordered[i].Number] = i + 1;
            ordered[i].Number = i + 1;
        }

        foreach (var pattern in Song.Patterns)
        {
            for (int i = 0; i < pattern.Rows.Count; i++)
            {
                if (pattern.Rows[i] is NoteRow(var note, var oldInst) && renumber.TryGetValue(oldInst, out int newInst))
                    pattern.Rows[i] = new NoteRow(note, newInst);
            }
        }
    }

    /// <summary>Call after a value-only edit (a note, order token, or instrument byte) that
    /// doesn't change the set of patterns/instruments - does NOT repopulate controls.</summary>
    public void MarkDirty()
    {
        _history.Capture(Song);
        HistoryChanged?.Invoke();
        IsDirty = true;
        DirtyChanged?.Invoke();
    }

    /// <summary>Call after adding instruments/patterns straight onto <see cref="Song"/> from
    /// outside this class (e.g. a generator tool that built its own Instrument/Pattern objects
    /// rather than going through AddInstrument) - repopulates every control the same way
    /// AddInstrument's own structural change does.</summary>
    public void NotifyStructureChanged() => RaiseStructureChanged();

    private void RaiseStructureChanged()
    {
        // Not via MarkDirty: this raises DirtyChanged itself, and going through MarkDirty would
        // capture the same edit twice - once here and once there.
        _history.Capture(Song);
        HistoryChanged?.Invoke();
        IsDirty = true;
        StructureChanged?.Invoke();
        DirtyChanged?.Invoke();
    }

    private static Song CreateNewSong()
    {
        var song = new Song();

        // One instrument from the start. Without it the song saves but will not assemble:
        // Startup\Tables.s only emits ad_table/sr_table/wave_table when there is at least one
        // instrument, while the player references those labels unconditionally, so a build of
        // an instrument-less song dies on "Undefined variable or constant 'ad_table'". It also
        // means notes can be typed immediately instead of hitting "Add an instrument first".
        song.Instruments.Add(Instrument.CreateDefault(1));

        // A pattern of its own per channel - P01 on CH1, P02 on CH2, P03 on CH3 - so each part can
        // be written straight away without the three writing over one shared pattern. Every
        // channel needs a non-empty order for the song to save at all.
        for (int channel = 0; channel < Song.ChannelCount; channel++)
        {
            int number = channel + 1;
            song.Patterns.Add(Pattern.CreateEmpty(number));
            song.ChannelOrders[channel].Tokens.Add(new PatternRefToken(number));
        }

        return song;
    }
}
