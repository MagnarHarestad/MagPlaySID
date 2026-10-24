using MagSid.Core.Model;
using MagSid.Playback;
using MagSid.Sid;
using NAudio.Wave;

namespace MagSid.Editor.App;

/// <summary>
/// The Signal Path window: pick an instrument, a note and how many frames, and Trace takes a
/// <see cref="SignalPathTrace"/> of it, draws it on a <see cref="SignalPathCanvas"/> and plays it
/// while a marker follows along.
/// </summary>
public sealed class SignalPathForm : Form
{
    private readonly IReadOnlyList<Instrument> _instruments;

    private readonly ComboBox _instrumentPicker = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 220,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
        FlatStyle = FlatStyle.Flat,
    };

    private readonly ComboBox _notePicker = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 80,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
        FlatStyle = FlatStyle.Flat,
    };

    private readonly NumericUpDown _frameCount = new()
    {
        Minimum = 4,
        Maximum = 200,
        Value = 24,
        Width = 60,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
    };

    private readonly Button _traceButton = new()
    {
        Text = "Trace",
        FlatStyle = FlatStyle.Flat,
        BackColor = EditorPalette.ButtonBackground,
        ForeColor = EditorPalette.Body,
        Width = 90,
        Height = 24,
        Margin = new Padding(0, 19, 0, 0), // aligns with the field row of a Labelled() group beside it
    };

    private readonly Label _summary = new()
    {
        AutoSize = true,
        ForeColor = EditorPalette.Subtitle,
        Padding = new Padding(0, 8, 0, 0),
    };

    /// <summary>Scrolls the canvas, which is as big as the whole trace.</summary>
    private readonly Panel _host = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        BackColor = EditorPalette.Background,
    };

    private readonly SignalPathCanvas _canvas = new() { Location = Point.Empty };

    // 10, not the 16 or 30 that would read as "smooth enough": a WinForms Timer is WM_TIMER,
    // which fires on the 15.6ms system tick and rounds a requested interval *up* to a whole
    // number of ticks, so 16 asks for two of them and lands nearer 31ms. Anything comfortably
    // below one tick gets one tick - measured mean 15.8ms against 29.6ms at Interval=16.
    private readonly System.Windows.Forms.Timer _playheadTimer = new() { Interval = 10 };

    // Trace plays what it draws. Deliberately the song engine rather than NotePreviewPlayer,
    // which is what jam mode uses: that one has no frame clock, so it sounds an instrument's
    // first wavetable step and holds it. Everything this window exists to show - a drum moving
    // through noise into pulse, an arpeggio, a filter sweep - happens on later frames, so
    // auditioning through jam mode's path would play something audibly different from the
    // picture beside it. This runs exactly the song the trace was taken from.
    private WaveOutEvent? _audition;
    private SongPlaybackEngine? _auditionEngine;
    private readonly System.Windows.Forms.Timer _auditionStopTimer = new();

    public SignalPathForm(IReadOnlyList<Instrument> instruments)
    {
        _instruments = instruments;

        Text = "Signal Path";
        // CenterScreen, not CenterParent: this window is taller than MainForm typically sits,
        // so centering on the parent's own (often off-center, smaller) bounds pushed it mostly
        // off the bottom of the screen instead of actually centering it.
        StartPosition = FormStartPosition.CenterScreen;
        DarkTheme.ApplyTitleBar(this);
        // Wide enough that the default 24-frame trace (LabelWidth + 24*FrameWidth + margin =
        // 1080px of content) fits without the horizontal scrollbar, and tall enough for all
        // eight rows plus the toolbar/summary (toolbar 62 + summary 30 + row stack ~686 = ~778)
        // to fit without the vertical one either.
        ClientSize = new Size(1160, 880);
        // The trace is a fixed-width column layout; narrower just cuts the rightmost
        // columns off rather than reflowing them.
        MinimumSize = new Size(900, 560);
        BackColor = EditorPalette.Background;
        ForeColor = EditorPalette.Body;

        foreach (var instrument in _instruments)
        {
            _instrumentPicker.Items.Add(SignalPathTrace.Label(instrument));
        }
        if (_instrumentPicker.Items.Count > 0)
        {
            _instrumentPicker.SelectedIndex = 0;
        }

        foreach (var note in NoteName.AllNotes)
        {
            _notePicker.Items.Add(note.ToString());
        }
        _notePicker.SelectedItem = "C-3";
        if (_notePicker.SelectedIndex < 0 && _notePicker.Items.Count > 0)
        {
            _notePicker.SelectedIndex = 0;
        }

        _traceButton.Click += (_, _) => RunTrace(play: true);
        _auditionStopTimer.Tick += (_, _) => StopAudition();
        _playheadTimer.Tick += (_, _) => UpdatePlayhead();
        FormClosed += (_, _) => StopAudition();

        // Height generous on purpose: each Labelled() field below stacks a caption label above
        // the control (~15px) plus the control itself (~23-24px), and a FlowLayoutPanel clips
        // any child that doesn't fit its own bounds rather than growing to fit - a row sized
        // just to the control's own height silently clips the caption above it. Learned the hard
        // way once already in the old InstrumentEditorForm dialog's fixed-height GroupBox rows
        // (removed 2026-08-31, but the layout trap it taught still applies here) - same trap.
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 62,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 6, 0, 0),
            BackColor = EditorPalette.Background,
        };
        toolbar.Controls.Add(Labelled("Instrument", _instrumentPicker));
        toolbar.Controls.Add(Labelled("Note", _notePicker));
        toolbar.Controls.Add(Labelled("Frames", _frameCount));
        toolbar.Controls.Add(new Panel { Width = 8, Height = 1 });
        toolbar.Controls.Add(_traceButton);

        var summaryRow = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = EditorPalette.Background };
        summaryRow.Controls.Add(_summary);

        _host.Controls.Add(_canvas);
        Controls.Add(_host);
        Controls.Add(summaryRow);
        Controls.Add(toolbar);

        if (_instruments.Count > 0)
        {
            // Draw a trace so the window opens with something in it, but silently: sound on
            // open is startling, and nobody asked for it by getting here.
            RunTrace(play: false);
        }
    }

    private static Control Labelled(string caption, Control field)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Margin = new Padding(0, 0, 16, 0),
        };
        panel.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = EditorPalette.Subtitle,
        });
        panel.Controls.Add(field);
        return panel;
    }

    // ---------------------------------------------------------------- capture

    private void RunTrace(bool play)
    {
        if (_instrumentPicker.SelectedIndex < 0 || _notePicker.SelectedIndex < 0)
        {
            return;
        }

        var instrument = _instruments[_instrumentPicker.SelectedIndex];
        var note = NoteName.AllNotes[_notePicker.SelectedIndex];
        int frameCount = (int)_frameCount.Value;

        var trace = SignalPathTrace.Capture(instrument, note, frameCount);
        _summary.Text = trace.Summary;
        _canvas.ShowTrace(trace);

        // Started only once the trace is computed and on screen. Tracing clocks the chip through
        // every frame and blocks this thread for a good fraction of a second; starting the sound
        // first meant the marker's first painted position was already a third of the way along,
        // instead of setting off from the trigger line with the note.
        if (play)
        {
            _canvas.Update();
            PlayAudition(trace.Song, frameCount);
        }
    }

    /// <summary>Sounds the traced note, so the trace can be heard as well as read. Silently does
    /// nothing if the sound card is unavailable - the song being played in the main window holds
    /// it, and a trace is still perfectly usable without audio.</summary>
    private void PlayAudition(Song song, int frameCount)
    {
        StopAudition();

        try
        {
            _auditionEngine = new SongPlaybackEngine(song, ChipModel.Mos8580);
            _audition = new WaveOutEvent { DesiredLatency = 150 };
            _audition.Init(_auditionEngine);
            _audition.Play();

            // Long enough to cover every traced frame plus a release tail - see
            // SignalPathTrace.AuditionMilliseconds. Racing the song's loop point with this timer
            // does not work: between the WM_TIMER quantum and the output buffer's latency, the
            // window that both covers the trace and beats the loop is narrower than the error on
            // either.
            _auditionStopTimer.Interval = SignalPathTrace.AuditionMilliseconds(frameCount);
            _auditionStopTimer.Start();
            _canvas.PlayheadFrame = 0;
            _playheadTimer.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException)
        {
            StopAudition();
        }
    }

    private void StopAudition()
    {
        _auditionStopTimer.Stop();
        _playheadTimer.Stop();
        _canvas.PlayheadFrame = null;
        _audition?.Stop();
        _audition?.Dispose();
        _audition = null;
        _auditionEngine = null;
    }

    /// <summary>Moves the marker to the frame currently being heard, and keeps it in view once
    /// the trace is wider than the window.</summary>
    private void UpdatePlayhead()
    {
        if (_audition is null || _auditionEngine is null || _canvas.Trace is not { } trace)
        {
            return;
        }

        long? samples = AuditionClock.SamplesPlayed(_audition, _auditionEngine);
        if (samples is null)
        {
            return; // the device can be torn down under us; a missed frame is not worth a crash
        }
        double frame = samples.Value / (double)SongPlaybackEngine.SampleRate * SignalPathTrace.FrameRate;

        // Parked at the last traced frame rather than run on: the audition is deliberately
        // longer than the trace so the release tail is audible, and without this the marker
        // carries on into blank canvas past the right-hand edge of the picture (measured at
        // frame 46 of a 24-frame trace).
        _canvas.PlayheadFrame = Math.Min(frame, trace.Frames.Count - 1);

        const int frameWidth = SignalPathCanvas.FrameWidth;
        int x = SignalPathCanvas.XOfFrame(frame);
        int scrollX = -_host.AutoScrollPosition.X;
        int visible = _host.ClientSize.Width;
        if (x > scrollX + visible - frameWidth * 2)
        {
            int contentWidth = _canvas.Width;
            int target = Math.Max(0, Math.Min(x - visible + frameWidth * 4, Math.Max(0, contentWidth - visible)));
            _host.AutoScrollPosition = new Point(target, 0);
        }
        else if (x < scrollX)
        {
            _host.AutoScrollPosition = new Point(Math.Max(0, x - frameWidth), 0);
        }
    }
}
