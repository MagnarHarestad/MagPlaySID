using MagSid.Core.Midi;
using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Editor.App;

/// <summary>
/// Chooses what each of the three SID voices is made from, and shows what the song will cost
/// before anything is replaced. Every change re-runs the import, so the report below is always
/// the song Import would make.
/// </summary>
public sealed class MidiImportForm : Form
{
    private readonly MidiSong _midi;
    private readonly IReadOnlyList<MidiSource> _sources;
    private readonly ComboBox[] _source = new ComboBox[Song.ChannelCount];
    private readonly ComboBox[] _role = new ComboBox[Song.ChannelCount];
    private readonly ComboBox[] _octave = new ComboBox[Song.ChannelCount];
    private readonly ComboBox[] _drums = new ComboBox[Song.ChannelCount];
    private readonly CheckBox[] _hiHat = new CheckBox[Song.ChannelCount];
    private readonly Label[] _instrumentName = new Label[Song.ChannelCount];
    private readonly Instrument?[] _instrument = new Instrument?[Song.ChannelCount];
    private readonly ComboBox _grid = Combo(MidiImportText.Grids);
    private readonly NumericUpDown _speed = Number(0, 255);
    private readonly Label _speedBpm = new() { AutoSize = true, Margin = new Padding(6, 8, 18, 3), ForeColor = EditorPalette.Subtitle };
    private readonly NumericUpDown _fromBar = Number(1, 1);
    private readonly NumericUpDown _toBar = Number(1, 1);
    private readonly CheckBox _useBank = new() { Text = "GM sound bank", Checked = true, AutoSize = true, Margin = new Padding(18, 6, 3, 3) };
    private readonly CheckBox _keepVibrato = new() { Text = "Keep vibrato (+112 bytes)", AutoSize = true, Margin = new Padding(6, 6, 3, 3) };
    private readonly CheckBox _chordGateOffs = new() { Text = "Chord gate-off", Checked = true, AutoSize = true, Margin = new Padding(6, 6, 3, 3) };
    private readonly GmBank _bank = GmBank.Load(EditorSettings.BankDirectory);
    private readonly Label _report = new() { AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(3, 10, 3, 3) };
    private readonly Button _import = new() { Text = "Import", DialogResult = DialogResult.OK, AutoSize = true };
    private bool _updating;

    /// <summary>The song as the report describes it - set while the choices make one.</summary>
    public MidiImportResult? Result { get; private set; }

    /// <summary>Plays a sound of the bank before it is chosen - the editor's own audition.</summary>
    private readonly Action<Instrument, NoteName>? _audition;

    public MidiImportForm(MidiSong midi, string fileName, Action<Instrument, NoteName>? audition = null)
    {
        _midi = midi;
        _audition = audition;
        _sources = midi.Sources();

        Text = "Import MIDI";
        Icon = AppIcon.Value;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        DarkTheme.ApplyTitleBar(this);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        BackColor = EditorPalette.Background;
        ForeColor = EditorPalette.Body;

        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        root.Controls.Add(Heading(MidiImportText.Heading(midi, fileName)));
        root.Controls.Add(BuildSourceList());
        root.Controls.Add(Heading("The three SID voices"));
        root.Controls.Add(BuildVoices());
        root.Controls.Add(Heading("Timing"));
        root.Controls.Add(BuildTiming());
        root.Controls.Add(_report);
        root.Controls.Add(Hint(MidiImportText.Hint(EditorSettings.BankDirectory)));
        root.Controls.Add(BuildButtons());
        Controls.Add(root);

        ChooseDefaults();
        foreach (var box in _source.Concat(_role).Concat(_octave).Concat(_drums).Append(_grid))
            box.SelectedIndexChanged += (_, _) => Recalculate(sourcesChanged: true);
        foreach (var box in _hiHat)
            box.CheckedChanged += (_, _) => Recalculate(sourcesChanged: false);
        _grid.SelectedIndexChanged += (_, _) => SuggestSpeed();
        foreach (var number in new[] { _speed, _fromBar, _toBar })
            number.ValueChanged += (_, _) => Recalculate(sourcesChanged: false);

        SuggestSpeed();
        Recalculate(sourcesChanged: true);
    }

    private Control BuildSourceList()
    {
        var grid = new DataGridView
        {
            Width = 760,
            Height = Math.Min(200, 26 + 22 * Math.Max(1, _sources.Count)),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Margin = new Padding(3, 3, 3, 6),
        };
        DarkTheme.Apply(grid);
        grid.Columns.Add("source", "Source");
        grid.Columns.Add("name", "Track name");
        grid.Columns.Add("sound", "Sound");
        grid.Columns.Add("notes", "Notes");
        grid.Columns.Add("range", "Range");
        grid.Columns.Add("chords", "At once");
        grid.Columns["name"]!.FillWeight = 180;
        grid.Columns["sound"]!.FillWeight = 130;

        foreach (var source in _sources)
        {
            grid.Rows.Add(MidiImportText.Source(source), source.Name, MidiImportText.Sound(source), source.NoteCount,
                MidiImportText.Range(source), source.MostAtOnce);
        }

        return grid;
    }

    private Control BuildVoices()
    {
        var table = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 8 };
        string[] sources = ["(silent)", .. _sources.Select(s => $"{MidiImportText.Source(s)}  {s.Name}".TrimEnd())];
        string[] drumSources = ["(no drums)", .. sources[1..]];

        for (int voice = 0; voice < Song.ChannelCount; voice++)
        {
            int v = voice;
            _source[v] = Combo(sources);
            _source[v].Width = 220;
            _role[v] = Combo(MidiImportText.Roles);
            _role[v].Width = 160;
            _drums[v] = Combo(drumSources);
            _drums[v].Width = 190;
            _drums[v].SelectedIndex = 0;
            _hiHat[v] = new CheckBox { Text = "Hi-hat", AutoSize = true, Margin = new Padding(6, 6, 3, 3) };
            _octave[v] = Combo(MidiImportText.Octaves);
            _octave[v].Width = 70;
            _octave[v].SelectedIndex = 3;
            _instrumentName[v] = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 3), MinimumSize = new Size(110, 0) };

            var bank = SmallButton("Soundbank...");
            bank.Click += (_, _) => ShowSoundBank(v, bank);
            var load = SmallButton("Load...");
            load.Click += (_, _) => LoadInstrument(v);
            var reset = SmallButton("Default");
            reset.Click += (_, _) =>
            {
                _instrument[v] = null;
                Recalculate(sourcesChanged: false);
            };

            table.Controls.Add(new Label { Text = $"Voice {v + 1}", AutoSize = true, Margin = new Padding(3, 8, 8, 3) }, 0, v);
            table.Controls.Add(_source[v], 1, v);
            table.Controls.Add(_role[v], 2, v);
            table.Controls.Add(_drums[v], 3, v);
            table.Controls.Add(_hiHat[v], 4, v);
            table.Controls.Add(_octave[v], 5, v);
            table.Controls.Add(_instrumentName[v], 6, v);
            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
            buttons.Controls.Add(bank);
            buttons.Controls.Add(load);
            buttons.Controls.Add(reset);
            table.Controls.Add(buttons, 7, v);
        }

        return table;
    }

    private Control BuildTiming()
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        _grid.Width = 190;
        _grid.SelectedIndex = 0;
        row.Controls.Add(Caption("Grid"));
        row.Controls.Add(_grid);
        row.Controls.Add(Caption("Speed"));
        row.Controls.Add(_speed);
        row.Controls.Add(_speedBpm);
        row.Controls.Add(Caption("Bars"));
        row.Controls.Add(_fromBar);
        row.Controls.Add(Caption("to"));
        row.Controls.Add(_toBar);
        row.Controls.Add(_useBank);
        row.Controls.Add(_keepVibrato);
        row.Controls.Add(_chordGateOffs);
        _chordGateOffs.CheckedChanged += (_, _) => Recalculate(sourcesChanged: false);
        _useBank.CheckedChanged += (_, _) => Recalculate(sourcesChanged: false);
        _keepVibrato.CheckedChanged += (_, _) => Recalculate(sourcesChanged: false);
        return row;
    }

    private Control BuildButtons()
    {
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        DarkTheme.Apply(_import);
        DarkTheme.Apply(cancel);
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 10, 0, 0),
        };
        panel.Controls.Add(cancel);
        panel.Controls.Add(_import);
        AcceptButton = _import;
        CancelButton = cancel;
        return panel;
    }

    /// <summary>The importer's own suggestion (see MidiImporter.SuggestVoices), shown in the
    /// boxes.</summary>
    private void ChooseDefaults()
    {
        int Index(MidiSource? s) => _midi.IndexOf(s) + 1;

        var suggested = MidiImporter.SuggestVoices(_midi);
        for (int v = 0; v < Song.ChannelCount; v++)
        {
            _source[v].SelectedIndex = Index(suggested[v].Source);
            _drums[v].SelectedIndex = Index(suggested[v].Drums);
            _role[v].SelectedIndex = (int)suggested[v].Role;
        }
    }

    private void SuggestSpeed()
    {
        var (speed, _) = MidiImporter.SuggestSpeed(_midi.Bpm, RowsPerBeat);
        _speed.Value = speed;
    }

    private int RowsPerBeat => _grid.SelectedIndex == 1 ? 8 : 4;

    private MidiSource? SourceOf(int voice) =>
        _source[voice].SelectedIndex is > 0 and var i ? _sources[i - 1] : null;

    private MidiSource? DrumsOf(int voice) =>
        Role(voice) == VoiceRole.DrumsAndBass && _drums[voice].SelectedIndex is > 0 and var i ? _sources[i - 1] : null;

    private VoiceImport VoiceOf(int v) =>
        new(SourceOf(v), Role(v), (_octave[v].SelectedIndex - 3) * 12, _instrument[v], DrumsOf(v), _hiHat[v].Checked);

    private void Recalculate(bool sourcesChanged)
    {
        if (_updating || _source.Any(s => s is null) || _role.Any(r => r.SelectedIndex < 0))
            return;

        _updating = true;
        try
        {
            if (sourcesChanged)
            {
                // The bar range follows the chosen parts; a range left at its end stays at the end.
                int total = Math.Max(1, MidiImporter.BarsIn(_midi,
                    Enumerable.Range(0, Song.ChannelCount).SelectMany(v => MidiImporter.PartsOf(VoiceOf(v))), RowsPerBeat));
                bool atEnd = _toBar.Value == _toBar.Maximum;
                _fromBar.Maximum = total;
                _toBar.Maximum = total;
                if (atEnd)
                    _toBar.Value = total;
            }
            if (_toBar.Value < _fromBar.Value)
                _toBar.Value = _fromBar.Value;
        }
        finally
        {
            _updating = false;
        }

        for (int v = 0; v < Song.ChannelCount; v++)
        {
            bool kit = Role(v) == VoiceRole.DrumsAndBass;
            _drums[v].Enabled = kit;
            // Dimmed by hand rather than disabled: Windows draws a disabled check box's text in
            // its own grey, which on this dark form can hardly be read.
            _hiHat[v].AutoCheck = _hiHat[v].TabStop = kit;
            _hiHat[v].ForeColor = kit ? EditorPalette.Body : EditorPalette.Subtitle;
            _instrumentName[v].Text = MidiImportText.VoiceSound(VoiceOf(v), v, _instrument[v]?.Name, Options([]));
        }

        _speedBpm.Text = $"= {MidiImporter.SongBpm((int)_speed.Value, RowsPerBeat):0.#} BPM";

        try
        {
            Result = MidiImporter.Import(_midi, Options(Enumerable.Range(0, Song.ChannelCount).Select(VoiceOf).ToList()));
            _report.Text = MidiImportText.Report(Result.Report, _chordGateOffs.Checked);
            _report.ForeColor = EditorPalette.Body;
            _import.Enabled = true;
        }
        catch (MidiImportException ex)
        {
            Result = null;
            _report.Text = ex.Message;
            _report.ForeColor = EditorPalette.Warning;
            _import.Enabled = false;
        }
    }

    private MidiImportOptions Options(IReadOnlyList<VoiceImport> voices) => new(
        voices, RowsPerBeat, (int)_speed.Value, (int)_fromBar.Value - 1, (int)(_toBar.Value - _fromBar.Value) + 1,
        UseBank: _useBank.Checked, Bank: _bank, KeepVibrato: _keepVibrato.Checked, ChordGateOffs: _chordGateOffs.Checked);

    private VoiceRole Role(int voice) => _role[voice].SelectedIndex switch
    {
        1 => VoiceRole.Bass,
        2 => VoiceRole.DrumsAndBass,
        3 => VoiceRole.Chords,
        _ => VoiceRole.Melody,
    };

    /// <summary>The sound bank's cards, dropped under the voice's button: a click plays a sound, a
    /// double-click or its + makes it the voice's instrument - as Load does with a file.</summary>
    private void ShowSoundBank(int voice, Control button)
    {
        var picker = new Daw.SoundBankPicker(GmBank.Load(EditorSettings.BankDirectory).All.ToList())
        {
            Hint = $"Click to hear  ·  double-click or + to play voice {voice + 1} on it",
        };
        var host = new ToolStripControlHost(picker) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = picker.Size };
        var drop = new ToolStripDropDown { Padding = Padding.Empty, BackColor = Daw.DawPalette.Panel };
        drop.Items.Add(host);
        drop.Closed += (_, _) => BeginInvoke(drop.Dispose);

        bool low = Role(voice) is VoiceRole.Bass or VoiceRole.DrumsAndBass;
        picker.Auditioned += sound => _audition?.Invoke(sound.Instrument,
            NoteName.Parse(low || sound.Key is "Bass" or "SynthBass" or "KickBass" ? "C-2" : "C-4"));
        picker.Picked += sound =>
        {
            drop.Close();
            _instrument[voice] = sound.Instrument;
            Recalculate(sourcesChanged: false);
        };
        picker.LoadRequested += () =>
        {
            drop.Close();
            BeginInvoke(() => LoadInstrument(voice));
        };
        picker.FolderRequested += () =>
        {
            drop.Close();
            MagSid.Editor.App.Controls.InstrumentListHeader.OpenBankFolder();
        };

        drop.Show(button, new Point(0, button.Height + 2));
    }

    private void LoadInstrument(int voice)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "MagSID instruments (*.json)|*.json|All files (*.*)|*.*",
            Title = $"Instrument for voice {voice + 1}",
        };
        if (EditorSettings.InstrumentDirectory is { Length: > 0 } remembered)
            dialog.InitialDirectory = remembered;
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            _instrument[voice] = InstrumentFile.Read(File.ReadAllText(dialog.FileName));
            EditorSettings.InstrumentDirectory = Path.GetDirectoryName(dialog.FileName) ?? "";
            Recalculate(sourcesChanged: false);
        }
        catch (Exception ex) when (ex is InstrumentFileException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Cannot load instrument", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static ComboBox Combo(params string[] items)
    {
        var box = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = EditorPalette.FieldBackground,
            ForeColor = EditorPalette.Body,
            Margin = new Padding(3, 4, 3, 3),
        };
        box.Items.AddRange(items);
        return box;
    }

    private static NumericUpDown Number(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = 56,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(3, 4, 3, 3),
    };

    private static Button SmallButton(string text)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
        DarkTheme.Apply(button);
        return button;
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = EditorPalette.Heading,
        Margin = new Padding(3, 10, 3, 4),
    };

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(3, 8, 3, 3),
    };

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(760, 0),
        ForeColor = EditorPalette.Subtitle,
        Margin = new Padding(3, 8, 3, 3),
    };
}
