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
    private static readonly string[] GmFamilies =
    [
        "Piano", "Chromatic perc.", "Organ", "Guitar", "Bass", "Strings", "Ensemble", "Brass",
        "Reed", "Pipe", "Synth lead", "Synth pad", "Synth FX", "Ethnic", "Percussive", "Sound FX",
    ];

    private readonly MidiSong _midi;
    private readonly IReadOnlyList<MidiSource> _sources;
    private readonly ComboBox[] _source = new ComboBox[Song.ChannelCount];
    private readonly ComboBox[] _role = new ComboBox[Song.ChannelCount];
    private readonly ComboBox[] _octave = new ComboBox[Song.ChannelCount];
    private readonly ComboBox[] _drums = new ComboBox[Song.ChannelCount];
    private readonly CheckBox[] _hiHat = new CheckBox[Song.ChannelCount];
    private readonly Label[] _instrumentName = new Label[Song.ChannelCount];
    private readonly Instrument?[] _instrument = new Instrument?[Song.ChannelCount];
    private readonly ComboBox _grid = Combo("16th notes - 4 rows a beat", "32nd notes - 8 rows a beat");
    private readonly NumericUpDown _speed = Number(0, 255);
    private readonly Label _speedBpm = new() { AutoSize = true, Margin = new Padding(6, 8, 18, 3), ForeColor = EditorPalette.Subtitle };
    private readonly NumericUpDown _fromBar = Number(1, 1);
    private readonly NumericUpDown _toBar = Number(1, 1);
    private readonly Label _report = new() { AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(3, 10, 3, 3) };
    private readonly Button _import = new() { Text = "Import", DialogResult = DialogResult.OK, AutoSize = true };
    private bool _updating;

    /// <summary>The song as the report describes it - set while the choices make one.</summary>
    public MidiImportResult? Result { get; private set; }

    public MidiImportForm(MidiSong midi, string fileName)
    {
        _midi = midi;
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
        root.Controls.Add(Heading(
            $"{fileName}  -  {midi.Bpm:0.#} BPM, {midi.BeatsPerBar}/{midi.BeatUnit}, " +
            $"{midi.Tracks.Count} track{(midi.Tracks.Count == 1 ? "" : "s")}"));
        root.Controls.Add(BuildSourceList());
        root.Controls.Add(Heading("The three SID voices"));
        root.Controls.Add(BuildVoices());
        root.Controls.Add(Heading("Timing"));
        root.Controls.Add(BuildTiming());
        root.Controls.Add(_report);
        root.Controls.Add(Hint(
            "Each voice plays one note at a time: Melody keeps the highest note of a chord, Bass the " +
            "lowest. Drums + bass puts the rhythm section on one voice the C64 way - kick and snare " +
            "from the drum part, the bass line between them, the kick settling on the bass note. " +
            "Notes snap to the grid, and a bar that repeats is stored once. Load your own .json " +
            "instruments here (for Drums + bass, the bass; for Chords, the sound every chord is " +
            "played on) or edit them afterwards. Chords reads the chord of each beat; an inversion " +
            "is the same chord, so C-E-G and E-G-C share one instrument, and a chord has at most 4 notes."));
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
            grid.Rows.Add(Describe(source), source.Name, Sound(source), source.NoteCount,
                $"{KeyName(source.LowestKey)} - {KeyName(source.HighestKey)}", source.MostAtOnce);
        }

        return grid;
    }

    private Control BuildVoices()
    {
        var table = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 8 };
        string[] sources = ["(silent)", .. _sources.Select(s => $"{Describe(s)}  {s.Name}".TrimEnd())];
        string[] drumSources = ["(no drums)", .. sources[1..]];

        for (int voice = 0; voice < Song.ChannelCount; voice++)
        {
            int v = voice;
            _source[v] = Combo(sources);
            _source[v].Width = 220;
            _role[v] = Combo("Melody - highest note", "Bass - lowest note", "Drums + bass", "Chords - arpeggios");
            _role[v].Width = 160;
            _drums[v] = Combo(drumSources);
            _drums[v].Width = 190;
            _drums[v].SelectedIndex = 0;
            _hiHat[v] = new CheckBox { Text = "Hi-hat", AutoSize = true, Margin = new Padding(6, 6, 3, 3) };
            _octave[v] = Combo("-3 oct", "-2 oct", "-1 oct", "0 oct", "+1 oct", "+2 oct", "+3 oct");
            _octave[v].Width = 70;
            _octave[v].SelectedIndex = 3;
            _instrumentName[v] = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 3), MinimumSize = new Size(110, 0) };

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
        int Index(MidiSource? s) => s is null ? 0 : _sources.ToList().IndexOf(s) + 1;

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
            _hiHat[v].Enabled = kit;
            bool chords = Role(v) == VoiceRole.Chords;
            string sound = _instrument[v]?.Name is { Length: > 0 } name ? name
                : _instrument[v] is not null ? "(loaded)"
                : chords ? "pulse (default)"
                : $"{(kit ? "Bass" : MidiImporter.Default(Role(v)).Name)} (default)";
            _instrumentName[v].Text = !VoiceOf(v).Plays ? "" : kit ? $"Kit + {sound}" : chords ? $"Chords on {sound}" : sound;
        }

        _speedBpm.Text = $"= {MidiImporter.SongBpm((int)_speed.Value, RowsPerBeat):0.#} BPM";

        try
        {
            var voices = Enumerable.Range(0, Song.ChannelCount).Select(VoiceOf).ToList();
            Result = MidiImporter.Import(_midi, new MidiImportOptions(
                voices, RowsPerBeat, (int)_speed.Value, (int)_fromBar.Value - 1, (int)(_toBar.Value - _fromBar.Value) + 1));
            _report.Text = Describe(Result.Report);
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

    private VoiceRole Role(int voice) => _role[voice].SelectedIndex switch
    {
        1 => VoiceRole.Bass,
        2 => VoiceRole.DrumsAndBass,
        3 => VoiceRole.Chords,
        _ => VoiceRole.Melody,
    };

    private static string Describe(MidiImportReport report)
    {
        var lines = new List<string>
        {
            $"{report.Bars} bars in {report.Patterns} patterns of {report.BarsPerPattern} bar{(report.BarsPerPattern == 1 ? "" : "s")} " +
            $"({report.RowsPerBar * report.BarsPerPattern} rows) - {report.TotalBytes} bytes with the player.",
            $"{report.NotesPlaced} notes placed.",
        };

        var bent = new List<string>();
        if (report.NotesDropped > 0)
            bent.Add($"{report.NotesDropped} dropped where a voice had more than one note at once");
        if (report.NotesMoved > 0)
            bent.Add($"{report.NotesMoved} moved onto the grid");
        if (report.NotesShifted > 0)
            bent.Add($"{report.NotesShifted} moved by octaves into C-0..B-7");
        if (bent.Count > 0)
            lines.Add(string.Join("; ", bent) + ".");
        if (report.ChordsFound > 0)
            lines.Add(report.ChordsKept == report.ChordsFound
                ? $"{report.ChordsFound} chords, one arpeggio instrument each."
                : $"{report.ChordsFound} chords; the {report.ChordsKept} most played became instruments " +
                  "and the rest play the nearest of them - instruments run out at 16.");
        if (report.DrumHits + report.DrumsLeftOut > 0)
            lines.Add($"{report.DrumHits} drum hits placed, {report.DrumsLeftOut} left out - hi-hats when off, " +
                      "toms and cymbals, flams, and hits a kick or snare already had.");

        double off = (report.SongBpm / report.SourceBpm - 1) * 100;
        if (Math.Abs(off) >= 0.5)
            lines.Add($"Plays at {report.SongBpm:0.#} BPM, {Math.Abs(off):0}% {(off > 0 ? "faster" : "slower")} than the file.");
        if (report.TempoChanges > 0)
            lines.Add($"The file changes tempo {report.TempoChanges} time{(report.TempoChanges == 1 ? "" : "s")}; the song keeps one speed.");

        return string.Join(Environment.NewLine, lines);
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

    private static string Describe(MidiSource source) =>
        $"T{source.Track + 1} ch{source.Channel + 1}";

    private static string Sound(MidiSource source) =>
        source.LooksLikeDrums ? "Drums" : source.Program is { } p ? $"{GmFamilies[p / 8]} ({p + 1})" : "";

    private static string KeyName(int key) =>
        $"{"C C#D D#E F F#G G#A A#B "[(key % 12 * 2)..(key % 12 * 2 + 2)].Trim()}{key / 12 - 1}";

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
