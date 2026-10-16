using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using MagSid.Core.Model;
using MagSid.Editor.App;
using MagSid.Editor.App.Controls;
using MagSid.Editor.Avalonia.Player;
using MagSid.Playback;
using MagSid.Sid;
using AvColor = Avalonia.Media.Color;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The editor's window on every system: the Retro view - the instrument list and the tracker grid -
/// with the song's file, history and playback around it. The grid is the Windows editor's own
/// TrackerGridControl, compiled against the WinForms face in MagSid.Editor.Views; what is here is
/// the part MainForm does there, rebuilt in Avalonia: menus, transport, and wiring the grid's
/// events to the session and the engines.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly IBrush WindowBackground = new SolidColorBrush(AvColor.FromRgb(0x10, 0x10, 0x20));
    private static readonly IBrush Field = new SolidColorBrush(AvColor.FromRgb(0x1C, 0x1C, 0x30));
    private static readonly IBrush Body = new SolidColorBrush(AvColor.FromRgb(0xD2, 0xD2, 0xDE));
    private static readonly IBrush Dim = new SolidColorBrush(AvColor.FromRgb(0x8A, 0x8A, 0xA8));
    private static readonly IBrush Accent = new SolidColorBrush(AvColor.FromRgb(0x66, 0xCC, 0xFF));
    private static readonly FontFamily Mono = new("Consolas, Menlo, DejaVu Sans Mono, monospace");

    private readonly EditorSession _session = new();
    private readonly TrackerGridControl _grid = new();
    private readonly ListBox _instruments = new();
    private readonly InstrumentEditorView _instrumentEditor = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _songLength = new();
    private readonly TextBlock _editMode = new();
    private readonly Button _play = new();
    private readonly Button _chip = new();
    private readonly NumericUpDown _speed = new();
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

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            StopPlayback();
            StopPatternPreview();
            _jam?.Dispose();
        };

        Opened += (_, _) =>
        {
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
            _session.MarkDirty();
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
        _instruments.SelectionChanged += (_, _) => ShowSelectedInstrument();

        var add = new Button { Content = "+ New" };
        add.Click += (_, _) => AddInstrument();
        var delete = new Button { Content = "Delete" };
        delete.Click += (_, _) => DeleteInstrument();
        var listHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var listButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { add, delete } };
        DockPanel.SetDock(listButtons, Dock.Right);
        listHeader.Children.Add(listButtons);
        listHeader.Children.Add(new TextBlock { Text = "Instruments", Foreground = Accent, VerticalAlignment = VerticalAlignment.Center });

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

        _grid.Width = TrackerGridControl.ContentWidth;
        _grid.HorizontalAlignment = HorizontalAlignment.Left;
        _grid.Margin = new Thickness(0, 4, 8, 8);

        _status.Foreground = Dim;
        _status.Margin = new Thickness(8, 2);

        var root = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(menu);
        root.Children.Add(toolbar);
        root.Children.Add(_status);
        root.Children.Add(left);
        root.Children.Add(_grid);
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
                Item("Save", () => _ = SaveAsync(saveAs: false), $"{command}+S"),
                Item("Save As...", () => _ = SaveAsync(saveAs: true), $"{command}+Shift+S"),
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

        return new Menu { ItemsSource = new[] { file, edit, play } };
    }

    // ------------------------------------------------------------------------------------ wiring

    private void WireEvents()
    {
        _session.StructureChanged += RefreshAll;
        _session.DirtyChanged += RefreshStatus;
        _session.HistoryChanged += RefreshStatus;

        _grid.Edited += () => _session.MarkDirty();
        _grid.NoteEntered += PreviewNote;
        _grid.NoteReleased += note => _jam?.NoteOff(note);
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

        _poll.Tick += (_, _) => DrainPlaybackEvents();
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
                StartFromCurrentPosition();
                break;
            case Key.F3:
                PlayCurrentPattern();
                break;
            case Key.F4:
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
                return;
        }

        e.Handled = true;
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
            _speed.Value = _session.Song.Speed;
            RefreshInstrumentNames();
            ShowSelectedInstrument();
            RefreshStatus();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshInstrumentNames()
    {
        int selected = Math.Max(0, _instruments.SelectedIndex);
        _instruments.ItemsSource = _session.Song.Instruments
            .Select(i => $"{i.Number:00}  {InstrumentNames.Display(i)}")
            .ToList();
        if (_session.Song.Instruments.Count > 0)
            _instruments.SelectedIndex = Math.Min(selected, _session.Song.Instruments.Count - 1);
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
        if (_session.AddInstrument() is null)
        {
            ShowNotice("All 16 instruments are in use - a pattern names one with a single byte.");
            return;
        }

        _instruments.SelectedIndex = _session.Song.Instruments.Count - 1;
    }

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

    private void PreviewNote(NoteName note, int instrumentNumber)
    {
        var instrument = _session.Song.Instruments.FirstOrDefault(i => i.Number == instrumentNumber);
        if (instrument is null)
            return;

        try
        {
            _jam ??= new JamEngine(_chipModel);
            _jam.Start();
            _jam.NoteOn(note, instrument);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException)
        {
            _jam?.Dispose();
            _jam = null;
        }
    }

    private void StartFromBeginning()
    {
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

        var preview = new Song { Speed = _session.Song.Speed };
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
        _grid.SetPlaybackActive(false);
        _play.Content = "▶ Play";
    }

    private void DrainPlaybackEvents()
    {
        if (_engine is not { } engine)
            return;

        while (engine.TryDequeueRowEvent(out var row))
            _grid.SetChannelPlaybackAnchor(row.Channel, row.PatternNumber, row.RowIndex, row.OrderEntrySequence, row.SampleIndex);
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
