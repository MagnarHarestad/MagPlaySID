using MagSid.Core.Model;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// One instrument, as knobs and shapes rather than hex fields.
///
/// Every value here is the same byte the retro editor types, and the hex stays printed under each
/// knob - the point is not to hide the format but to stop it being the only way in. Turning a knob
/// writes straight onto the Instrument and raises <see cref="Edited"/>; there is no Save button,
/// because a knob you have to confirm is not a knob.
///
/// What is deliberately NOT here: a cutoff or resonance knob at instrument level. Those live inside
/// the filter program's setup records, one per record, so a knob up here would be a dial that does
/// nothing - the same reason the piano roll shows instrument rather than velocity.
/// </summary>
internal sealed class DawInstrumentView : Panel
{
    private readonly Label _title = new();

    /// <summary>The name and the oscillator and vibrato row: where the DAW puts the instrument
    /// strip on this page, so the program lanes and the filter editor below run the full width
    /// under it - as the piano roll does under the strip on ARRANGE.</summary>
    private readonly Panel _upper = new() { Dock = DockStyle.Top, BackColor = DawPalette.Background };

    /// <summary>See <see cref="_upper"/>.</summary>
    public Control StripHost => _upper;

    /// <summary>Takes the title's place while the name is being typed. Edits the name only - the
    /// number in front of it is the instrument's slot, not part of what it is called.</summary>
    private readonly TextBox _nameEditor = new()
    {
        Visible = false,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = DawPalette.Inset,
        Font = new Font("Segoe UI", 13f),
    };

    private readonly WaveformSelector _waveform = new();
    private readonly EnvelopeView _envelope = new();

    private readonly KnobControl _attack = new();
    private readonly KnobControl _decay = new();
    private readonly KnobControl _sustain = new();
    private readonly KnobControl _release = new();
    private readonly KnobControl _pulseWidth = new();
    private readonly KnobControl _pulseSweep = new();
    private readonly KnobControl _vibratoDelay = new();
    private readonly KnobControl _vibratoSpeed = new();
    private readonly KnobControl _vibratoDepth = new();

    private readonly ToggleButton _continuous = new("CONT");
    private readonly ToggleButton _noHardRestart = new("NO HR");
    private readonly ToggleButton _softHardRestart = new("SOFT HR");

    // What happens after the note program's last step. The lane draws the answer as a marker, but
    // drawing it was all it could do - this is where it gets set.
    private readonly ToggleButton _endingLoop = new("LOOP");
    private readonly ToggleButton _endingHold = new("HOLD");
    private readonly ToggleButton _endingRoot = new("ROOT");

    /// <summary>REC beside the endings: while it is lit, the chord held on the MIDI keyboard is
    /// written as the note program.</summary>
    private readonly ToggleButton _noteRecord = new("REC") { IsRecord = true };
    private readonly HashSet<NoteName> _chordHeld = [];
    private readonly KnobControl _arpLoopFrom = new();

    /// <summary>Opened over a note program or filter program step on a double-click, for typing
    /// the value rather than walking to it with the wheel.</summary>
    private readonly TextBox _stepEntry = new()
    {
        Visible = false,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = DawPalette.Inset,
        ForeColor = DawPalette.Text,
        Font = new Font("Consolas", 10f, FontStyle.Bold),
        TextAlign = HorizontalAlignment.Center,
        CharacterCasing = CharacterCasing.Upper,
        MaxLength = 3,
    };

    /// <summary>The lane the entry box is over, and what turns its text into an edit - null for
    /// text that is not a value, which keeps the box open.</summary>
    private ProgramLaneControl? _entryLane;
    private Func<string, Action<Instrument>?>? _entryParse;

    private readonly ProgramLaneControl _wavetable = new();
    private readonly ProgramLaneControl _noteProgram = new();
    private readonly ProgramLaneControl _filter = new();
    private readonly FilterStepEditor _filterStep = new();

    private Instrument? _instrument;

    /// <summary>True while an instrument is being loaded into the controls, so the change events
    /// that loading inevitably fires do not write the values straight back and mark the song dirty
    /// for having been looked at.</summary>
    private bool _loading;

    public DawInstrumentView()
    {
        BackColor = DawPalette.Background;
        Padding = new Padding(14);
        DoubleBuffered = true;

        BuildLayout();
        WireKnobs();
    }

    /// <summary>Every knob in this view, for the host to offer to MIDI. Gathered by walking the
    /// controls rather than listed, so a knob added later is included without anyone remembering
    /// to add it here too.</summary>
    public IEnumerable<KnobControl> Knobs() => AllKnobs(this);

    private static IEnumerable<KnobControl> AllKnobs(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is KnobControl knob)
                yield return knob;

            foreach (var nested in AllKnobs(child))
                yield return nested;
        }
    }

    /// <summary>Raised after any edit, with the instrument that changed. The host marks the song
    /// dirty and refreshes anything showing this instrument.</summary>
    public event Action<Instrument>? Edited;

    public void SetEnvelopePlayhead((int Stage, int Level)? at) => _envelope.SetPlayhead(at);

    public void SetCutoffPlayhead((int Frame, int Cutoff)? at) => _filterStep.SetPlayhead(at);

    /// <summary>The note program's REC is lit, on an instrument, on a page that is showing.</summary>
    public bool IsRecordingChord => _noteRecord.Checked && _instrument is not null && Visible;

    /// <summary>
    /// A key went down while REC is lit: the keys held now, low to high, become the note program,
    /// looping from its first step - an arpeggio of the chord. Letting go changes nothing, so the
    /// chord stays once the hand is off the keys; the next key pressed starts from what is held
    /// then. The notes are absolute, as every step of a note program is.
    /// </summary>
    public void RecordChordNote(NoteName note)
    {
        if (!IsRecordingChord)
            return;

        _chordHeld.Add(note);
        var chord = _chordHeld.OrderBy(n => n.TableIndex).ToList();

        Commit(instrument =>
        {
            instrument.Arpeggio.Clear();
            instrument.Arpeggio.AddRange(chord);
            instrument.ArpeggioEnding = ArpeggioEnding.Loop;
            instrument.ArpeggioLoopPoint = 0;
        });
        ShowEnding(_instrument);
    }

    public void RecordChordRelease(NoteName note) => _chordHeld.Remove(note);

    /// <summary>The chip the oscillator's waveform is drawn from - the one playback uses.</summary>
    public MagSid.Sid.ChipModel ChipModel
    {
        get => _waveform.ChipModel;
        set => _waveform.ChipModel = value;
    }

    public void LoadInstrument(Instrument? instrument)
    {
        // Another instrument's step 02 is not this one's: a selection carried across would point
        // the waveform tabs at a step nobody picked.
        bool changed = !ReferenceEquals(instrument, _instrument);
        if (changed)
        {
            _wavetable.ClearSelection();
            _noteRecord.Checked = false;
            _chordHeld.Clear();
        }

        _instrument = instrument;
        _loading = true;

        try
        {
            if (instrument is null)
            {
                _title.Text = "No instrument";
                _wavetable.SetSteps([]);
                _noteProgram.SetSteps([]);
                _filter.SetSteps([]);
                ShowEnding(null);
                ShowWaveformTarget(null);
                return;
            }

            var accent = DawPalette.ForInstrument(instrument.Number);
            _title.Text = TitleOf(instrument);
            _title.ForeColor = accent;

            _waveform.Accent = accent;

            _attack.SetValueSilently(PackedNibbles.High(instrument.AttackDecay));
            _decay.SetValueSilently(PackedNibbles.Low(instrument.AttackDecay));
            _sustain.SetValueSilently(PackedNibbles.High(instrument.SustainRelease));
            _release.SetValueSilently(PackedNibbles.Low(instrument.SustainRelease));

            _pulseWidth.SetValueSilently(instrument.PulseWidth);
            _pulseSweep.SetValueSilently(instrument.PulseSweep);

            _vibratoDelay.SetValueSilently(PackedNibbles.High(instrument.VibratoDelaySpeed));
            _vibratoSpeed.SetValueSilently(PackedNibbles.Low(instrument.VibratoDelaySpeed));
            _vibratoDepth.SetValueSilently(instrument.VibratoDepth);

            _continuous.Checked = instrument.PulseContinuous;
            _noHardRestart.Checked = instrument.NoHardRestart;
            _softHardRestart.Checked = instrument.SoftHardRestart;
            ShowEnding(instrument);

            RefreshDerived(instrument);

            // A newly shown instrument opens on its first filter step, so the step editor and the
            // cutoff graph below have something in them from the start.
            if (changed)
                _filter.SelectStep(instrument.Filter.Count > 0 ? 0 : -1);
            _filterStep.ShowStep(instrument, _filter.SelectedStep);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Which step of each program playback has reached, or -1. Feeds the same idea as the
    /// retro view's tint: a program step nothing ever reaches is a program step worth deleting.</summary>
    public void SetPlayheads(int wavetableStep, int arpeggioStep, int filterStep)
    {
        _wavetable.PlayingStep = wavetableStep;
        _noteProgram.PlayingStep = arpeggioStep;
        _filter.PlayingStep = filterStep;
    }

    private void Commit(Action<Instrument> edit)
    {
        if (_loading || _instrument is not { } instrument)
            return;

        edit(instrument);
        RefreshDerived(instrument);

        // Not while the editor itself is the one committing, or showing the step would reload the
        // controls out from under the knob being turned.
        if (!_filterStep.ContainsFocus)
            _filterStep.ShowStep(instrument, _filter.SelectedStep);

        Edited?.Invoke(instrument);
    }

    /// <summary>Everything that is drawn from the values rather than edited directly - the envelope
    /// curve and the three program lanes.</summary>
    private void RefreshDerived(Instrument instrument)
    {
        _envelope.SetEnvelope(
            PackedNibbles.High(instrument.AttackDecay), PackedNibbles.Low(instrument.AttackDecay),
            PackedNibbles.High(instrument.SustainRelease), PackedNibbles.Low(instrument.SustainRelease));

        _wavetable.SetSteps(ProgramSteps.Wavetable(instrument));
        _noteProgram.SetSteps(ProgramSteps.NoteProgram(instrument));
        _filter.SetSteps(ProgramSteps.Filter(instrument));
        ShowWaveformTarget(instrument);
    }

    private void BeginNoteEntry(int step, Rectangle cell)
    {
        // The cell after the last note is the LOOP / HOLD / ROOT ending, not a note.
        if (_instrument is not { } instrument || step >= instrument.Arpeggio.Count)
            return;

        var was = instrument.Arpeggio[step];
        BeginStepEntry(_noteProgram, cell, was.ToString(), text =>
            ParseTypedNote(text) is { } note
                ? i => { if (step < i.Arpeggio.Count) i.Arpeggio[step] = note; }
                : null);
    }

    private void BeginCutoffEntry(int step, Rectangle cell)
    {
        if (_instrument is not { } instrument || step >= instrument.Filter.Count)
            return;

        BeginStepEntry(_filter, cell, $"${instrument.Filter[step].Cutoff:X2}", text =>
            ParseTypedHex(text, FilterStep.MinCutoff, FilterStep.MaxCutoff) is { } cutoff
                ? i => { if (step < i.Filter.Count) i.Filter[step] = i.Filter[step].WithCutoff(cutoff); }
                : null);
    }

    /// <summary>
    /// The whole control register byte, typed: unlike the wheel, which only walks the waveform
    /// bits, this is the way to set gate, sync, ring and test exactly. $00 ends a wavetable, so it
    /// is not a step.
    /// </summary>
    private void BeginWaveEntry(int step, Rectangle cell)
    {
        if (_instrument is not { } instrument || step >= Wavetable(instrument).Count)
            return;

        BeginStepEntry(_wavetable, cell, $"${Wavetable(instrument)[step]:X2}", text =>
            ParseTypedHex(text, 0x01, 0xFF) is { } value
                ? i => SetWaveStep(i, step, value)
                : null);
    }

    private static void SetWaveStep(Instrument instrument, int step, byte value)
    {
        // The implied one-step program is the instrument's waveform plus gate; a gated byte typed
        // there is still that, so it stays the waveform rather than growing a WAVE section.
        if (instrument.Wavetable.Count == 0 && (value & 0x01) != 0)
        {
            instrument.Waveform = (byte)((value & 0xFE) | (instrument.Waveform & 0x01));
            return;
        }

        var program = Wavetable(instrument);
        if (step >= program.Count)
            return;

        program[step] = value;
        Apply(instrument, program);
    }

    private void BeginStepEntry(ProgramLaneControl lane, Rectangle cell, string text,
                                Func<string, Action<Instrument>?> parse)
    {
        if (_stepEntry.Parent != lane)
        {
            _stepEntry.Parent?.Controls.Remove(_stepEntry);
            lane.Controls.Add(_stepEntry);
        }

        _entryLane = lane;
        _entryParse = parse;
        _stepEntry.Text = text;
        _stepEntry.ForeColor = DawPalette.Text;
        _stepEntry.Bounds = new Rectangle(cell.X + 2, cell.Y + (cell.Height / 2) - 12, cell.Width - 4, 24);
        _stepEntry.Visible = true;
        _stepEntry.BringToFront();
        _stepEntry.Focus();
        _stepEntry.SelectAll();
    }

    private void EndStepEntry(bool keep)
    {
        // Hiding a focused box moves the focus, and LostFocus comes back in here before the box
        // counts as hidden - the entry is let go of first, so that second call finds nothing open.
        if (!_stepEntry.Visible || _entryParse is not { } parse || _entryLane is not { } lane)
            return;

        var edit = keep ? parse(_stepEntry.Text) : null;
        if (keep && edit is null && _stepEntry.Focused)
        {
            // Not a value: say so and stay open, rather than throwing the typing away or keeping
            // something that is not one. Escape is always the way out.
            System.Media.SystemSounds.Beep.Play();
            _stepEntry.ForeColor = Color.FromArgb(0xFF, 0x66, 0x66);
            _stepEntry.SelectAll();
            return;
        }

        _entryParse = null;
        _entryLane = null;
        _stepEntry.Visible = false;
        lane.Focus();

        if (edit is not null)
            Commit(edit);
    }

    /// <summary>A byte as someone would type it: hex, with or without the $ - "40", "$4A". Null
    /// outside the range, which is where each program keeps its end and setup markers.</summary>
    private static byte? ParseTypedHex(string text, byte minimum, byte maximum)
    {
        string trimmed = text.Trim().TrimStart('$');
        return trimmed.Length is >= 1 and <= 2
               && byte.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out byte value)
               && value >= minimum && value <= maximum
            ? value
            : null;
    }

    /// <summary>A note as someone would type it: "D#4", "c-5", or "C4" for C-4. Flats are not
    /// notes in this format, and neither is anything outside C-0..B-7.</summary>
    private static NoteName? ParseTypedNote(string text)
    {
        string trimmed = text.Trim().ToUpperInvariant();
        if (trimmed.Length == 2)
            trimmed = $"{trimmed[0]}-{trimmed[1]}";

        return NoteName.TryParse(trimmed, out var note) ? note : null;
    }

    /// <summary>The wavetable step the waveform tabs are editing, or -1 for the instrument's own
    /// waveform.</summary>
    private int WaveformStep(Instrument instrument) =>
        _wavetable.SelectedStep >= 0 && _wavetable.SelectedStep < Wavetable(instrument).Count
            ? _wavetable.SelectedStep
            : -1;

    /// <summary>Shows in the tabs whatever they will edit: the selected wavetable step's byte, or
    /// the instrument's waveform when no step is selected.</summary>
    private void ShowWaveformTarget(Instrument? instrument)
    {
        if (instrument is null)
            return;

        int step = WaveformStep(instrument);
        _waveform.Waveform = step >= 0 ? Wavetable(instrument)[step] : instrument.Waveform;
        _waveform.PulseWidth = instrument.PulseWidth;
    }

    /// <summary>
    /// A waveform tab clicked with a wavetable step selected changes that step: the step takes the
    /// new waveform, and sync and ring with it, but keeps its own gate and test bits - a step that
    /// gates the note off or resets the oscillator does that on purpose, and picking a waveform
    /// is not a reason to undo it. With no step selected, the instrument's own waveform changes.
    ///
    /// Silence is the exception both ways: it is the whole byte, $09, and a step that was silent
    /// takes the chosen waveform without the test bit that was part of its silence.
    /// </summary>
    private void ApplyWaveform(Instrument instrument, byte chosen)
    {
        int step = WaveformStep(instrument);

        // The implied one-step program is the instrument's waveform under another name; changing
        // it there keeps the instrument free of a WAVE section it does not need.
        if (step < 0 || instrument.Wavetable.Count == 0)
        {
            instrument.Waveform = chosen;
            return;
        }

        var program = Wavetable(instrument);
        bool silence = (chosen & 0xF0) == 0;
        bool wasSilent = (program[step] & 0xF0) == 0;
        program[step] = silence ? WaveformSelector.Silence
            : wasSilent ? (byte)(chosen | 0x01)
            : (byte)((program[step] & 0x09) | (chosen & 0xF6));
        Apply(instrument, program);
    }

    private void WireKnobs()
    {
        // Through PackedNibbles rather than inline shifting: a knob for one half must never disturb
        // the other, and that arithmetic is tested rather than eyeballed.
        _attack.ValueChanged += v => Commit(i => i.AttackDecay = PackedNibbles.WithHigh(i.AttackDecay, v));
        _decay.ValueChanged += v => Commit(i => i.AttackDecay = PackedNibbles.WithLow(i.AttackDecay, v));
        _sustain.ValueChanged += v => Commit(i => i.SustainRelease = PackedNibbles.WithHigh(i.SustainRelease, v));
        _release.ValueChanged += v => Commit(i => i.SustainRelease = PackedNibbles.WithLow(i.SustainRelease, v));

        _pulseWidth.ValueChanged += v => Commit(i => i.PulseWidth = v);
        _pulseSweep.ValueChanged += v => Commit(i => i.PulseSweep = v);

        _vibratoDelay.ValueChanged += v => Commit(i => i.VibratoDelaySpeed = PackedNibbles.WithHigh(i.VibratoDelaySpeed, v));
        _vibratoSpeed.ValueChanged += v => Commit(i => i.VibratoDelaySpeed = PackedNibbles.WithLow(i.VibratoDelaySpeed, v));
        _vibratoDepth.ValueChanged += v => Commit(i => i.VibratoDepth = (byte)v);

        _continuous.CheckedChanged += on => Commit(i => i.PulseContinuous = on);
        _noHardRestart.CheckedChanged += on => Commit(i => i.NoHardRestart = on);
        _softHardRestart.CheckedChanged += on => Commit(i => i.SoftHardRestart = on);

        _waveform.WaveformChanged += w => Commit(i => ApplyWaveform(i, w));
        _wavetable.StepSelected += _ => ShowWaveformTarget(_instrument);

        // Typing a step's value: Enter keeps it, Escape throws it away, and clicking elsewhere
        // keeps it if it is a value at all. "C4" is taken as "C-4" - the dash is how the file
        // writes a natural, not something anyone should have to type.
        _wavetable.StepDoubleClicked += BeginWaveEntry;
        _noteProgram.StepDoubleClicked += BeginNoteEntry;
        _filter.StepDoubleClicked += BeginCutoffEntry;
        _stepEntry.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Escape))
                return;

            EndStepEntry(keep: e.KeyCode == Keys.Enter);
            e.Handled = e.SuppressKeyPress = true;
        };
        _stepEntry.LostFocus += (_, _) => EndStepEntry(keep: true);

        WireProgram(
            _wavetable,
            nudge: (i, step, by) =>
            {
                var program = Wavetable(i);
                if (step < program.Count)
                {
                    // Up the waveform bits, not the whole byte: the low nibble is the gate and the
                    // test bit, and walking through those one value at a time would be a tour of
                    // states nobody wants.
                    int waveform = Math.Clamp((program[step] >> 4) + by, 0, 15);
                    program[step] = (byte)((waveform << 4) | (program[step] & 0x0F));
                    Apply(i, program);
                }
            },
            append: i =>
            {
                var program = Wavetable(i);
                program.Add(program.Count > 0 ? program[^1] : (byte)(i.Waveform | 0x01));
                Apply(i, program);
            },
            duplicate: (i, step) =>
            {
                var program = Wavetable(i);
                if (step < program.Count)
                {
                    program.Insert(step + 1, program[step]);
                    Apply(i, program);
                }
            },
            remove: (i, step) =>
            {
                var program = Wavetable(i);
                if (step < program.Count)
                {
                    program.RemoveAt(step);
                    Apply(i, program);
                }
            });

        WireProgram(
            _noteProgram,
            nudge: (i, step, by) =>
            {
                if (step < i.Arpeggio.Count)
                    i.Arpeggio[step] = Transpose(i.Arpeggio[step], by);
            },
            append: i => i.Arpeggio.Add(i.Arpeggio.Count > 0 ? i.Arpeggio[^1] : NoteName.Parse("C-4")),
            duplicate: (i, step) =>
            {
                // The ending cell after the last note is not a note: Insert there adds one.
                if (step >= i.Arpeggio.Count)
                {
                    i.Arpeggio.Add(i.Arpeggio.Count > 0 ? i.Arpeggio[^1] : NoteName.Parse("C-4"));
                    return;
                }

                i.Arpeggio.Insert(step + 1, i.Arpeggio[step]);

                // The loop goes back to the same note it did, which is one step further along now
                // when the copy went in before it.
                if (i.ArpeggioLoopPoint > step)
                    i.ArpeggioLoopPoint++;
            },
            remove: (i, step) =>
            {
                if (step < i.Arpeggio.Count)
                    i.Arpeggio.RemoveAt(step);
            });

        WireProgram(
            _filter,
            nudge: (i, step, by) =>
            {
                if (step < i.Filter.Count)
                    i.Filter[step] = WithCutoff(i.Filter[step], by);
            },
            append: i => i.Filter.Add(i.Filter.Count > 0
                ? FilterStep.Sweep(i.Filter[^1].Cutoff)
                : FilterStep.Sweep(0x40)),
            duplicate: (i, step) =>
            {
                if (step < i.Filter.Count)
                    i.Filter.Insert(step + 1, i.Filter[step]);
            },
            remove: (i, step) =>
            {
                if (step < i.Filter.Count)
                    i.Filter.RemoveAt(step);
            });
    }

    /// <summary>The wavetable as an editable list, standing in the implied one-step program when
    /// the instrument has no WAVE section - editing it is what turns the implied program into a
    /// real one.</summary>
    private static List<byte> Wavetable(Instrument instrument) =>
        instrument.Wavetable.Count > 0
            ? [.. instrument.Wavetable]
            : [(byte)(instrument.Waveform | 0x01)];

    private static void Apply(Instrument instrument, List<byte> program)
    {
        instrument.Wavetable.Clear();
        instrument.Wavetable.AddRange(program);
    }

    /// <summary>Moves a note by semitones, staying inside the frequency table's C-0..B-7.</summary>
    private static NoteName Transpose(NoteName note, int semitones)
    {
        int index = Math.Clamp(note.TableIndex + semitones, 0, 95);
        return NoteName.AllNotes[index];
    }

    /// <summary>Nudges a filter step's cutoff, kept inside $01..$FE.</summary>
    private static FilterStep WithCutoff(FilterStep step, int by) => step.WithCutoff(step.Cutoff + by);

    private void WireProgram(
        ProgramLaneControl lane,
        Action<Instrument, int, int> nudge,
        Action<Instrument> append,
        Action<Instrument, int> remove,
        Action<Instrument, int> duplicate)
    {
        lane.StepNudged += (step, by) => Commit(i => nudge(i, step, by));
        lane.StepAppended += () => Commit(append);
        lane.StepDuplicated += step => Commit(i => duplicate(i, step));
        lane.StepRemoved += step => Commit(i => remove(i, step));
    }

    private void BuildLayout()
    {
        _title.Font = new Font("Segoe UI", 13f);
        _title.ForeColor = DawPalette.Oscillator;
        _title.AutoSize = true;
        _title.Location = new Point(16, 12);

        // The name is edited where it is read. A click on it opens it, Enter keeps what was typed,
        // Escape throws it away, and clicking elsewhere keeps it - the same rules as renaming in
        // the instrument strip, since it is the same edit.
        _title.Cursor = Cursors.IBeam;
        _title.Click += (_, _) => BeginRename();
        _nameEditor.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Escape))
                return;

            EndRename(keep: e.KeyCode == Keys.Enter);
            e.Handled = e.SuppressKeyPress = true;
        };
        _nameEditor.LostFocus += (_, _) => EndRename(keep: true);

        // The name alone: the wave, A/D and S/R bytes a line under it read the same as the
        // controls right below.
        var header = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = DawPalette.Background };
        header.Controls.Add(_title);
        header.Controls.Add(_nameEditor);

        // --- oscillator -------------------------------------------------------------------
        var oscillator = Section("OSCILLATOR", DawPalette.Oscillator);
        oscillator.Width = 620;
        oscillator.Dock = DockStyle.Left;

        _waveform.Dock = DockStyle.Top;
        _waveform.Height = 76;
        _waveform.Margin = new Padding(0, 0, 0, 8);

        // Taller by the row the CONT and NO HR switches used to have under it - they sit beside
        // the knobs now, and the curve is the thing on this panel that most wants the height.
        _envelope.Dock = DockStyle.Bottom;
        _envelope.Height = 108;

        var knobRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DawPalette.Panel,
            Padding = new Padding(4, 8, 4, 8),
            WrapContents = false,
        };

        Knob(_attack, "Attack", DawPalette.Oscillator, 0, 15, Hex1, bindingKey: "att");
        Knob(_decay, "Decay", DawPalette.Oscillator, 0, 15, Hex1, bindingKey: "dec");
        Knob(_sustain, "Sustain", DawPalette.Oscillator, 0, 15, Hex1, bindingKey: "sus");
        Knob(_release, "Release", DawPalette.Oscillator, 0, 15, Hex1, bindingKey: "rel");
        Knob(_pulseWidth, "Pulse", DawPalette.Envelope, 0, 15, v => v.ToString(), Instrument.DefaultPulseWidth, bindingKey: "pw");
        Knob(_pulseSweep, "PulseSweep", DawPalette.Envelope, -128, 127, v => v.ToString("+#;-#;0"), bindingKey: "sweep");
        foreach (var knob in new[] { _attack, _decay, _sustain, _release, _pulseWidth, _pulseSweep })
            knob.Width = 64;    // room for the spelled-out names, PulseSweep the widest

        // CONT, NO HR and SOFT HR are switches about the pulse and the envelope, so they stand
        // beside the knobs for those rather than on a row of their own - two to a column, which
        // is as many as the knob row is tall enough for.
        _continuous.Accent = DawPalette.Envelope;
        _noHardRestart.Accent = DawPalette.Envelope;
        _softHardRestart.Accent = DawPalette.Envelope;
        var flags = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = true,
            BackColor = DawPalette.Panel,
            Size = new Size(166, 66),
            Margin = new Padding(10, 4, 0, 0),
        };
        _continuous.Margin = new Padding(0, 0, 0, 6);
        _noHardRestart.Margin = Padding.Empty;
        _softHardRestart.Margin = new Padding(6, 0, 0, 0);
        flags.Controls.AddRange([_continuous, _noHardRestart, _softHardRestart]);

        knobRow.Controls.AddRange([_attack, _decay, _sustain, _release, _pulseWidth, _pulseSweep, flags]);

        oscillator.Body.Controls.Add(knobRow);
        oscillator.Body.Controls.Add(_envelope);
        oscillator.Body.Controls.Add(_waveform);

        // --- effects ----------------------------------------------------------------------
        var effects = Section("VIBRATO", DawPalette.Effect);
        effects.Dock = DockStyle.Fill;

        var effectKnobs = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DawPalette.Panel,
            Padding = new Padding(8, 8, 4, 8),
            WrapContents = false,
        };
        Knob(_vibratoDelay, "Delay", DawPalette.Effect, 0, 15, Hex1);
        Knob(_vibratoSpeed, "Speed", DawPalette.Effect, 0, 15, Hex1);
        Knob(_vibratoDepth, "Depth", DawPalette.Effect, 0, 255, Hex2);
        effectKnobs.Controls.AddRange([_vibratoDelay, _vibratoSpeed, _vibratoDepth]);

        var effectNote = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            BackColor = DawPalette.Panel,
            ForeColor = DawPalette.TextFaint,
            Font = new Font("Segoe UI", 7.5f),
            Padding = new Padding(8, 2, 8, 6),
            Text = "Depth 0 turns vibrato off, and a song with no vibrato leaves it out of the "
                   + "assembled player entirely. Slides are written in the pattern: 1xx up, 2xx down.",
        };

        effects.Body.Controls.Add(effectKnobs);
        effects.Body.Controls.Add(effectNote);

        var top = new Panel { Dock = DockStyle.Top, Height = 300, BackColor = DawPalette.Background };
        top.Controls.Add(effects);
        top.Controls.Add(oscillator);

        // --- the three programs -----------------------------------------------------------
        _wavetable.Title = "WAVETABLE";
        _wavetable.Hint = "One per frame - last holds unless loop activated.";
        _wavetable.Accent = DawPalette.Envelope;

        _noteProgram.Title = "NOTE PROGRAM";
        _noteProgram.Hint = "Absolute notes. Mouseclick to write note or use Mousewheel to change. Right-click to delete.";
        _noteProgram.Accent = DawPalette.Oscillator;

        // The lane picks a step; the editor below shows everything that step carries. Mode,
        // volume, resonance and routing live in a setup record and nowhere else, which is why the
        // lane alone could never edit them.
        _filter.StepSelected += step => _filterStep.ShowStep(_instrument, step);
        _filterStep.Edited += () => Commit(_ => { });

        // A step grabbed in the cutoff graph is picked in the lane too, and its drag lands as one
        // edit - one undo step - when the button comes up.
        _filterStep.StepPicked += step =>
        {
            _filter.SelectStep(step);
            _filterStep.ShowStep(_instrument, step);
        };
        _filterStep.CutoffDragged += (step, cutoff) =>
        {
            Commit(i => { if (step < i.Filter.Count) i.Filter[step] = i.Filter[step].WithCutoff(cutoff); });
            _filterStep.ShowStep(_instrument, step);
        };
        _filterStep.Dock = DockStyle.Bottom;

        _filter.Title = "FILTER PROGRAM";
        _filter.Hint = "Click on entry to edit filter value or Filter setups";
        _filter.Accent = DawPalette.Filter;

        var programs = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = DawPalette.Background,
            Padding = new Padding(0, 10, 0, 0),
        };
        for (int i = 0; i < 3; i++)
            programs.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3f));

        foreach (var (lane, row) in new[] { (_wavetable, 0), (_noteProgram, 1), (_filter, 2) })
        {
            lane.Dock = DockStyle.Fill;
            lane.Margin = new Padding(0, row == 0 ? 0 : 5, 0, 0);

            if (lane != _noteProgram)
            {
                programs.Controls.Add(lane, 0, row);
                continue;
            }

            // The ending belongs beside the steps it follows, not in a panel somewhere else: it is
            // the last thing the program does, and the lane already draws it as the step after the
            // last one.
            var withEnding = new Panel { Dock = DockStyle.Fill, Margin = lane.Margin, BackColor = DawPalette.Background };
            withEnding.Controls.Add(lane);
            withEnding.Controls.Add(BuildEndingStrip());
            programs.Controls.Add(withEnding, 0, row);
        }

        _upper.Height = header.Height + top.Height;
        _upper.Controls.Add(top);
        _upper.Controls.Add(header);

        Controls.Add(programs);
        Controls.Add(_filterStep);
        Controls.Add(_upper);
    }

    /// <summary>LOOP, HOLD and ROOT are one choice, so they latch as a group - and the loop's own
    /// start step only means anything for LOOP, which is the only time it is shown.</summary>
    private Control BuildEndingStrip()
    {
        var strip = new Panel { Dock = DockStyle.Right, Width = 162, BackColor = DawPalette.Background };

        var caption = new Label
        {
            Text = "AFTER THE LAST STEP",
            Font = new Font("Segoe UI", 6.5f, FontStyle.Bold),
            ForeColor = DawPalette.TextFaint,
            AutoSize = false,
            Size = new Size(104, 24),
            Location = new Point(2, 4),
        };
        strip.Controls.Add(caption);

        var endings = new[]
        {
            (Button: _endingLoop, Ending: ArpeggioEnding.Loop),
            (Button: _endingHold, Ending: ArpeggioEnding.Hold),
            (Button: _endingRoot, Ending: ArpeggioEnding.Root),
        };

        for (int i = 0; i < endings.Length; i++)
        {
            var (button, ending) = endings[i];
            button.Accent = DawPalette.Oscillator;
            button.Size = new Size(50, 22);
            button.Location = new Point(2 + (i % 2 * 54), 30 + (i / 2 * 26));
            button.CheckedChanged += on =>
            {
                if (!on)
                {
                    return;
                }

                Commit(instrument => instrument.ArpeggioEnding = ending);
                ShowEnding(_instrument);
            };
            strip.Controls.Add(button);
        }

        // Right of ROOT, as the piano roll's REC sits beside the pattern's name.
        _noteRecord.Size = new Size(50, 22);
        _noteRecord.Location = new Point(56, 56);
        _noteRecord.CheckedChanged += _ => _chordHeld.Clear();
        strip.Controls.Add(_noteRecord);

        Knob(_arpLoopFrom, "From", DawPalette.Oscillator, 0, 63, value => value.ToString());
        _arpLoopFrom.Size = new Size(50, 62);
        _arpLoopFrom.Location = new Point(110, 30);
        _arpLoopFrom.ValueChanged += value => Commit(instrument => instrument.ArpeggioLoopPoint = value);
        strip.Controls.Add(_arpLoopFrom);

        return strip;
    }

    /// <summary>Silently, because this also runs as the answer to a click on one of these very
    /// buttons - firing their events again from here would be a loop.</summary>
    private void ShowEnding(Instrument? instrument)
    {
        bool loading = _loading;
        _loading = true;

        try
        {
            var ending = instrument?.ArpeggioEnding ?? ArpeggioEnding.Root;
            _endingLoop.Checked = ending == ArpeggioEnding.Loop;
            _endingHold.Checked = ending == ArpeggioEnding.Hold;
            _endingRoot.Checked = ending == ArpeggioEnding.Root;

            _arpLoopFrom.Visible = ending == ArpeggioEnding.Loop;
            _arpLoopFrom.SetValueSilently(instrument?.ArpeggioLoopPoint ?? 0);
        }
        finally
        {
            _loading = loading;
        }
    }

    private static string TitleOf(Instrument instrument) =>
        $"{instrument.Number:00}  {InstrumentNames.Display(instrument)}";

    // The instrument the open editor belongs to, caught when it opened - the view can be handed
    // another instrument while it is open, and the typed name belongs to the one it was opened on.
    private Instrument? _renaming;

    private void BeginRename()
    {
        if (_instrument is not { } instrument || _nameEditor.Visible)
        {
            return;
        }

        _renaming = instrument;
        _nameEditor.Text = instrument.Name;
        _nameEditor.ForeColor = _title.ForeColor;
        _nameEditor.Bounds = new Rectangle(
            _title.Left, _title.Top - 2, Math.Max(320, _title.Width + 60), _nameEditor.PreferredHeight);

        _title.Visible = false;
        _nameEditor.Visible = true;
        _nameEditor.BringToFront();
        _nameEditor.Focus();
        _nameEditor.SelectAll();
    }

    /// <summary>Commits verbatim, the same as the strip and the retro grid: a name that happens to
    /// match a placeholder is still a name someone chose.</summary>
    private void EndRename(bool keep)
    {
        if (!_nameEditor.Visible)
        {
            return;
        }

        var instrument = _renaming;
        _renaming = null;
        _nameEditor.Visible = false;
        _title.Visible = true;

        if (!keep || instrument is null)
        {
            return;
        }

        string name = _nameEditor.Text.Trim();
        if (name == instrument.Name)
        {
            return;
        }

        if (instrument == _instrument)
        {
            Commit(i => i.Name = name);
            _title.Text = TitleOf(instrument);
            return;
        }

        // Opened on one instrument and closed after the view moved to another: still that first
        // instrument's name, and still an edit the host has to hear about.
        instrument.Name = name;
        Edited?.Invoke(instrument);
    }

    private static string Hex1(int value) => $"${value:X}";

    private static string Hex2(int value) => $"${value:X2}";

    private static void Knob(
        KnobControl knob, string caption, Color accent, int minimum, int maximum,
        Func<int, string> format, int defaultValue = 0, string? bindingKey = null)
    {
        knob.Caption = caption;

        // Derived from the caption unless told otherwise, so a knob cannot be added with a binding
        // key someone forgot to give it. Prefixed by panel, since CUTOFF means one thing on an
        // instrument and another on a filter step. A knob whose caption has since been spelled out
        // keeps the key it was saved under, or every controller bound to it would quietly let go.
        knob.BindingKey = "instrument." + (bindingKey ?? caption.ToLowerInvariant());
        knob.Accent = accent;
        knob.Minimum = minimum;
        knob.Maximum = maximum;
        knob.Format = format;
        knob.DefaultValue = defaultValue;
        knob.Margin = new Padding(2, 0, 2, 0);
    }

    private static SectionPanel Section(string title, Color accent) => new(title, accent);
}
