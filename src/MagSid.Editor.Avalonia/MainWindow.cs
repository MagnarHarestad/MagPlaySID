using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using MagSid.Core.Midi;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Core.SidImport;
using MagSid.Core.Validation;
using MagSid.Editor.App;
using MagSid.Editor.App.Controls;
using MagSid.Editor.App.Daw;
using MagSid.Editor.App.Midi;
using MagSid.Editor.Avalonia.Player;
using MagSid.Midi;
using MagSid.Playback;
using MagSid.Sid;
using AvColor = Avalonia.Media.Color;
using WinKeys = System.Windows.Forms.Keys;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The editor's window on every system: the Retro view - the instrument list and the tracker grid -
/// and the DAW view, with the song's file, history and playback around them. The grid and the DAW
/// are the Windows editor's own TrackerGridControl and DawView, compiled against the WinForms face
/// in MagSid.Editor.Views; what is here is the part MainForm does there, rebuilt in Avalonia:
/// menus, transport, and wiring the views' events to the session and the engines.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly IBrush WindowBackground = new SolidColorBrush(AvColor.FromRgb(0x10, 0x10, 0x20));
    private static readonly IBrush Field = new SolidColorBrush(AvColor.FromRgb(0x1C, 0x1C, 0x30));
    private static readonly IBrush Body = new SolidColorBrush(AvColor.FromRgb(0xD2, 0xD2, 0xDE));
    private static readonly IBrush Dim = new SolidColorBrush(AvColor.FromRgb(0x8A, 0x8A, 0xA8));
    private static readonly IBrush Accent = new SolidColorBrush(AvColor.FromRgb(0x66, 0xCC, 0xFF));
    private static readonly FontFamily Mono = new("Consolas, Menlo, DejaVu Sans Mono, monospace");

    private readonly EditorSession _session = new();
    private readonly TrackerGridControl _grid = new();
    private readonly DawView _daw = new();
    private readonly ListBox _instruments = new();
    private readonly InstrumentEditorView _instrumentEditor = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _songLength = new();
    private readonly TextBlock _editMode = new();
    private readonly Button _play = new();
    private readonly Button _chip = new();
    private readonly NumericUpDown _speed = new();

    // Funk speed: every second row lasts this speed instead. The same value as Song Speed is no
    // funk at all - which is what it shows, and follows the speed, until it is set to another.
    private readonly NumericUpDown _funkSpeed = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(15) };

    private ChipModel _chipModel = ChipModel.Mos6581;
    private SongPlaybackEngine? _engine;
    private IAudioOutput? _output;
    private IAudioOutput? _previewOutput;
    private SongPlaybackEngine? _previewEngine;
    private JamEngine? _jam;
    private SongSequencer.SequencerPosition? _resumeFrom;
    private (int Position, int Row)? _resumeCursor;
    private bool _refreshing;
    private Control _retroView = null!;
    private MenuItem _viewToggle = null!;
    private bool _dawShowing;
    private MenuItem _midiMenu = null!;
    private MidiInput? _midi;
    private MidiKnobBindings? _midiBindings;

    /// <summary>The piano keys held down in the DAW, so a key's repeat does not retrigger it and its
    /// release finds the note it started.</summary>
    private readonly Dictionary<WinKeys, NoteName> _dawHeldKeys = [];

    /// <summary>The take's punch-in and punch-out, in song frames: notes are written between them.</summary>
    private (int In, int Out) _recordWindow = (0, int.MaxValue);

    /// <summary>The song frame heard at the last poll - a step back means Cycle jumped.</summary>
    private int _lastHeardFrame;

    public MainWindow(string? songPath = null)
    {
        Title = "MagSID Editor";
        Width = 1280;
        Height = 820;
        MinWidth = 900;
        MinHeight = 520;
        Background = WindowBackground;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = BuildLayout();
        WireEvents();
        RefreshAll();

        // The transport keys work wherever focus is - tunnelling, so they arrive before the grid
        // or a list would take them.
        AddHandler(KeyDownEvent, OnTransportKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnDawKeyUp, RoutingStrategies.Tunnel);

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            StopPlayback();
            StopPatternPreview();
            _jam?.Dispose();
            _midi?.Dispose();
        };

        Opened += (_, _) =>
        {
            ReopenMidi();
            if (songPath is not null)
                Open(songPath);
            _grid.Focus();
        };
    }

    // ------------------------------------------------------------------------------------ layout

    private Control BuildLayout()
    {
        var menu = BuildMenu();

        _play.Content = "▶ Play";
        _play.Click += (_, _) =>
        {
            if (_output is not null)
                StopTransport();
            else
                StartFromBeginning();
            _grid.Focus();
        };

        _speed.Minimum = 0;
        _speed.Maximum = 31;
        _speed.Width = 110;
        _speed.FormatString = "0";
        _speed.ValueChanged += (_, e) =>
        {
            if (_refreshing || e.NewValue is not { } value)
                return;
            _session.Song.Speed = (int)value;
            if (_session.Song.FunkSpeed is null)
            {
                _refreshing = true;
                _funkSpeed.Value = value;
                _refreshing = false;
            }
            else if (_session.Song.FunkSpeed == _session.Song.Speed)
            {
                _session.Song.FunkSpeed = null;
            }
            _session.MarkDirty();
        };

        _funkSpeed.Minimum = 0;
        _funkSpeed.Maximum = 31;
        _funkSpeed.Width = 110;
        _funkSpeed.FormatString = "0";
        ToolTip.SetTip(_funkSpeed, "Funk speed: every second row lasts this speed instead - a swing, as " +
                                   "GoatTracker's funktempo. The same as Song Speed: no funk. Costs 6 bytes.");
        _funkSpeed.ValueChanged += (_, e) =>
        {
            if (_refreshing || e.NewValue is not { } value)
                return;
            _session.Song.FunkSpeed = (int)value == _session.Song.Speed ? null : (int)value;
            _session.MarkDirty();
            RefreshStatus();
        };

        _editMode.VerticalAlignment = VerticalAlignment.Center;
        _editMode.FontFamily = Mono;
        _editMode.Cursor = new Cursor(StandardCursorType.Hand);
        _editMode.PointerPressed += (_, _) =>
        {
            _grid.ToggleEditMode();
            _grid.Focus();
        };

        _chip.Click += (_, _) =>
        {
            _chipModel = _chipModel == ChipModel.Mos6581 ? ChipModel.Mos8580 : ChipModel.Mos6581;
            _daw.ChipModel = _chipModel;
            _jam?.Dispose();
            _jam = null;
            UpdateChipButton();
            if (_output is not null)
            {
                var resume = _engine?.PositionAt(_output.SamplesPlayed);
                StopPlayback();
                StartPlaybackFrom(resume);
            }
            _grid.Focus();
        };
        UpdateChipButton();

        _songLength.VerticalAlignment = VerticalAlignment.Center;
        _songLength.Foreground = Dim;
        _songLength.FontFamily = Mono;

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(8, 4),
            Children =
            {
                _play,
                Label("Song Speed:"), _speed,
                Label("Funk:"), _funkSpeed,
                _editMode,
                _chip,
                _songLength,
            },
        };

        // The instrument list and the editor under it, left of the grid as in the Windows editor.
        _instruments.FontFamily = Mono;
        _instruments.Background = Field;
        _instruments.Height = 16 * 22 + 4;
        _instruments.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 2)),
                new Setter(MinHeightProperty, 22.0),
                new Setter(HeightProperty, 22.0),
            },
        });
        var listHeader = BuildInstrumentListHeader();

        _instrumentEditor.Edited += () =>
        {
            _session.MarkDirty();
            RefreshInstrumentNames();
        };

        var left = new DockPanel { Width = 420, Margin = new Thickness(8, 4, 8, 8) };
        DockPanel.SetDock(listHeader, Dock.Top);
        DockPanel.SetDock(_instruments, Dock.Top);
        left.Children.Add(listHeader);
        left.Children.Add(_instruments);
        left.Children.Add(new ScrollViewer { Content = _instrumentEditor, Margin = new Thickness(0, 8, 0, 0) });

        // The grid's columns are fixed pixel widths; it keeps its own width, left-aligned.
        _grid.Width = TrackerGridControl.ContentWidth;
        var gridHost = new Border
        {
            Child = _grid,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 8, 8),
        };

        var retro = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        retro.Children.Add(left);
        retro.Children.Add(gridHost);
        _retroView = retro;

        // Both views fill the same place; only one is ever visible, and both stay alive so
        // selection and scroll survive a round trip.
        _daw.IsVisible = false;
        _daw.ChipModel = _chipModel;
        var centre = new Panel { Children = { retro, _daw } };

        _status.Foreground = Dim;
        _status.Margin = new Thickness(8, 2);

        var root = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(menu);
        root.Children.Add(toolbar);
        root.Children.Add(_status);
        root.Children.Add(centre);
        return root;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Body,
    };

    private Menu BuildMenu()
    {
        MenuItem Item(string header, Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            if (gesture is not null)
                item.InputGesture = KeyGesture.Parse(gesture);
            return item;
        }

        string command = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";

        var file = new MenuItem
        {
            Header = "_File",
            ItemsSource = new Control[]
            {
                Item("New", () => _ = NewAsync(), $"{command}+N"),
                Item("Open...", () => _ = OpenAsync(), $"{command}+O"),
                Item("Import MIDI...", () => _ = ImportMidiAsync()),
                Item("Import SID...", () => _ = ImportSidAsync()),
                Item("Save", () => _ = SaveAsync(saveAs: false), $"{command}+S"),
                Item("Save As...", () => _ = SaveAsync(saveAs: true), $"{command}+Shift+S"),
                new Separator(),
                Item("Load Instrument...", () => _ = LoadInstrumentsAsync()),
                Item("Save Instrument...", () => _ = SaveInstrumentAsync()),
                new Separator(),
                Item("Export to .sid...", () => _ = new SidExportWindow(_session.Song, _session.Name ?? "Untitled", _chipModel).ShowDialog(this), "F9"),
                Item("Load and Play .sid...", () => new SidPlayerWindow().Show()),
                new Separator(),
                Item("Quit", Close),
            },
        };

        var edit = new MenuItem
        {
            Header = "_Edit",
            ItemsSource = new Control[]
            {
                Item("Undo", () => StepHistory(undo: true), $"{command}+Z"),
                Item("Redo", () => StepHistory(undo: false), $"{command}+Y"),
                new Separator(),
                Item("Optimize...", () => _ = OptimizeAsync()),
            },
        };

        var play = new MenuItem
        {
            Header = "_Play",
            ItemsSource = new Control[]
            {
                Item("Play from the top", StartFromBeginning, "F1"),
                Item("Play from the cursor", StartFromCurrentPosition, "F2"),
                Item("Loop the patterns at the cursor", PlayCurrentPattern, "F3"),
                Item("Stop", StopAny, "F4"),
                new Separator(),
                Item("Edit / Jam", () => _grid.ToggleEditMode(), "F5"),
            },
        };

        _viewToggle = new MenuItem { Header = "_DAW" };
        _viewToggle.Click += (_, _) => ToggleView();

        // Listed afresh each time it opens, so a keyboard plugged in mid-session shows up.
        _midiMenu = new MenuItem { Header = "_MIDI" };
        _midiMenu.Items.Add(new MenuItem { Header = "No MIDI inputs found", IsEnabled = false });
        _midiMenu.SubmenuOpened += (_, _) => BuildMidiMenu();

        return new Menu { ItemsSource = new[] { file, edit, play, _midiMenu, _viewToggle } };
    }

    // ------------------------------------------------------------------------------------ wiring

    private void WireEvents()
    {
        _session.StructureChanged += RefreshAll;
        WireInstrumentList();
        _session.DirtyChanged += RefreshStatus;
        _session.HistoryChanged += RefreshStatus;

        _grid.Edited += () => _session.MarkDirty();
        _grid.NoteEntered += PreviewNote;
        _grid.NoteReleased += ReleaseJamNote;
        _grid.InstrumentStepRequested += StepSelectedInstrument;
        _grid.JamSilenceRequested += () => _jam?.SilenceAll();
        _grid.EditModeChanged += UpdateEditMode;
        _grid.Notice += ShowNotice;
        _grid.SelectedInstrumentNumber = () =>
            SelectedInstrument?.Number ?? _session.Song.Instruments.FirstOrDefault()?.Number ?? 1;
        _grid.AudioClockSamples = () => _output?.SamplesPlayed ?? 0;
        _grid.ChannelMuteChanged += (channel, muted) =>
        {
            _engine?.SetVoiceMuted(channel, muted);
            _previewEngine?.SetVoiceMuted(channel, muted);
        };
        UpdateEditMode();

        WireDaw();

        _poll.Tick += (_, _) => DrainPlaybackEvents();
    }

    /// <summary>The DAW's events, as MainForm wires them: edits go through the session, and the
    /// tracker is told so the two views never disagree about the song.</summary>
    private void WireDaw()
    {
        _daw.InstrumentEdited += _ =>
        {
            _session.MarkDirty();
            RefreshInstrumentNames();
        };

        // A click in the piano roll is a short tap: a fixed release, whatever the instrument's own.
        _daw.NotePreviewRequested += (note, instrument) => PreviewNote(note, instrument, RollPreviewRelease);
        _daw.NotePreviewReleased += ReleaseJamNote;
        _daw.CycleChanged += () => ApplyCycle(_engine);
        _daw.RecordToggled += on =>
        {
            if (on)
                StartRecording();
            else
                StopAny();
        };
        _daw.MuteToggleRequested += channel => _grid.ToggleChannelMute(channel);
        _daw.IsChannelMuted = _grid.IsChannelMuted;
        _daw.Notice += ShowNotice;

        _daw.InstrumentAddRequested += AddInstrument;
        _daw.InstrumentFromBankRequested += AddFromBank;
        _daw.InstrumentSoundChangeRequested += ChangeSound;
        _daw.InstrumentLoadRequested += () => _ = LoadInstrumentsAsync();
        _daw.SoundAuditionRequested += AuditionSound;
        _daw.InstrumentCopyRequested += instrument => _session.CopyInstrument(instrument);
        _daw.InstrumentDeleteRequested += instrument => _session.DeleteInstrument(instrument);

        _daw.PatternEdited += SongEditedInDaw;
        _daw.OrderEdited += SongEditedInDaw;
    }

    private void SongEditedInDaw()
    {
        _session.MarkDirty();
        _grid.SetSong(_session.Song);
        RefreshStatus();
    }

    /// <summary>Swaps the centre between the tracker and the DAW. Both edit the same Song, so there
    /// is nothing to convert; the selected instrument is carried across.</summary>
    private void ToggleView()
    {
        _dawShowing = !_dawShowing;
        _dawHeldKeys.Clear();

        if (_dawShowing)
        {
            _daw.LoadSong(_session.Song);
            if (SelectedInstrument is { } selected)
                _daw.SelectInstrument(selected.Number);
        }
        else if (_daw.SelectedInstrumentNumber is { } number)
        {
            SelectInstrumentNumber(number);
        }

        _retroView.IsVisible = !_dawShowing;
        _daw.IsVisible = _dawShowing;
        _viewToggle.Header = _dawShowing ? "_Retro" : "_DAW";

        if (_dawShowing)
            _daw.Focus();
        else
            _grid.Focus();
    }

    private void OnTransportKey(object? sender, KeyEventArgs e)
    {
        // Typing in a field keeps its keys.
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        switch (e.Key)
        {
            case Key.F1:
                StartFromBeginning();
                break;
            case Key.F2:
                // In the DAW the project cursor says where, and F2 goes back to it while playing too.
                if (_dawShowing)
                    PlayFromProjectCursor();
                else
                    StartFromCurrentPosition();
                break;
            case Key.F3:
                PlayCurrentPattern();
                break;
            case Key.F4:
                // Stopped already: a second stop takes the DAW's project cursor back to where
                // playback last started, as Cubase does.
                if (_output is null && _previewOutput is null && _dawShowing)
                    _daw.ReturnToPlayStart();
                else
                    StopAny();
                break;
            case Key.F9:
                _ = new SidExportWindow(_session.Song, _session.Name ?? "Untitled", _chipModel).ShowDialog(this);
                break;
            case Key.F5:
            case Key.Space when e.KeyModifiers == KeyModifiers.None:
                _grid.ToggleEditMode();
                break;
            default:
                if (_dawShowing)
                    OnDawKeyDown(e);
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// The piano keys, for the DAW view - caught at the window, before whatever has focus, as
    /// MainForm does with KeyPreview: the tracker grid that owns them in Retro is hidden here.
    /// Routed through the same path a MIDI key takes, so Edit mode decides whether the note is
    /// written into the roll and Jam mode only sounds it.
    /// </summary>
    private void OnDawKeyDown(KeyEventArgs e)
    {
        // The arrange lanes take digits as pattern numbers, and a selected LE in the roll takes
        // them as its loop count; 2, 3, 5, 6 and 7 would otherwise be piano keys.
        var modifiers = System.Windows.Forms.KeyMap.Modifiers(e.KeyModifiers);
        if ((modifiers & (WinKeys.Control | WinKeys.Alt)) != 0 || _daw.ArrangeHasFocus)
            return;

        var key = System.Windows.Forms.KeyMap.From(e);
        if (_daw.RollTakesDigits && key is >= WinKeys.D0 and <= WinKeys.D9 or >= WinKeys.NumPad0 and <= WinKeys.NumPad9)
            return;

        bool shift = (modifiers & WinKeys.Shift) != 0;

        // Numpad /, Cubase's Cycle key.
        if (key == WinKeys.Divide)
        {
            _daw.CycleOn = !_daw.CycleOn;
            e.Handled = true;
            return;
        }

        if (key == PianoKeyMap.OctaveKey)
        {
            _daw.StepOctave(shift);
            e.Handled = true;
            return;
        }

        // Shift+C and Shift+V are the roll's own copy and paste.
        if (shift && key is WinKeys.C or WinKeys.V)
            return;

        if (PianoKeyMap.TryNote(key, _daw.Octave) is not { } note)
            return;

        // A held key repeats; the note is already sounding.
        if (_dawHeldKeys.TryAdd(key, note))
            PlayDawNote(note);

        e.Handled = true;
    }

    private void OnDawKeyUp(object? sender, KeyEventArgs e)
    {
        if (_dawHeldKeys.Remove(System.Windows.Forms.KeyMap.From(e), out var note))
        {
            ReleaseDawNote(note);
            e.Handled = true;
        }
    }

    /// <summary>A piano key in the DAW. It always sounds; it is written down only by a take, a
    /// chord being recorded, or in Edit mode with the roll focused - see MainForm.OnMidiNote.</summary>
    private void PlayDawNote(NoteName note)
    {
        int instrument = _daw.SelectedInstrumentNumber ?? SelectedInstrument?.Number ?? 1;

        if (_daw.IsRecording)
            _daw.RecordNote(note, instrument);
        else if (_daw.IsRecordingChord)
            _daw.RecordChordNote(note);
        else if (_grid.EditMode && _daw.RollHasFocus)
            _daw.StepInput(note);

        PreviewNote(note, instrument);
    }

    private void ReleaseDawNote(NoteName note)
    {
        if (_daw.IsRecording)
            _daw.RecordRelease(note);
        _daw.RecordChordRelease(note);
        ReleaseJamNote(note);
    }

    private Instrument? SelectedInstrument =>
        _instruments.SelectedIndex is var index and >= 0 && index < _session.Song.Instruments.Count
            ? _session.Song.Instruments[index]
            : null;

    private void RefreshAll()
    {
        _refreshing = true;
        try
        {
            _grid.SetSong(_session.Song);
            // Undo restores a whole-song snapshot, so the DAW is pointed at the new object too.
            _daw.LoadSong(_session.Song);
            _speed.Value = _session.Song.Speed;
            _funkSpeed.Value = _session.Song.FunkSpeed ?? _session.Song.Speed;
            RefreshInstrumentNames();
            ShowSelectedInstrument();
            RefreshStatus();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ShowSelectedInstrument() => _instrumentEditor.Load(SelectedInstrument);

    private void RefreshStatus()
    {
        string name = _session.FilePath is { } path ? Path.GetFileName(path) : _session.Name ?? "New Song";
        Title = $"MagSID Editor - {name}{(_session.IsDirty ? " *" : "")}";
        _status.Text = $"{_session.FilePath ?? "(unsaved)"}   ·   {(_session.IsDirty ? "Modified" : "Saved")}";

        try
        {
            int bytes = MagSid.Core.SongSizeCalculator.Measure(_session.Song.WithUnusedPatternsRemoved()).TotalBytes;
            _songLength.Text = $"Song Length: ${bytes:X4} ({bytes} bytes)";
        }
        catch (Exception)
        {
            _songLength.Text = "";
        }
    }

    private void UpdateEditMode()
    {
        _editMode.Text = _grid.EditMode ? "● EDIT" : "● JAM";
        _editMode.Foreground = _grid.EditMode
            ? new SolidColorBrush(AvColor.FromRgb(0xFF, 0x66, 0x66))
            : new SolidColorBrush(AvColor.FromRgb(0x66, 0xFF, 0x99));
    }

    private void UpdateChipButton() => _chip.Content = $"SID: {(_chipModel == ChipModel.Mos6581 ? "6581" : "8580")}";

    private void ShowNotice(string text) => _status.Text = text;

    private void StepHistory(bool undo)
    {
        StopTransport();
        if (undo)
            _session.Undo();
        else
            _session.Redo();
    }

    // ------------------------------------------------------------------------------------ instruments

    private void AddInstrument()
    {
        if (_session.AddInstrument() is not { } added)
        {
            ShowInstrumentLimit();
            return;
        }

        SelectInstrumentNumber(added.Number);
        if (_dawShowing)
            _daw.RenameSelectedInstrument();
        else
            _instrumentEditor.FocusName();
    }

    /// <summary>A copy of one of the GM bank's sounds, as it is in the bank, added after the
    /// song's own instruments.</summary>
    private void AddFromBank(GmBank.Sound sound)
    {
        var instrument = InstrumentFile.Read(InstrumentFile.Write(sound.Instrument));
        if (_session.ImportInstrument(instrument) is not { } added)
        {
            ShowInstrumentLimit();
            return;
        }

        SelectInstrumentNumber(added.Number);
        ShowNotice($"Added {added.Name} from the sound bank ({sound.Origin})");
    }

    private static readonly FilePickerFileType InstrumentFiles = new("MagSID instruments") { Patterns = ["*.json"] };

    private async Task LoadInstrumentsAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Load instrument",
            AllowMultiple = true,
            FileTypeFilter = [InstrumentFiles, FilePickerFileTypes.All],
        };
        if (EditorSettings.InstrumentDirectory is { Length: > 0 } remembered)
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(remembered);

        var files = await StorageProvider.OpenFilePickerAsync(options);
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0)
            return;

        EditorSettings.InstrumentDirectory = Path.GetDirectoryName(paths[0]) ?? "";

        var problems = new List<string>();
        Instrument? last = null;
        foreach (string path in paths)
        {
            string file = Path.GetFileName(path);
            Instrument loaded;
            try
            {
                loaded = InstrumentFile.Read(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is InstrumentFileException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{file}: {ex.Message}");
                continue;
            }

            if (_session.ImportInstrument(loaded) is null)
            {
                problems.Add($"{file}: the song already has all {SongValidator.MaxInstrumentNumber} instruments.");
                break;
            }
            last = loaded;
        }

        if (last is not null)
            SelectInstrumentNumber(last.Number);

        if (problems.Count > 0)
            await MessageWindow.ShowAsync(this, "Some instruments were not loaded", string.Join(Environment.NewLine + Environment.NewLine, problems));
    }

    /// <summary>Selects the instrument in both views' lists.</summary>
    private void SelectInstrumentNumber(int number)
    {
        int index = _session.Song.Instruments.FindIndex(i => i.Number == number);
        if (index >= 0)
            _instruments.SelectedIndex = index;
        _daw.SelectInstrument(number);
    }

    private void ShowInstrumentLimit() =>
        ShowNotice($"This song already has all {SongValidator.MaxInstrumentNumber} instruments a pattern can select.");

    private void DeleteInstrument()
    {
        if (SelectedInstrument is { } instrument)
            _session.DeleteInstrument(instrument);
    }

    private void StepSelectedInstrument(int delta)
    {
        int count = _session.Song.Instruments.Count;
        if (count == 0)
            return;

        int index = Math.Max(0, _instruments.SelectedIndex);
        _instruments.SelectedIndex = (index + delta + count) % count;
    }

    // ------------------------------------------------------------------------------------ playback

    /// <summary>The release a note clicked into the piano roll is auditioned with - a tap, so the
    /// same short tail for every instrument. As MainForm.RollPreviewRelease.</summary>
    private const int RollPreviewRelease = 0x9;

    /// <summary>The longest release an Edit-mode note is auditioned with.</summary>
    private const int MaxEditPreviewRelease = 0xA;

    private void PreviewNote(NoteName note, int instrumentNumber) => PreviewNote(note, instrumentNumber, null);

    /// <param name="forcedRelease">A release nibble to audition with in place of the
    /// instrument's own, or null for the usual: capped in Edit mode only.</param>
    private void PreviewNote(NoteName note, int instrumentNumber, int? forcedRelease)
    {
        var instrument = _session.Song.Instruments.FirstOrDefault(i => i.Number == instrumentNumber);
        if (instrument is null)
            return;

        if (forcedRelease is { } release)
            instrument = WithRelease(instrument, release);
        else if (_grid.EditMode && (instrument.SustainRelease & 0x0F) > MaxEditPreviewRelease)
            instrument = WithRelease(instrument, MaxEditPreviewRelease);

        try
        {
            _jam ??= new JamEngine(_chipModel);
            _jam.Start();
            _jam.NoteOn(note, instrument);
            _daw.SetNoteSounding(note, true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException)
        {
            _jam?.Dispose();
            _jam = null;
        }
    }

    private void ReleaseJamNote(NoteName note)
    {
        _jam?.NoteOff(note);
        _daw.SetNoteSounding(note, false);
    }

    /// <summary>A copy with the release nibble replaced; the song's own instrument is never touched.</summary>
    private static Instrument WithRelease(Instrument instrument, int release)
    {
        if ((instrument.SustainRelease & 0x0F) == release)
            return instrument;

        var copy = InstrumentFile.Read(InstrumentFile.Write(instrument));
        copy.Number = instrument.Number;
        copy.SustainRelease = (byte)((instrument.SustainRelease & 0xF0) | (release & 0x0F));
        return copy;
    }

    /// <summary>Plays a sound that is not in the song yet - the sound bank's cards - for half a second.</summary>
    private void AuditionSound(Instrument instrument, NoteName note)
    {
        try
        {
            _jam ??= new JamEngine(_chipModel);
            _jam.Start();
            _jam.ResetFilter();
            _jam.NoteOn(note, instrument);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException)
        {
            _jam?.Dispose();
            _jam = null;
            return;
        }

        var release = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        release.Tick += (_, _) =>
        {
            release.Stop();
            _jam?.NoteOff(note);
        };
        release.Start();
    }

    private void StartFromBeginning()
    {
        _daw.ResetProjectCursor();
        StopTransport();
        _resumeFrom = null;
        _resumeCursor = null;
        _grid.SetSongPosition(0);
        StartFromCurrentPosition();
    }

    private void StartFromCurrentPosition()
    {
        if (_output is not null)
            return;
        StartPlaybackFrom(ResumePosition());
    }

    /// <summary>Where the next play begins: exactly where it stopped while the cursor has not
    /// moved since, otherwise the cursor's row - found by running the song silently up to it, so
    /// every channel is where the song really has it. See MainForm.ResumePosition.</summary>
    private SongSequencer.SequencerPosition? ResumePosition()
    {
        if (_resumeFrom is not null && _resumeCursor is { } parked &&
            parked.Position == _grid.SongPosition && parked.Row == _grid.CursorRow)
            return _resumeFrom;

        if (_grid.CursorSongLocation() is not { } target || (target.OrderIndex == 0 && target.RowIndex == 0))
            return null;

        return target.TicksInto >= 0
            ? SongSequencer.FindPositionInEntry(_session.Song, target.Channel, target.OrderIndex, target.TicksInto)
            : SongSequencer.FindPosition(_session.Song, target.Channel, target.OrderIndex, target.RowIndex);
    }

    /// <summary>
    /// Cycle, when it is on in the DAW and the locators are set: the engine goes on from L each
    /// time the song reaches R. Cleared otherwise. As MainForm.ApplyCycle.
    /// </summary>
    private void ApplyCycle(SongPlaybackEngine? engine)
    {
        if (engine is null)
            return;

        if (!_daw.CycleOn || _daw.LocatorRange is not { } range)
        {
            engine.ClearCycle();
            return;
        }

        var start = range.Left is { } left
            ? SongSequencer.FindPositionInEntry(_session.Song, left.Channel, left.Index, left.TicksInto)
            : null;
        var end = SongSequencer.FindPositionInEntry(_session.Song, range.Right.Channel, range.Right.Index, range.Right.TicksInto);
        if (end is null)
        {
            engine.ClearCycle();
            return;
        }

        engine.SetCycle(start, end.Frame);
    }

    /// <summary>The DAW's F2: plays from the project cursor - where the song last stopped, or
    /// wherever it was put in the arrange ruler. Playing already, it starts again from there.</summary>
    private void PlayFromProjectCursor()
    {
        StopAny();
        _daw.MarkPlayStart();
        StartPlaybackFrom(_daw.PlayFrom is { } at
            ? SongSequencer.FindPositionInEntry(_session.Song, at.Channel, at.Index, at.TicksInto)
            : null);
    }

    /// <summary>
    /// The piano roll's REC: plays the song from the selected clip's place in it - or the left
    /// locator with PUNCH on, a bar earlier with PRE-ROLL - and the notes played go into that
    /// clip's pattern. As MainForm.StartRecording.
    /// </summary>
    private void StartRecording()
    {
        StopAny();
        if (_session.Song.Instruments.Count == 0 || !_daw.BeginRecording() || _daw.RecordingFrom is not { } clip)
            return;

        var song = _session.Song;
        SongSequencer.SequencerPosition? punchIn;
        int punchOut = int.MaxValue;
        if (_daw.PunchOn && _daw.LocatorRange is { } range)
        {
            punchIn = range.Left is { } left ? SongSequencer.FindPositionInEntry(song, left.Channel, left.Index, left.TicksInto) : null;
            punchOut = SongSequencer.FindPositionInEntry(song, range.Right.Channel, range.Right.Index, range.Right.TicksInto)?.Frame ?? int.MaxValue;
        }
        else
        {
            punchIn = clip.Index == 0 ? null : SongSequencer.FindPositionInEntry(song, clip.Channel, clip.Index, 0);
            if (clip.Index > 0 && punchIn is null)
            {
                _daw.EndRecording();
                return;
            }
        }

        // A bar is 16 rows, as the piano roll counts; a beat is 4.
        int framesPerBeat = (int)Math.Round(4 * song.AverageFramesPerRow);
        int punchInFrame = punchIn?.Frame ?? 0;
        var start = _daw.PreRollOn && punchInFrame > 0
            ? SongSequencer.PositionAtFrame(song, Math.Max(0, punchInFrame - (4 * framesPerBeat)))
            : punchIn;

        _recordWindow = (punchInFrame, punchOut);
        _daw.SetRecordWindow(false);
        _resumeFrom = null;
        _resumeCursor = null;
        StartPlaybackFrom(start);
        if (_output is null || _engine is null)
        {
            _daw.EndRecording();
            return;
        }

        if (_daw.ClickOn && _daw.PreRollOn && start is not null)
            _engine.SetClick(start.Frame, punchInFrame, framesPerBeat);
        else if (_daw.ClickOn)
            _engine.SetCountIn(4 * framesPerBeat, framesPerBeat);
    }

    /// <summary>F4: stops whichever is running, the song or an F3 loop.</summary>
    private void StopAny()
    {
        if (_previewOutput is not null)
            StopPatternPreview();
        else
            StopTransport();
    }

    /// <summary>
    /// F3: loops what is on screen - each channel's pattern at the cursor's row, from that pattern's
    /// own start - so a sound can be worked on without playing the song up to it. A channel whose
    /// order does not reach the row plays a silent row. As MainForm.PlayCurrentPattern.
    /// </summary>
    private void PlayCurrentPattern()
    {
        if (_session.Song.Instruments.Count == 0)
            return;

        StopTransport();
        StopPatternPreview();
        _jam?.Dispose();
        _jam = null;

        var patterns = _grid.CurrentPatternsAtCursor();
        if (Array.TrueForAll(patterns, p => p is null))
            return;

        var preview = new Song { Speed = _session.Song.Speed, FunkSpeed = _session.Song.FunkSpeed };
        preview.Instruments.AddRange(_session.Song.Instruments);
        var seen = new HashSet<int>();
        foreach (var pattern in patterns)
        {
            if (pattern is not null && seen.Add(pattern.Number))
                preview.Patterns.Add(pattern);
        }

        var silent = new Pattern { Number = preview.Patterns.Max(p => p.Number) + 1 };
        silent.Rows.Add(new HoldRow());
        preview.Patterns.Add(silent);
        for (int ch = 0; ch < 3; ch++)
            preview.ChannelOrders[ch].Tokens.Add(new PatternRefToken(patterns[ch]?.Number ?? silent.Number));

        try
        {
            var engine = new SongPlaybackEngine(preview, _chipModel);
            for (int ch = 0; ch < 3; ch++)
                engine.SetVoiceMuted(ch, _grid.IsChannelMuted(ch));
            _previewEngine = engine;
            _previewOutput = AudioOutput.Start(engine, 120);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException or ArgumentException)
        {
            StopPatternPreview();
            _ = MessageWindow.ShowAsync(this, "Cannot play pattern", ex.Message);
        }
    }

    private void StopPatternPreview()
    {
        var output = _previewOutput;
        _previewOutput = null;
        _previewEngine = null;
        output?.Stop();
        output?.Dispose();
    }

    private void StartPlaybackFrom(SongSequencer.SequencerPosition? resume)
    {
        StopPatternPreview();
        try
        {
            _jam?.Dispose();
            _jam = null;

            var engine = new SongPlaybackEngine(_session.Song, _chipModel, resume);
            ApplyCycle(engine);
            for (int ch = 0; ch < 3; ch++)
                engine.SetVoiceMuted(ch, _grid.IsChannelMuted(ch));

            var output = AudioOutput.Open!(engine, 120);
            output.Stopped += _ => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_output, output))
                    StopPlayback();
            });

            _engine = engine;
            _output = output;
            output.Play();

            _grid.SetPlaybackActive(true);
            _grid.SetPlaybackStartEntries(
                resume is null ? null : [.. resume.Channels.Select(c => c.PlayingEntry)],
                resume is null ? null : SongSequencer.TicksIntoEntries(_session.Song, resume));
            _poll.Start();
            _play.Content = "■ Stop";
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException or ArgumentException)
        {
            StopPlayback();
            _ = MessageWindow.ShowAsync(this, "Cannot start playback", ex.Message);
        }
    }

    /// <summary>F4: parks the cursor where playback reached and keeps that as F2's resume point.</summary>
    private void StopTransport()
    {
        if (_output is null)
            return;

        _resumeFrom = _engine?.PositionAt(_output.SamplesPlayed);
        _grid.JumpEditCursorToLivePlaybackPosition();
        StopPlayback();
        _resumeCursor = (_grid.SongPosition, _grid.CursorRow);
    }

    private void StopPlayback()
    {
        var output = _output;
        _output = null;
        _engine = null;
        _poll.Stop();
        output?.Stop();
        output?.Dispose();

        // A take ends with the song: a held key is let go where it got to, and the take becomes
        // one edit - one undo step.
        if (_daw.IsRecording)
            _daw.EndRecording();
        _grid.SetPlaybackActive(false);
        _daw.ClearPlayback();
        _play.Content = "▶ Play";
    }

    private void DrainPlaybackEvents()
    {
        if (_engine is not { } engine)
            return;

        while (engine.TryDequeueRowEvent(out var row))
            _grid.SetChannelPlaybackAnchor(row.Channel, row.PatternNumber, row.RowIndex, row.OrderEntrySequence, row.SampleIndex);

        if (_dawShowing && _output is { } output)
            DrainToDaw(engine, output.SamplesPlayed);
    }

    /// <summary>The DAW's views follow the clock the listener hears, as in MainForm: the clips'
    /// playheads, the roll's row, the record window and the instrument page's program steps.</summary>
    private void DrainToDaw(SongPlaybackEngine engine, long heard)
    {
        if (engine.PositionAt(heard, out long capturedAt) is { Channels.Count: 3 } position)
        {
            _daw.SetPlayheads(
                position.Channels[0].PlayingEntry, position.Channels[1].PlayingEntry, position.Channels[2].PlayingEntry);

            double subFrame = (heard - capturedAt) / SongPlaybackEngine.SamplesPerFrame;
            int speed = position.TickSpeed(_session.Song);

            var at = new (int Pattern, int Row, double Fraction, double Ticks)[3];
            var lanes = new (int Entry, double Ticks)?[3];
            for (int ch = 0; ch < 3; ch++)
            {
                var channel = position.Channels[ch];
                var (row, fraction) = PlayheadPosition.Of(channel, position.SpeedCounter, speed, subFrame);
                double ticks = PlayheadPosition.TicksIntoEntry(channel, position.SpeedCounter, speed, subFrame);
                at[ch] = (channel.CurrentPatternNumber, row, fraction, ticks);
                lanes[ch] = (channel.PlayingEntry, ticks);
            }

            _daw.SetPlayRow(at[0], at[1], at[2]);
            _daw.SetSongPositions(lanes);

            if (_daw.IsRecording)
            {
                // Cycle took the song back from R to L: a punch-out and a punch-in at once.
                if (position.Frame < _lastHeardFrame)
                    _daw.SetRecordWindow(false);
                _daw.SetRecordWindow(position.Frame >= _recordWindow.In && position.Frame < _recordWindow.Out);
            }

            _lastHeardFrame = position.Frame;
        }

        if (_daw.SelectedInstrumentNumber is { } selected and > 0)
        {
            engine.PlayheadAt(heard, selected, out int wave, out int arp, out int filter);
            _daw.SetProgramPlayheads(wave, arp, filter);
            _daw.SetEnvelopePlayhead(engine.EnvelopeAt(heard, selected, out int stage, out int level) ? (stage, level) : null);
            _daw.SetCutoffPlayhead(engine.FilterAt(heard, selected, out int frame, out int cutoff) ? (frame, cutoff) : null);
        }
    }

    // ------------------------------------------------------------------------------------ MIDI

    private void BuildMidiMenu()
    {
        _midiMenu.Items.Clear();
        var devices = MidiInput.Devices();
        if (devices.Count == 0)
            _midiMenu.Items.Add(new MenuItem { Header = "No MIDI inputs found", IsEnabled = false });

        for (int i = 0; i < devices.Count; i++)
        {
            int index = i;
            var item = new MenuItem
            {
                Header = devices[i],
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _midi?.DeviceName == devices[i],
            };
            item.Click += (_, _) => OpenMidi(index, quiet: false);
            _midiMenu.Items.Add(item);
        }

        _midiMenu.Items.Add(new Separator());
        var close = new MenuItem { Header = "Close MIDI", IsEnabled = _midi?.IsOpen == true };
        close.Click += (_, _) => CloseMidi();
        _midiMenu.Items.Add(close);

        var forget = new MenuItem { Header = "Forget all knob bindings" };
        forget.Click += (_, _) =>
        {
            EditorSettings.MidiKnobBindings = string.Empty;
            _ = MessageWindow.ShowAsync(this, "MIDI", "Knob bindings cleared. They take effect next time MagSID starts.");
        };
        _midiMenu.Items.Add(forget);
    }

    /// <summary>Opens whatever was used last, by name - an input's place in the list moves when
    /// another is plugged in.</summary>
    private void ReopenMidi()
    {
        if (EditorSettings.MidiDevice is not { Length: > 0 } wanted)
            return;

        int index = MidiInput.Devices().ToList().FindIndex(name => name == wanted);
        if (index >= 0)
            OpenMidi(index, quiet: true);
    }

    private void OpenMidi(int index, bool quiet)
    {
        CloseMidi();

        var midi = new MidiInput(action => Dispatcher.UIThread.Post(action));
        if (!midi.Open(index))
        {
            midi.Dispose();
            if (!quiet)
                _ = MessageWindow.ShowAsync(this, "MIDI", "That MIDI input could not be opened. Another program usually has it.");
            return;
        }

        _midi = midi;
        EditorSettings.MidiDevice = midi.DeviceName ?? string.Empty;

        if (_midiBindings is null)
        {
            _midiBindings = new MidiKnobBindings(EditorSettings.MidiKnobBindings);
            _midiBindings.Changed += () => EditorSettings.MidiKnobBindings = _midiBindings.Save();
            foreach (var knob in _daw.Knobs())
                _midiBindings.Register(knob);
        }

        midi.ControlChanged += (controller, value) => _midiBindings.HandleControlChange(controller, value);
        midi.NotePressed += (note, _) => OnMidiNote(note);
        midi.NoteReleased += ReleaseDawNote;
        ShowNotice($"MIDI: {midi.DeviceName}");
    }

    private void CloseMidi()
    {
        _midi?.Dispose();
        _midi = null;
    }

    /// <summary>A key on the MIDI keyboard. It always sounds, and is written down only in Edit
    /// mode, in whichever view is on screen - see MainForm.OnMidiNote.</summary>
    private void OnMidiNote(NoteName note)
    {
        if (_dawShowing)
        {
            PlayDawNote(note);
            return;
        }

        // The tracker's own entry path decides whether Edit mode writes the note, and raises
        // NoteEntered, which already sounds it.
        _grid.EnterNote(note);
    }

    // ------------------------------------------------------------------------------------ optimize

    /// <summary>What every instrument's programs reach over one full pass of the song, played
    /// silently here - see MainForm.BuildInstrumentUsage.</summary>
    private Dictionary<int, MagSid.Core.InstrumentUsage> BuildInstrumentUsage()
    {
        var analyzed = SongCoverageAnalyzer.Run(_session.Song);
        var usage = new Dictionary<int, MagSid.Core.InstrumentUsage>();
        foreach (var instrument in _session.Song.Instruments)
        {
            if (instrument.Number < 0 || instrument.Number >= analyzed.Count)
                continue;

            var coverage = analyzed[instrument.Number];
            usage[instrument.Number] = new MagSid.Core.InstrumentUsage(
                HighestSet(coverage.Wavetable), HighestSet(coverage.Arpeggio), HighestSet(coverage.Filter));
        }

        return usage;

        static int HighestSet(bool[] reached) => Array.LastIndexOf(reached, true);
    }

    /// <summary>Shrinks the pattern data without changing what the song plays (SongOptimizer).
    /// Reports first and applies only on a yes; it lands on the undo stack like any edit.</summary>
    private async Task OptimizeAsync()
    {
        var usage = BuildInstrumentUsage();
        var result = MagSid.Core.SongOptimizer.Analyze(_session.Song, usage);
        string blank = Environment.NewLine + Environment.NewLine;

        if (!result.FoundAnything)
        {
            await MessageWindow.ShowAsync(this, "Optimize",
                "Nothing to change - the pattern data is already as small as loops can make it." + Environment.NewLine +
                "Every instrument's programs are fully used." + blank +
                $"Song length ${result.Before.EndAddress - 0x1000:X3}, patterns {result.Before.PatternBytes} bytes.");
            return;
        }

        var lines = result.Findings
            .Take(20)
            .Select(f => $"  PAT{f.PatternNumber:D2}  {f.Description}" + (f.BytesSaved > 0 ? $"  (-{f.BytesSaved} bytes)" : ""))
            .ToList();
        if (result.Findings.Count > lines.Count)
            lines.Add($"  ...and {result.Findings.Count - lines.Count} more");

        string summary = result.BytesSaved > 0
            ? $"Saves {result.BytesSaved} bytes: ${result.Before.EndAddress - 0x1000:X3} to ${result.After.EndAddress - 0x1000:X3}."
            : "Saves no bytes - a dead LS marker emits nothing - but the loops read as what they are.";
        const string caveat =
            "Program trims come from playing the whole song through silently, so they cover every " +
            "row of every pattern - no need to have listened to it first.";
        string undo = OperatingSystem.IsMacOS() ? "Cmd+Z" : "Ctrl+Z";

        if (!await ConfirmAsync("Optimize", summary, string.Join(Environment.NewLine, lines), caveat + blank + $"Apply? {undo} undoes it."))
            return;

        StopTransport();
        StopPatternPreview();
        MagSid.Core.SongOptimizer.Apply(_session.Song, usage);
        _session.NotifyStructureChanged();
    }

    /// <summary>A question with a monospaced list in the middle; true for Apply.</summary>
    private async Task<bool> ConfirmAsync(string title, string above, string list, string below)
    {
        bool answer = false;
        var window = new Window
        {
            Title = title,
            Width = 640,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var apply = new Button { Content = "Apply", MinWidth = 90, IsDefault = true };
        apply.Click += (_, _) =>
        {
            answer = true;
            window.Close();
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        cancel.Click += (_, _) => window.Close();

        window.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = above, TextWrapping = TextWrapping.Wrap },
                new ScrollViewer
                {
                    MaxHeight = 360,
                    Content = new TextBlock { Text = list, FontFamily = Mono, Foreground = Body },
                },
                new TextBlock { Text = below, TextWrapping = TextWrapping.Wrap, Foreground = Dim },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { apply, cancel },
                },
            },
        };
        await window.ShowDialog(this);
        return answer;
    }

    // ------------------------------------------------------------------------------------ files

    private static readonly FilePickerFileType SongFiles = new("MagSID songs") { Patterns = ["*.s"] };

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_session.IsDirty)
            return true;

        var result = await AskAsync("Unsaved changes", "The song has changes that are not saved. Save them first?");
        if (result is null)
            return false;
        return result == false || await SaveAsync(saveAs: false);
    }

    /// <summary>Makes a new song from a MIDI file - see MidiImportWindow for the choices and
    /// MidiImporter for how the notes are fitted to three voices.</summary>
    private async Task ImportMidiAsync()
    {
        if (!await ConfirmDiscardAsync())
            return;

        var options = new FilePickerOpenOptions
        {
            Title = "Import MIDI",
            FileTypeFilter = [new FilePickerFileType("MIDI files") { Patterns = ["*.mid", "*.midi"] }, FilePickerFileTypes.All],
        };
        if (EditorSettings.MidiImportDirectory is { Length: > 0 } remembered)
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(remembered);

        var files = await StorageProvider.OpenFilePickerAsync(options);
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        EditorSettings.MidiImportDirectory = Path.GetDirectoryName(path) ?? "";

        MidiSong midi;
        try
        {
            midi = MidiFileReader.Read(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is MidiFileException or IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(this, "Cannot read MIDI file", ex.Message);
            return;
        }

        if (midi.Sources().Count == 0)
        {
            await MessageWindow.ShowAsync(this, "Import MIDI", "The MIDI file has no notes.");
            return;
        }

        var result = await new MidiImportWindow(midi, Path.GetFileName(path), AuditionSound).ShowDialog<MidiImportResult?>(this);
        if (result is null)
            return;

        // The engines hold the old song; they stop before it is replaced.
        StopTransport();
        StopPatternPreview();
        _jam?.SilenceAll();
        _daw.ClearSoundingNotes();
        _session.LoadImported(result.Song, Path.GetFileNameWithoutExtension(path));
        _grid.SetSongPosition(0);
        ShowNotice($"Imported {Path.GetFileName(path)}: {result.Report.Bars} bars, {result.Report.TotalBytes} bytes");
    }

    /// <summary>Makes a new song from a .sid tune - see SidImportWindow for the choices and
    /// SidTranscriber for how what the tune does to the chip becomes notes and instruments.</summary>
    private async Task ImportSidAsync()
    {
        if (!await ConfirmDiscardAsync())
            return;

        var options = new FilePickerOpenOptions
        {
            Title = "Import SID",
            FileTypeFilter = [new FilePickerFileType("SID tunes") { Patterns = ["*.sid"] }, FilePickerFileTypes.All],
        };
        if (EditorSettings.SidOpenDirectory is { Length: > 0 } remembered)
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(remembered);

        var files = await StorageProvider.OpenFilePickerAsync(options);
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        EditorSettings.SidOpenDirectory = Path.GetDirectoryName(path) ?? "";

        SidFile file;
        try
        {
            file = SidFile.Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is SidPlaybackException or IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(this, "Cannot read .sid file", ex.Message);
            return;
        }

        if (file.UnsupportedReason() is { } reason)
        {
            await MessageWindow.ShowAsync(this, "Cannot import this .sid", reason);
            return;
        }

        // The engines hold the old song; they stop before it is replaced.
        StopTransport();
        StopPatternPreview();
        _jam?.SilenceAll();
        _daw.ClearSoundingNotes();

        var result = await new SidImportWindow(file, Path.GetFileName(path)).ShowDialog<SidImportResult?>(this);
        if (result is null)
            return;

        _session.LoadImported(result.Song, file.Name.Length > 0 ? file.Name : Path.GetFileNameWithoutExtension(path));
        _grid.SetSongPosition(0);
        ShowNotice($"Imported {Path.GetFileName(path)}: {result.Report.Rows} rows, {result.Report.TotalBytes} bytes");
    }

    private async Task NewAsync()
    {
        if (!await ConfirmDiscardAsync())
            return;
        StopTransport();
        _session.New();
    }

    private async Task OpenAsync()
    {
        if (!await ConfirmDiscardAsync())
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open song",
            AllowMultiple = false,
            FileTypeFilter = [SongFiles, FilePickerFileTypes.All],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            Open(path);
    }

    private void Open(string path)
    {
        StopTransport();
        try
        {
            _session.Load(path);
            _grid.SetSongPosition(0);
        }
        catch (Exception ex)
        {
            _ = MessageWindow.ShowAsync(this, "Cannot open song", ex.Message);
        }
    }

    private async Task<bool> SaveAsync(bool saveAs)
    {
        string? path = _session.FilePath;
        if (saveAs || path is null)
        {
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save song",
                SuggestedFileName = (_session.Name ?? "Untitled") + ".s",
                DefaultExtension = "s",
                FileTypeChoices = [SongFiles],
            });
            path = target?.TryGetLocalPath();
            if (path is null)
                return false;
        }

        try
        {
            _session.Save(path);
            return true;
        }
        catch (Exception ex)
        {
            await MessageWindow.ShowAsync(this, "Cannot save song", ex.Message);
            return false;
        }
    }

    private bool _closeConfirmed;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !_session.IsDirty)
            return;

        e.Cancel = true;
        if (await ConfirmDiscardAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>Yes, No or Cancel - null for Cancel.</summary>
    private async Task<bool?> AskAsync(string title, string question)
    {
        bool? answer = null;
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Button Choice(string text, bool? value)
        {
            var button = new Button { Content = text, MinWidth = 80 };
            button.Click += (_, _) =>
            {
                answer = value;
                window.Close();
            };
            return button;
        }

        window.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { Choice("Save", true), Choice("Don't save", false), Choice("Cancel", null) },
                },
            },
        };
        await window.ShowDialog(this);
        return answer;
    }
}
