using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MagSid.Core.Model;
using AvColor = Avalonia.Media.Color;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The instrument editor under the list, as in the Windows editor's Retro view: name, envelope,
/// hard restart, pulse, vibrato, and the three per-frame programs. The programs are three columns of
/// one step a line - wavetable bytes, notes, filter steps - side by side, so a line is one frame
/// across all three, the way the Windows editor's merged grid lines them up.
///
/// Every field applies the moment it is left, through one validation that reads everything first
/// and only then writes, so a mistake never leaves the instrument half changed - the same rule as
/// InstrumentEditorPanel.TryCommit, whose checks this repeats.
/// </summary>
internal sealed class InstrumentEditorView : StackPanel
{
    private static readonly FontFamily Mono = new("Consolas, Menlo, DejaVu Sans Mono, monospace");
    private static readonly IBrush Label = new SolidColorBrush(AvColor.FromRgb(0x8A, 0x8A, 0xA8));
    private static readonly IBrush Heading = new SolidColorBrush(AvColor.FromRgb(0xAA, 0xAA, 0xFF));
    private static readonly IBrush Warning = new SolidColorBrush(AvColor.FromRgb(0xFF, 0xB0, 0x5C));

    private readonly TextBox _name = Field(220);
    private readonly TextBox _attackDecay = Hex();
    private readonly TextBox _sustainRelease = Hex();
    private readonly CheckBox _noHardRestart = new() { Content = "No HR" };
    private readonly CheckBox _softHardRestart = new() { Content = "Soft HR" };
    private readonly NumericUpDown _pulseWidth = new() { Minimum = 0, Maximum = 15, Width = 110, FormatString = "0" };
    private readonly TextBox _pulseSweep = Hex();
    private readonly CheckBox _pulseContinuous = new() { Content = "Cont." };
    private readonly CheckBox _vibrato = new() { Content = "Vibrato" };
    private readonly TextBox _vibDelaySpeed = Hex();
    private readonly TextBox _vibDepth = Hex();
    private readonly TextBox _wave = Program(70);
    private readonly TextBox _notes = Program(70);
    private readonly TextBox _filter = Program(170);
    private readonly ComboBox _ending = new() { ItemsSource = new[] { "Loop", "Hold", "Root" }, Width = 100 };
    private readonly NumericUpDown _loopFrom = new() { Minimum = 0, Maximum = 63, Width = 110, FormatString = "0" };
    private readonly TextBlock _problem = new() { Foreground = Warning, TextWrapping = TextWrapping.Wrap };

    private Instrument? _instrument;
    private bool _loading;

    public InstrumentEditorView()
    {
        Spacing = 6;

        Children.Add(Row(Caption("Name"), _name));
        Children.Add(Row(Caption("A/D"), _attackDecay, Caption("S/R"), _sustainRelease, _noHardRestart, _softHardRestart));
        Children.Add(Row(Caption("Pulse"), _pulseWidth, Caption("Sweep"), _pulseSweep, _pulseContinuous));
        Children.Add(Row(_vibrato, Caption("D/S"), _vibDelaySpeed, Caption("Depth"), _vibDepth));

        Children.Add(new TextBlock { Text = "Programs - one frame a line", Foreground = Heading, Margin = new Thickness(0, 6, 0, 0) });
        Children.Add(Row(Column("Wave", _wave), Column("Note", _notes), Column("Filter: MM RR CC rate, or CC", _filter)));
        Children.Add(Row(Caption("After the last note"), _ending, Caption("loop from"), _loopFrom));
        Children.Add(_problem);

        foreach (var box in new[] { _name, _attackDecay, _sustainRelease, _pulseSweep, _vibDelaySpeed, _vibDepth, _wave, _notes, _filter })
            box.LostFocus += (_, _) => Commit();
        foreach (var check in new[] { _noHardRestart, _softHardRestart, _pulseContinuous, _vibrato })
            check.IsCheckedChanged += (_, _) => Commit();
        _pulseWidth.ValueChanged += (_, _) => Commit();
        _loopFrom.ValueChanged += (_, _) => Commit();
        _ending.SelectionChanged += (_, _) =>
        {
            _loopFrom.IsEnabled = _ending.SelectedIndex == (int)ArpeggioEnding.Loop;
            Commit();
        };

        Load(null);
    }

    /// <summary>The instrument changed - the song is dirty and the list's names may differ.</summary>
    public event Action? Edited;

    /// <summary>Puts the caret in the name, all of it selected - a new instrument is named first.</summary>
    public void FocusName()
    {
        _name.Focus();
        _name.SelectAll();
    }

    public void Load(Instrument? instrument)
    {
        _instrument = instrument;
        _loading = true;
        try
        {
            IsEnabled = instrument is not null;
            _problem.Text = "";
            if (instrument is null)
                return;

            _name.Text = instrument.Name;
            _attackDecay.Text = instrument.AttackDecay.ToString("x2");
            _sustainRelease.Text = instrument.SustainRelease.ToString("x2");
            _noHardRestart.IsChecked = instrument.NoHardRestart;
            _softHardRestart.IsChecked = instrument.SoftHardRestart;
            _pulseWidth.Value = Math.Clamp(instrument.PulseWidth, 0, 15);
            _pulseSweep.Text = unchecked((byte)Math.Clamp(instrument.PulseSweep, -128, 127)).ToString("x2");
            _pulseContinuous.IsChecked = instrument.PulseContinuous;
            _vibrato.IsChecked = instrument.HasVibrato;
            _vibDelaySpeed.Text = (instrument.HasVibrato ? instrument.VibratoDelaySpeed : (byte)0x32).ToString("x2");
            _vibDepth.Text = (instrument.HasVibrato ? instrument.VibratoDepth : (byte)0x10).ToString("x2");

            var waves = instrument.Wavetable.Count > 0 ? instrument.Wavetable : [instrument.Waveform];
            _wave.Text = string.Join("\n", waves.Select(b => b.ToString("x2")));
            _notes.Text = string.Join("\n", instrument.Arpeggio.Select(n => n.ToString()));
            _filter.Text = string.Join("\n", instrument.Filter.Select(FilterText));
            _ending.SelectedIndex = (int)instrument.ArpeggioEnding;
            _loopFrom.Value = Math.Clamp(instrument.ArpeggioLoopPoint, 0, 63);
            _loopFrom.IsEnabled = instrument.ArpeggioEnding == ArpeggioEnding.Loop;
        }
        finally
        {
            _loading = false;
        }
    }

    private static string FilterText(FilterStep step) => step.IsSetup
        ? $"{step.ModeVolume:x2} {step.ResonanceRouting:x2} {step.Cutoff:x2}{(step.SweepRate != 0 ? $" {step.SweepRate:+0;-0}" : "")}"
        : step.Cutoff.ToString("x2");

    private void Commit()
    {
        if (_loading || _instrument is not { } instrument)
            return;

        _problem.Text = "";
        try
        {
            byte attackDecay = ReadHex(_attackDecay, "Attack/Decay");
            byte sustainRelease = ReadHex(_sustainRelease, "Sustain/Release");
            byte sweep = ReadHex(_pulseSweep, "Pulse sweep");
            bool vibrato = _vibrato.IsChecked == true;
            byte delaySpeed = vibrato ? ReadHex(_vibDelaySpeed, "Vibrato delay/speed") : instrument.VibratoDelaySpeed;
            byte depth = vibrato ? ReadHex(_vibDepth, "Vibrato depth") : (byte)0;
            if (vibrato && depth == 0)
                throw new FormatException("Vibrato depth must be 01-ff - 00 would mean no vibrato; switch Vibrato off instead.");

            var waves = Lines(_wave).Select((text, i) => ParseWave(text, i + 1)).ToList();
            var notes = Lines(_notes).Select((text, i) => NoteName.TryParse(text, out var note)
                ? note
                : throw new FormatException($"Frame {i + 1}: '{text}' is not a note.")).ToList();
            var filter = Lines(_filter).Select((text, i) => ParseFilter(text, i + 1)).ToList();
            var ending = (ArpeggioEnding)Math.Max(0, _ending.SelectedIndex);
            int loopFrom = (int)(_loopFrom.Value ?? 0);

            if (ending == ArpeggioEnding.Hold && notes.Count == 0)
                throw new FormatException("\"Hold\" stays on the last note, so the note column needs at least one.");
            if (ending == ArpeggioEnding.Loop && notes.Count > 0 && loopFrom >= notes.Count)
                throw new FormatException($"The loop goes back to step {loopFrom}, so there has to be a note there. Steps count from 0.");

            instrument.Name = (_name.Text ?? "").Trim();
            instrument.AttackDecay = attackDecay;
            instrument.SustainRelease = sustainRelease;
            instrument.NoHardRestart = _noHardRestart.IsChecked == true;
            instrument.SoftHardRestart = _softHardRestart.IsChecked == true;
            instrument.PulseWidth = (int)(_pulseWidth.Value ?? 0);
            instrument.PulseSweep = unchecked((sbyte)sweep);
            instrument.PulseContinuous = _pulseContinuous.IsChecked == true;
            if (vibrato)
                instrument.VibratoDelaySpeed = delaySpeed;
            instrument.VibratoDepth = depth;

            // One step is the plain waveform, as the Windows editor stores it.
            instrument.Wavetable.Clear();
            if (waves.Count == 1)
                instrument.Waveform = waves[0];
            else if (waves.Count > 1)
            {
                instrument.Waveform = waves[0];
                instrument.Wavetable.AddRange(waves);
            }

            instrument.Arpeggio.Clear();
            instrument.Arpeggio.AddRange(notes);
            instrument.ArpeggioEnding = ending;
            instrument.ArpeggioLoopPoint = ending == ArpeggioEnding.Loop ? loopFrom : 0;
            instrument.Filter.Clear();
            instrument.Filter.AddRange(filter);

            Edited?.Invoke();
        }
        catch (FormatException ex)
        {
            _problem.Text = ex.Message;
        }
    }

    private static IEnumerable<string> Lines(TextBox box) =>
        (box.Text ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);

    private static byte ParseWave(string text, int frame)
    {
        if (!byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
            throw new FormatException($"Frame {frame} waveform: '{text}' is not a hex byte.");
        if (value == 0)
            throw new FormatException($"Frame {frame}: $00 is what ends a wavetable, so it cannot also be a step.");
        return value;
    }

    private static FilterStep ParseFilter(string text, int frame)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        byte Hex(string part, string what) =>
            byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value)
                ? value
                : throw new FormatException($"Filter step {frame}: '{part}' is not a hex {what}.");

        byte Cutoff(string part)
        {
            byte cutoff = Hex(part, "cutoff");
            if (cutoff is < FilterStep.MinCutoff or > FilterStep.MaxCutoff)
                throw new FormatException($"Filter step {frame}: the cutoff must be ${FilterStep.MinCutoff:x2}-${FilterStep.MaxCutoff:x2}.");
            return cutoff;
        }

        switch (parts.Length)
        {
            case 1:
                return FilterStep.Sweep(Cutoff(parts[0]));
            case 3:
            case 4:
                int rate = 0;
                if (parts.Length == 4 && (!int.TryParse(parts[3], out rate) || rate is < -128 or > 127))
                    throw new FormatException($"Filter step {frame}: the sweep rate is a signed number, -128 to 127.");
                return FilterStep.Setup(Hex(parts[0], "mode+volume"), Hex(parts[1], "resonance+routing"), Cutoff(parts[2]), rate);
            default:
                throw new FormatException($"Filter step {frame}: write a setup as 'MM RR CC [rate]' or a sweep as 'CC'.");
        }
    }

    private static byte ReadHex(TextBox box, string what)
    {
        string text = (box.Text ?? "").Trim();
        return byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value)
            ? value
            : throw new FormatException($"{what}: '{text}' is not a hex byte, 00-ff.");
    }

    private static TextBox Field(double width) => new() { Width = width, FontFamily = Mono };

    private static TextBox Hex() => new() { Width = 44, FontFamily = Mono, MaxLength = 2, TextAlignment = TextAlignment.Center };

    private static TextBox Program(double width) => new()
    {
        Width = width,
        MinHeight = 160,
        AcceptsReturn = true,
        FontFamily = Mono,
        TextWrapping = TextWrapping.NoWrap,
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Foreground = Label,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 2, 0),
    };

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var child in children)
            row.Children.Add(child);
        return row;
    }

    private static StackPanel Column(string caption, Control content) => new()
    {
        Spacing = 2,
        Children = { new TextBlock { Text = caption, Foreground = Label, FontSize = 11 }, content },
    };
}
