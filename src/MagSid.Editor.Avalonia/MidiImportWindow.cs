using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MagSid.Core.Midi;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Editor.App;
using MagSid.Editor.App.Controls;
using MagSid.Editor.App.Daw;
using AvColor = Avalonia.Media.Color;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// File → Import MIDI: what each of the three SID voices is made from, and what the song will cost
/// before anything is replaced. Every change re-runs the import, so the report is always the song
/// Import would make. The choices, the importer and the words are the Windows dialog's
/// (MidiImportForm, MidiImporter, MidiImportText); only the controls are Avalonia's.
/// </summary>
internal sealed class MidiImportWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(AvColor.FromRgb(0x8A, 0x8A, 0xA8));
    private static readonly IBrush Body = new SolidColorBrush(AvColor.FromRgb(0xD2, 0xD2, 0xDE));
    private static readonly IBrush Heading = new SolidColorBrush(AvColor.FromRgb(0x66, 0xCC, 0xFF));
    private static readonly IBrush Warning = new SolidColorBrush(AvColor.FromRgb(0xFF, 0xB0, 0x5C));

    private readonly MidiSong _midi;
    private readonly IReadOnlyList<MidiSource> _sources;
    private readonly Action<Instrument, NoteName>? _audition;
    private readonly ComboBox[] _source = new ComboBox[Song.ChannelCount];
    private readonly ComboBox[] _role = new ComboBox[Song.ChannelCount];
    private readonly ComboBox[] _octave = new ComboBox[Song.ChannelCount];
    private readonly ComboBox[] _drums = new ComboBox[Song.ChannelCount];
    private readonly CheckBox[] _hiHat = new CheckBox[Song.ChannelCount];
    private readonly TextBlock[] _instrumentName = new TextBlock[Song.ChannelCount];
    private readonly Instrument?[] _instrument = new Instrument?[Song.ChannelCount];
    private readonly ComboBox _grid = Combo(MidiImportText.Grids);
    private readonly NumericUpDown _speed = Number(0, 255);
    private readonly TextBlock _speedBpm = new() { Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly NumericUpDown _fromBar = Number(1, 1);
    private readonly NumericUpDown _toBar = Number(1, 1);
    private readonly CheckBox _useBank = new() { Content = "GM sound bank", IsChecked = true };
    private readonly CheckBox _keepVibrato = new() { Content = "Keep vibrato (+112 bytes)" };
    private readonly CheckBox _chordGateOffs = new() { Content = "Chord gate-off", IsChecked = true };
    private readonly GmBank _bank = GmBank.Load(EditorSettings.BankDirectory);
    private readonly TextBlock _report = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _import = new() { Content = "Import", MinWidth = 90, IsDefault = true };
    private bool _updating = true;

    /// <summary>The song as the report describes it - set while the choices make one.</summary>
    public MidiImportResult? Result { get; private set; }

    public MidiImportWindow(MidiSong midi, string fileName, Action<Instrument, NoteName>? audition)
    {
        _midi = midi;
        _audition = audition;
        _sources = midi.Sources();

        Title = "Import MIDI";
        Width = 1200;
        FontSize = 13;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        _import.Click += (_, _) => Close(Result);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 4,
            Children =
            {
                Caption(MidiImportText.Heading(midi, fileName), Heading),
                BuildSourceList(),
                Caption("The three SID voices", Heading),
                BuildVoices(),
                Caption("Timing", Heading),
                BuildTiming(),
                _report,
                new TextBlock
                {
                    Text = MidiImportText.Hint(EditorSettings.BankDirectory),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Dim,
                    FontSize = 12,
                    Margin = new Thickness(0, 8, 0, 0),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0),
                    Children = { _import, cancel },
                },
            },
        };

        ChooseDefaults();
        SuggestSpeed();
        _updating = false;

        foreach (var box in _source.Concat(_role).Concat(_octave).Concat(_drums))
            box.SelectionChanged += (_, _) => Recalculate(sourcesChanged: true);
        _grid.SelectionChanged += (_, _) =>
        {
            SuggestSpeed();
            Recalculate(sourcesChanged: true);
        };
        foreach (var box in _hiHat.Append(_useBank).Append(_keepVibrato).Append(_chordGateOffs))
            box.IsCheckedChanged += (_, _) => Recalculate(sourcesChanged: false);
        foreach (var number in new[] { _speed, _fromBar, _toBar })
            number.ValueChanged += (_, _) => Recalculate(sourcesChanged: false);

        Recalculate(sourcesChanged: true);
    }

    // ------------------------------------------------------------------------------------ layout

    private Control BuildSourceList()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("90,2*,1.4*,70,110,70"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        string[] header = ["Source", "Track name", "Sound", "Notes", "Range", "At once"];
        for (int c = 0; c < header.Length; c++)
            Cell(grid, 0, c, header[c], Dim);

        for (int i = 0; i < _sources.Count; i++)
        {
            var source = _sources[i];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            string[] cells = [MidiImportText.Source(source), source.Name, MidiImportText.Sound(source),
                source.NoteCount.ToString(), MidiImportText.Range(source), source.MostAtOnce.ToString()];
            for (int c = 0; c < cells.Length; c++)
                Cell(grid, i + 1, c, cells[c], Body);
        }

        grid.RowDefinitions.Insert(0, new RowDefinition(GridLength.Auto));
        return new Border
        {
            Background = new SolidColorBrush(AvColor.FromRgb(0x16, 0x16, 0x28)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            Child = new ScrollViewer { MaxHeight = 190, Content = grid },
        };

        static void Cell(Grid grid, int row, int column, string text, IBrush brush)
        {
            var block = new TextBlock { Text = text, Foreground = brush, Margin = new Thickness(0, 2, 8, 2), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }
    }

    private Control BuildVoices()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,200,185,165,Auto,90,*,Auto") };
        string[] sources = ["(silent)", .. _sources.Select(s => $"{MidiImportText.Source(s)}  {s.Name}".TrimEnd())];
        string[] drumSources = ["(no drums)", .. sources[1..]];

        for (int voice = 0; voice < Song.ChannelCount; voice++)
        {
            int v = voice;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _source[v] = Combo(sources);
            _role[v] = Combo(MidiImportText.Roles);
            _drums[v] = Combo(drumSources);
            _drums[v].SelectedIndex = 0;
            _hiHat[v] = new CheckBox { Content = "Hi-hat" };
            _octave[v] = Combo(MidiImportText.Octaves);
            _octave[v].SelectedIndex = 3;
            _instrumentName[v] = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Body, TextTrimming = TextTrimming.CharacterEllipsis };

            var bank = new Button { Content = "Soundbank..." };
            bank.Click += (_, _) => ShowSoundBank(v, bank);
            var load = new Button { Content = "Load..." };
            load.Click += async (_, _) => await LoadInstrumentAsync(v);
            var reset = new Button { Content = "Default" };
            reset.Click += (_, _) =>
            {
                _instrument[v] = null;
                Recalculate(sourcesChanged: false);
            };

            Control[] row =
            [
                new TextBlock { Text = $"Voice {v + 1}", VerticalAlignment = VerticalAlignment.Center, Foreground = Body },
                _source[v], _role[v], _drums[v], _hiHat[v], _octave[v], _instrumentName[v],
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { bank, load, reset } },
            ];
            for (int c = 0; c < row.Length; c++)
            {
                row[c].Margin = new Thickness(0, 3, 8, 3);
                Grid.SetRow(row[c], v);
                Grid.SetColumn(row[c], c);
                grid.Children.Add(row[c]);
            }
        }

        return grid;
    }

    private Control BuildTiming()
    {
        _grid.SelectedIndex = 0;
        _grid.Width = 210;
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[]
                 {
                     Caption("Grid", Body), _grid, Caption("Speed", Body), _speed, _speedBpm,
                     Caption("Bars", Body), _fromBar, Caption("to", Body), _toBar,
                     _useBank, _keepVibrato, _chordGateOffs,
                 })
        {
            control.Margin = new Thickness(0, 2, 8, 2);
            control.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(control);
        }

        return row;
    }

    // ------------------------------------------------------------------------------------ choices

    /// <summary>The importer's own suggestion (MidiImporter.SuggestVoices), shown in the boxes.</summary>
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
        new(SourceOf(v), Role(v), (Math.Max(0, _octave[v].SelectedIndex) - 3) * 12, _instrument[v], DrumsOf(v), _hiHat[v].IsChecked == true);

    private VoiceRole Role(int voice) => _role[voice].SelectedIndex switch
    {
        1 => VoiceRole.Bass,
        2 => VoiceRole.DrumsAndBass,
        3 => VoiceRole.Chords,
        _ => VoiceRole.Melody,
    };

    private static int Value(NumericUpDown number) => (int)(number.Value ?? number.Minimum);

    private void Recalculate(bool sourcesChanged)
    {
        if (_updating)
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
            _drums[v].IsEnabled = kit;
            _hiHat[v].IsEnabled = kit;
            _instrumentName[v].Text = MidiImportText.VoiceSound(VoiceOf(v), v, _instrument[v]?.Name, Options([]));
        }

        _speedBpm.Text = $"= {MidiImporter.SongBpm(Value(_speed), RowsPerBeat):0.#} BPM";

        try
        {
            Result = MidiImporter.Import(_midi, Options(Enumerable.Range(0, Song.ChannelCount).Select(VoiceOf).ToList()));
            _report.Text = MidiImportText.Report(Result.Report, _chordGateOffs.IsChecked == true);
            _report.Foreground = Body;
            _import.IsEnabled = true;
        }
        catch (MidiImportException ex)
        {
            Result = null;
            _report.Text = ex.Message;
            _report.Foreground = Warning;
            _import.IsEnabled = false;
        }
    }

    private MidiImportOptions Options(IReadOnlyList<VoiceImport> voices) => new(
        voices, RowsPerBeat, Value(_speed), Value(_fromBar) - 1, Value(_toBar) - Value(_fromBar) + 1,
        UseBank: _useBank.IsChecked == true, Bank: _bank, KeepVibrato: _keepVibrato.IsChecked == true,
        ChordGateOffs: _chordGateOffs.IsChecked == true);

    // ------------------------------------------------------------------------------------ sounds

    /// <summary>The sound bank's cards - the DAW's own picker - under the voice's button: a click
    /// plays a sound, a double-click or its + makes it the voice's instrument.</summary>
    private void ShowSoundBank(int voice, Button button)
    {
        var picker = new SoundBankPicker(_bank.All.ToList())
        {
            Hint = $"Click to hear  ·  double-click or + to play voice {voice + 1} on it",
        };
        var flyout = new Flyout { Content = picker, Placement = PlacementMode.BottomEdgeAlignedLeft };

        bool low = Role(voice) is VoiceRole.Bass or VoiceRole.DrumsAndBass;
        picker.Auditioned += sound => _audition?.Invoke(sound.Instrument,
            NoteName.Parse(low || sound.Key is "Bass" or "SynthBass" or "KickBass" ? "C-2" : "C-4"));
        picker.Picked += sound =>
        {
            flyout.Hide();
            _instrument[voice] = sound.Instrument;
            Recalculate(sourcesChanged: false);
        };
        picker.LoadRequested += () =>
        {
            flyout.Hide();
            _ = LoadInstrumentAsync(voice);
        };
        picker.FolderRequested += () =>
        {
            flyout.Hide();
            InstrumentListHeader.OpenBankFolder();
        };

        flyout.ShowAt(button);
    }

    private async Task LoadInstrumentAsync(int voice)
    {
        var options = new FilePickerOpenOptions
        {
            Title = $"Instrument for voice {voice + 1}",
            FileTypeFilter = [new FilePickerFileType("MagSID instruments") { Patterns = ["*.json"] }, FilePickerFileTypes.All],
        };
        if (EditorSettings.InstrumentDirectory is { Length: > 0 } remembered)
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(remembered);

        var files = await StorageProvider.OpenFilePickerAsync(options);
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        try
        {
            _instrument[voice] = InstrumentFile.Read(File.ReadAllText(path));
            EditorSettings.InstrumentDirectory = Path.GetDirectoryName(path) ?? "";
            Recalculate(sourcesChanged: false);
        }
        catch (Exception ex) when (ex is InstrumentFileException or IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(this, "Cannot load instrument", ex.Message);
        }
    }

    // ------------------------------------------------------------------------------------ helpers

    private static ComboBox Combo(params string[] items) => new()
    {
        ItemsSource = items,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static NumericUpDown Number(int minimum, int maximum) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = minimum,
        Increment = 1,
        FormatString = "0",
        Width = 110,
    };

    private static TextBlock Caption(string text, IBrush brush) => new()
    {
        Text = text,
        Foreground = brush,
        Margin = new Thickness(0, 8, 0, 2),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
