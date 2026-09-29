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
    private readonly Label _subtitle = new();

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
    private readonly KnobControl _slide = new();

    private readonly ToggleButton _continuous = new("CONT");
    private readonly ToggleButton _noHardRestart = new("NO HR");

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

    /// <summary>Raised after any edit, with the instrument that changed. The host marks the song
    /// dirty and refreshes anything showing this instrument.</summary>
    public event Action<Instrument>? Edited;

    public void LoadInstrument(Instrument? instrument)
    {
        _instrument = instrument;
        _loading = true;

        try
        {
            if (instrument is null)
            {
                _title.Text = "No instrument";
                _subtitle.Text = string.Empty;
                _wavetable.SetSteps([]);
                _noteProgram.SetSteps([]);
                _filter.SetSteps([]);
                return;
            }

            var accent = DawPalette.ForInstrument(instrument.Number);
            _title.Text = string.IsNullOrWhiteSpace(instrument.Name)
                ? $"Instrument {instrument.Number:00}"
                : $"{instrument.Number:00}  {instrument.Name}";
            _title.ForeColor = accent;
            _subtitle.Text = Describe(instrument);

            _waveform.Accent = accent;
            _waveform.Waveform = instrument.Waveform;

            _attack.SetValueSilently(PackedNibbles.High(instrument.AttackDecay));
            _decay.SetValueSilently(PackedNibbles.Low(instrument.AttackDecay));
            _sustain.SetValueSilently(PackedNibbles.High(instrument.SustainRelease));
            _release.SetValueSilently(PackedNibbles.Low(instrument.SustainRelease));

            _pulseWidth.SetValueSilently(instrument.PulseWidth);
            _pulseSweep.SetValueSilently(instrument.PulseSweep);

            _vibratoDelay.SetValueSilently(PackedNibbles.High(instrument.VibratoDelaySpeed));
            _vibratoSpeed.SetValueSilently(PackedNibbles.Low(instrument.VibratoDelaySpeed));
            _vibratoDepth.SetValueSilently(instrument.VibratoDepth);
            _slide.SetValueSilently(instrument.SlideRate);

            _continuous.Checked = instrument.PulseContinuous;
            _noHardRestart.Checked = instrument.NoHardRestart;

            RefreshDerived(instrument);
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

    private static string Describe(Instrument instrument)
    {
        var parts = new List<string>
        {
            $"WAVE ${instrument.Waveform:X2}",
            $"A/D ${instrument.AttackDecay:X2}",
            $"S/R ${instrument.SustainRelease:X2}",
        };

        if (instrument.HasVibrato)
            parts.Add($"VIB ${instrument.VibratoDelaySpeed:X2} ${instrument.VibratoDepth:X2}");
        if (instrument.HasSlide)
            parts.Add($"SLIDE {instrument.SlideRate:+#;-#;0}");

        return string.Join("   ·   ", parts);
    }

    private void Commit(Action<Instrument> edit)
    {
        if (_loading || _instrument is not { } instrument)
            return;

        edit(instrument);
        RefreshDerived(instrument);
        _subtitle.Text = Describe(instrument);

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

        _waveform.Waveform = instrument.Waveform;
        _wavetable.SetSteps(ProgramSteps.Wavetable(instrument));
        _noteProgram.SetSteps(ProgramSteps.NoteProgram(instrument));
        _filter.SetSteps(ProgramSteps.Filter(instrument));
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
        _slide.ValueChanged += v => Commit(i => i.SlideRate = (sbyte)v);

        _continuous.CheckedChanged += on => Commit(i => i.PulseContinuous = on);
        _noHardRestart.CheckedChanged += on => Commit(i => i.NoHardRestart = on);

        _waveform.WaveformChanged += w => Commit(i => i.Waveform = w);

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

    /// <summary>Nudges a filter step's cutoff, keeping it inside $01..$FE - $00 ends a program and
    /// $FF opens a setup record, so neither is a cutoff.</summary>
    private static FilterStep WithCutoff(FilterStep step, int by)
    {
        byte cutoff = (byte)Math.Clamp(step.Cutoff + by, FilterStep.MinCutoff, FilterStep.MaxCutoff);

        return step.IsSetup
            ? FilterStep.Setup(step.ModeVolume!.Value, step.ResonanceRouting!.Value, cutoff, step.SweepRate)
            : FilterStep.Sweep(cutoff);
    }

    private void WireProgram(
        ProgramLaneControl lane,
        Action<Instrument, int, int> nudge,
        Action<Instrument> append,
        Action<Instrument, int> remove)
    {
        lane.StepNudged += (step, by) => Commit(i => nudge(i, step, by));
        lane.StepAppended += () => Commit(append);
        lane.StepRemoved += step => Commit(i => remove(i, step));
    }

    private void BuildLayout()
    {
        _title.Font = new Font("Segoe UI", 13f);
        _title.ForeColor = DawPalette.Oscillator;
        _title.AutoSize = true;
        _title.Location = new Point(16, 12);

        _subtitle.Font = new Font("Consolas", 8f);
        _subtitle.ForeColor = DawPalette.TextDim;
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(18, 38);

        var header = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = DawPalette.Background };
        header.Controls.Add(_title);
        header.Controls.Add(_subtitle);

        // --- oscillator -------------------------------------------------------------------
        var oscillator = Section("OSCILLATOR", DawPalette.Oscillator);
        oscillator.Width = 620;
        oscillator.Dock = DockStyle.Left;

        _waveform.Dock = DockStyle.Top;
        _waveform.Height = 76;
        _waveform.Margin = new Padding(0, 0, 0, 8);

        _envelope.Dock = DockStyle.Bottom;
        _envelope.Height = 74;

        var knobRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DawPalette.Panel,
            Padding = new Padding(4, 8, 4, 8),
            WrapContents = false,
        };

        Knob(_attack, "ATT", DawPalette.Oscillator, 0, 15, Hex1);
        Knob(_decay, "DEC", DawPalette.Oscillator, 0, 15, Hex1);
        Knob(_sustain, "SUS", DawPalette.Oscillator, 0, 15, Hex1);
        Knob(_release, "REL", DawPalette.Oscillator, 0, 15, Hex1);
        Knob(_pulseWidth, "PW", DawPalette.Envelope, 0, 15, v => v.ToString(), Instrument.DefaultPulseWidth);
        Knob(_pulseSweep, "SWEEP", DawPalette.Envelope, -128, 127, v => v.ToString("+#;-#;0"));
        knobRow.Controls.AddRange([_attack, _decay, _sustain, _release, _pulseWidth, _pulseSweep]);

        var flags = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            BackColor = DawPalette.Panel,
            Padding = new Padding(4, 2, 4, 4),
            WrapContents = false,
        };
        _continuous.Accent = DawPalette.Envelope;
        _noHardRestart.Accent = DawPalette.Envelope;
        flags.Controls.AddRange([_continuous, _noHardRestart]);

        oscillator.Body.Controls.Add(knobRow);
        oscillator.Body.Controls.Add(_envelope);
        oscillator.Body.Controls.Add(flags);
        oscillator.Body.Controls.Add(_waveform);

        // --- effects ----------------------------------------------------------------------
        var effects = Section("VIBRATO & SLIDE", DawPalette.Effect);
        effects.Dock = DockStyle.Fill;

        var effectKnobs = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DawPalette.Panel,
            Padding = new Padding(8, 8, 4, 8),
            WrapContents = false,
        };
        Knob(_vibratoDelay, "DELAY", DawPalette.Effect, 0, 15, Hex1);
        Knob(_vibratoSpeed, "SPEED", DawPalette.Effect, 0, 15, Hex1);
        Knob(_vibratoDepth, "DEPTH", DawPalette.Effect, 0, 255, Hex2);
        Knob(_slide, "SLIDE", DawPalette.Effect, -128, 127, v => v.ToString("+#;-#;0"));
        effectKnobs.Controls.AddRange([_vibratoDelay, _vibratoSpeed, _vibratoDepth, _slide]);

        var effectNote = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            BackColor = DawPalette.Panel,
            ForeColor = DawPalette.TextFaint,
            Font = new Font("Segoe UI", 7.5f),
            Padding = new Padding(8, 2, 8, 6),
            Text = "Depth 0 turns vibrato off, rate 0 turns slide off. A song using neither leaves "
                   + "both out of the assembled player entirely.",
        };

        effects.Body.Controls.Add(effectKnobs);
        effects.Body.Controls.Add(effectNote);

        var top = new Panel { Dock = DockStyle.Top, Height = 300, BackColor = DawPalette.Background };
        top.Controls.Add(effects);
        top.Controls.Add(oscillator);

        // --- the three programs -----------------------------------------------------------
        _wavetable.Title = "WAVETABLE";
        _wavetable.Hint = "one control byte per frame · the last value holds";
        _wavetable.Accent = DawPalette.Envelope;

        _noteProgram.Title = "NOTE PROGRAM";
        _noteProgram.Hint = "absolute notes · ROOT returns to the row's own note";
        _noteProgram.Accent = DawPalette.Oscillator;

        // The lane picks a step; the editor below shows everything that step carries. Mode,
        // volume, resonance and routing live in a setup record and nowhere else, which is why the
        // lane alone could never edit them.
        _filter.StepSelected += step => _filterStep.ShowStep(_instrument, step);
        _filterStep.Edited += () => Commit(_ => { });
        _filterStep.Dock = DockStyle.Bottom;

        _filter.Title = "FILTER PROGRAM";
        _filter.Hint = "a setup record opens with $FF · $00 ends and holds the cutoff";
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
            programs.Controls.Add(lane, 0, row);
        }

        Controls.Add(programs);
        Controls.Add(_filterStep);
        Controls.Add(top);
        Controls.Add(header);
    }

    private static string Hex1(int value) => $"${value:X}";

    private static string Hex2(int value) => $"${value:X2}";

    private static void Knob(
        KnobControl knob, string caption, Color accent, int minimum, int maximum,
        Func<int, string> format, int defaultValue = 0)
    {
        knob.Caption = caption;
        knob.Accent = accent;
        knob.Minimum = minimum;
        knob.Maximum = maximum;
        knob.Format = format;
        knob.DefaultValue = defaultValue;
        knob.Margin = new Padding(2, 0, 2, 0);
    }

    private static SectionPanel Section(string title, Color accent) => new(title, accent);
}
