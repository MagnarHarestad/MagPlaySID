using MagSid.Core.Model;
using MagSid.Playback;
using MagSid.Sid;
using NAudio.Wave;

namespace MagSid.Editor.App;

/// <summary>
/// Frame-by-frame SID signal-path trace for one instrument: register writes, gate, envelope,
/// oscillator and the final mixed output, all read straight out of a live <see cref="SidChip"/>
/// driven by the real <see cref="SongSequencer"/> - not a re-derivation from source, the actual
/// emulation this editor uses for playback and preview.
///
/// The instrument is triggered in a small synthetic one-note song built just for this trace
/// (channel 1 gets the real note; channels 2/3 get a silent hold-only pattern, since
/// SongSequencer always needs all three channels' order lists populated) rather than reading
/// from whatever is live in the main window - that keeps the trace reproducible and free of
/// interference from other channels fighting over the one shared filter.
/// </summary>
public sealed class SignalPathForm : Form
{
    private const double ClockFrequency = 985248.0; // PAL C64
    private const double FrameRate = 50.0;
    private const int SamplesPerFrame = 24; // sub-frame resolution for the osc/env/out sparklines

    private const int FrameWidth = 40;
    private const int LabelWidth = 100;

    /// <summary>Hold rows padded onto the audition song after the traced span. A channel order
    /// with a single pattern in it loops, which would retrigger the note and make one press of
    /// Trace sound like two; these push the loop point far beyond any time the audition is
    /// stopped, so the retrigger is unreachable rather than merely unlikely.</summary>
    private const int LoopGuardFrames = 250;
    private const int TopMargin = 20;
    private const int RulerHeight = 24;
    private const int WritesHeight = 24;
    private const int GateHeight = 32;
    private const int EnvelopeHeight = 130;
    private const int CutoffHeight = 90;
    private const int FilterModeHeight = 90;
    private const int OscHeight = 110;
    private const int OutHeight = 110;
    private const int RowGap = 6;

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

    /// <summary>A Panel that paints through an off-screen buffer. A plain one erases to the
    /// background and redraws in two visible steps, which the playhead's repaints turn into
    /// steady flicker - the same reason the Digi tool's canvas does this.</summary>
    private sealed class BufferedCanvas : Panel
    {
        public BufferedCanvas()
        {
            AutoScroll = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);
        }
    }

    private readonly BufferedCanvas _canvas = new()
    {
        Dock = DockStyle.Fill,
        BackColor = EditorPalette.Background,
    };

    /// <summary>Where the audition has reached, in trace frames, or null when nothing is
    /// sounding. Read from the output device's own play position rather than counting samples
    /// handed to it, so the marker sits on the frame being heard instead of one buffer ahead.</summary>
    private double? _playheadFrame;

    // 10, not the 16 or 30 that would read as "smooth enough": a WinForms Timer is WM_TIMER,
    // which fires on the 15.6ms system tick and rounds a requested interval *up* to a whole
    // number of ticks, so 16 asks for two of them and lands nearer 31ms. Anything comfortably
    // below one tick gets one tick - measured mean 15.8ms against 29.6ms at Interval=16.
    private readonly System.Windows.Forms.Timer _playheadTimer = new() { Interval = 10 };

    private TraceResult? _trace;

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
            string name = string.IsNullOrWhiteSpace(instrument.Name) ? "" : $" : {instrument.Name}";
            _instrumentPicker.Items.Add($"{instrument.Number:d2}{name}");
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
        _canvas.Paint += (_, e) => DrawTrace(e.Graphics);

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

        Controls.Add(_canvas);
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

        var song = BuildSingleNoteSong(instrument, note, frameCount);

        var sid = new SidChip();
        sid.SetChipModel(ChipModel.Mos8580);
        sid.SetSamplingParameters(ClockFrequency, SamplingMethod.Interpolate, 44100);

        var result = new TraceResult { Frames = new List<FrameData>(frameCount) };
        List<WriteEvent> pendingWrites = [];
        int controlRegister = 0;

        // The filter is one shared chip resource, not per-voice, and it only ever moves once
        // per frame (StepFilter/StepFilterSweep), so - unlike osc/env/out below - there is
        // nothing to sub-sample within a frame: one running value each is enough.
        int filterCutoff = 0;
        int filterResRoute = 0;
        int filterModeVol = 0;

        void CaptureWrite(int reg, int value)
        {
            pendingWrites.Add(new WriteEvent(reg, value));
            switch (reg)
            {
                case 4: controlRegister = value; break;
                case 0x16: filterCutoff = value; break;
                case 0x17: filterResRoute = value; break;
                case 0x18: filterModeVol = value; break;
            }
            sid.Write(reg, value);
        }

        var sequencer = new SongSequencer(song, CaptureWrite);
        pendingWrites.Clear(); // the constructor's own master-volume write - not a traced frame

        int triggerFrame = -1;
        sequencer.RowTriggered += (channel, _, _, _, row) =>
        {
            if (channel == 0 && row is NoteRow && triggerFrame < 0)
            {
                triggerFrame = result.Frames.Count;
            }
        };

        double cyclesTarget = 0;
        int cyclesClocked = 0;

        var voice = sid.Voice(0);

        for (int frame = 0; frame < frameCount; frame++)
        {
            pendingWrites.Clear();
            sequencer.Frame();

            var frameData = new FrameData
            {
                Writes = new List<WriteEvent>(pendingWrites),
                ControlRegister = controlRegister,
                FilterCutoff = filterCutoff,
                FilterResRoute = filterResRoute,
                FilterModeVol = filterModeVol,
                FilterResRouteChanged = pendingWrites.Any(w => w.Register == 0x17),
            };

            cyclesTarget += ClockFrequency / FrameRate;
            int cyclesThisFrame = (int)cyclesTarget - cyclesClocked;
            int sampleEvery = Math.Max(1, cyclesThisFrame / SamplesPerFrame);

            var osc = new List<int>(SamplesPerFrame + 1);
            var env = new List<int>(SamplesPerFrame + 1);
            var outp = new List<int>(SamplesPerFrame + 1);

            for (int c = 0; c < cyclesThisFrame; c++)
            {
                sid.Clock();
                cyclesClocked++;
                if (c % sampleEvery == 0)
                {
                    osc.Add(voice.Wave.Output());
                    env.Add(voice.Envelope.Output());
                    outp.Add(sid.Output());
                }
            }

            frameData.OscSamples = osc.ToArray();
            frameData.EnvelopeSamples = env.ToArray();
            frameData.OutSamples = outp.ToArray();
            frameData.EnvelopeValue = voice.Envelope.Output();
            frameData.EnvelopeState = voice.Envelope.State;
            frameData.GateOn = (controlRegister & 0x01) != 0;
            frameData.TestBit = (controlRegister & 0x08) != 0;

            result.Frames.Add(frameData);
        }

        result.TriggerFrame = triggerFrame;
        result.MasterVolume = 0x0f;
        result.InstrumentLabel = _instrumentPicker.SelectedItem?.ToString() ?? "";
        result.NoteLabel = note.ToString();

        _trace = result;
        _summary.Text = triggerFrame < 0
            ? $"{result.InstrumentLabel}  note {result.NoteLabel}  - never triggered (check the instrument's own data)"
            : $"{result.InstrumentLabel}  note {result.NoteLabel}  triggered on frame {triggerFrame}  master vol $0f";

        // Sized here, once, rather than from inside Paint - reassigning a control's own Size (or
        // its parent Form's, which an earlier version of this method did by mistake) from within
        // its own Paint handler triggers a re-layout mid-paint, which is exactly what left the
        // canvas rendering only its first couple of rows before silently giving up.
        int contentWidth = LabelWidth + frameCount * FrameWidth + 20;
        int contentHeight = TopMargin + RulerHeight + WritesHeight + GateHeight + EnvelopeHeight +
                             CutoffHeight + FilterModeHeight + OscHeight + OutHeight + RowGap * 6 + 20;
        _canvas.AutoScrollMinSize = new Size(contentWidth, contentHeight);

        _canvas.Invalidate();

        // Started only once the trace is computed and on screen. Tracing clocks the chip through
        // every frame and blocks this thread for a good fraction of a second; starting the sound
        // first meant the marker's first painted position was already a third of the way along,
        // instead of setting off from the trigger line with the note.
        if (play)
        {
            _canvas.Update();
            PlayAudition(song, frameCount);
        }
    }

    /// <summary>One note on channel 1, held via HoldRows for the whole capture window so it
    /// never re-triggers or runs off the end of the pattern (which would advance the order and
    /// loop back to a second trigger). Channels 2/3 get their own silent hold-only pattern -
    /// SongSequencer always primes all three channels' order lists in its constructor, so they
    /// cannot be left empty.</summary>
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

            // BuildSingleNoteSong is one note plus frameCount + 8 holds, and the song order
            // loops, so leaving it running retriggers the note - which is why an earlier
            // too-generous stop time made Trace audibly play the instrument twice. Stopping one
            // frame short of the loop point keeps the traced span plus its release tail and
            // nothing after it.
            // Long enough to cover every traced frame plus a release tail. It does not have to
            // beat the song's loop point to a deadline: BuildSingleNoteSong pads the pattern
            // with LoopGuardFrames of holds so the note cannot retrigger anywhere near here,
            // which is what stops Trace from playing the instrument twice. Racing the loop with
            // this timer instead does not work - between the WM_TIMER quantum and the output
            // buffer's latency, the window that both covers the trace and beats the loop is
            // narrower than the error on either.
            _auditionStopTimer.Interval = Math.Max(50, (int)((frameCount + 12) * 1000.0 / FrameRate));
            _auditionStopTimer.Start();
            _playheadFrame = 0;
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
        _playheadFrame = null;
        _canvas.Invalidate();
        _audition?.Stop();
        _audition?.Dispose();
        _audition = null;
        _auditionEngine = null;
    }

    /// <summary>Moves the marker to the frame currently being heard, and keeps it in view once
    /// the trace is wider than the window.</summary>
    private void UpdatePlayhead()
    {
        if (_audition is null || _auditionEngine is null || _trace is null)
        {
            return;
        }

        long? samples = AuditionClock.SamplesPlayed(_audition, _auditionEngine);
        if (samples is null)
        {
            return; // the device can be torn down under us; a missed frame is not worth a crash
        }
        double frame = samples.Value / (double)SongPlaybackEngine.SampleRate * FrameRate;

        // Parked at the last traced frame rather than run on: the audition is deliberately
        // longer than the trace so the release tail is audible, and without this the marker
        // carries on into blank canvas past the right-hand edge of the picture (measured at
        // frame 46 of a 24-frame trace).
        _playheadFrame = Math.Min(frame, _trace.Frames.Count - 1);

        int x = LabelWidth + (int)(frame * FrameWidth);
        int scrollX = -_canvas.AutoScrollPosition.X;
        int visible = _canvas.ClientSize.Width;
        if (x > scrollX + visible - FrameWidth * 2)
        {
            int contentWidth = _canvas.AutoScrollMinSize.Width;
            int target = Math.Max(0, Math.Min(x - visible + FrameWidth * 4, Math.Max(0, contentWidth - visible)));
            _canvas.AutoScrollPosition = new Point(target, 0);
        }
        else if (x < scrollX)
        {
            _canvas.AutoScrollPosition = new Point(Math.Max(0, x - FrameWidth), 0);
        }

        _canvas.Invalidate();
    }

    private static Song BuildSingleNoteSong(Instrument instrument, NoteName note, int frameCount)
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(instrument);

        int rows = frameCount + 8 + LoopGuardFrames;

        var lead = new Pattern { Number = 1 };
        lead.Rows.Add(new NoteRow(note, instrument.Number));
        for (int i = 0; i < rows; i++)
        {
            lead.Rows.Add(new HoldRow());
        }
        song.Patterns.Add(lead);

        var silent = new Pattern { Number = 2 };
        for (int i = 0; i < rows; i++)
        {
            silent.Rows.Add(new HoldRow());
        }
        song.Patterns.Add(silent);

        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(2));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(2));

        return song;
    }

    // ---------------------------------------------------------------- drawing

    private void DrawTrace(Graphics g)
    {
        g.Clear(EditorPalette.Background);
        if (_trace is null || _trace.Frames.Count == 0)
        {
            return;
        }

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var mono = new Font("Consolas", 8f);
        using var rowFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);

        int top = TopMargin;
        int left = LabelWidth;

        // Frame ruler
        using (var rulerBrush = new SolidBrush(EditorPalette.Subtitle))
        using (var gridPen = new Pen(EditorPalette.Border))
        {
            for (int i = 0; i < _trace.Frames.Count; i++)
            {
                int x = left + i * FrameWidth;
                if (i % 2 == 0)
                {
                    g.DrawString(i.ToString(), mono, rulerBrush, x + 2, top);
                }
                g.DrawLine(gridPen, x, top, x, top + RulerHeight + WritesHeight + GateHeight + EnvelopeHeight + OscHeight + OutHeight);
            }
        }

        int y = top + RulerHeight;

        DrawRowLabel(g, rowFont, "writes\n$d4xx", y, WritesHeight);
        DrawWrites(g, mono, left, y, WritesHeight);
        y += WritesHeight + RowGap;

        DrawRowLabel(g, rowFont, "gate", y, GateHeight);
        DrawGate(g, mono, left, y, GateHeight);
        y += GateHeight + RowGap;

        DrawRowLabel(g, rowFont, "envelope", y, EnvelopeHeight);
        DrawEnvelope(g, mono, left, y, EnvelopeHeight);
        y += EnvelopeHeight + RowGap;

        DrawRowLabel(g, rowFont, "cutoff\n$d416", y, CutoffHeight);
        DrawFilterCutoff(g, left, y, CutoffHeight);
        y += CutoffHeight + RowGap;

        DrawRowLabel(g, rowFont, "filter mode\n$d418", y, FilterModeHeight);
        DrawFilterMode(g, mono, left, y, FilterModeHeight);
        y += FilterModeHeight + RowGap;

        DrawRowLabel(g, rowFont, "oscillator", y, OscHeight);
        DrawWaveformRow(g, left, y, OscHeight, f => f.TestBit ? null : f.OscSamples, f => f.TestBit,
            EditorPalette.Mono, Color.FromArgb(0x5a, 0x20, 0x20));
        y += OscHeight + RowGap;

        DrawRowLabel(g, rowFont, "out\nwave x env", y, OutHeight);
        DrawWaveformRow(g, left, y, OutHeight, f => f.OutSamples, _ => false,
            EditorPalette.Title, Color.Empty);
        y += OutHeight + RowGap;

        if (_trace.TriggerFrame >= 0)
        {
            using var trigPen = new Pen(Color.FromArgb(0x66, 0xcc, 0x66), 1.5f);
            int x = left + _trace.TriggerFrame * FrameWidth;
            g.DrawLine(trigPen, x, top, x, y);
        }

        // Last, so it sits over the trace rather than under it. Amber, to stay distinct from the
        // green trigger marker beside it - the two are often only a frame or two apart.
        if (_playheadFrame is double playhead)
        {
            using var playPen = new Pen(Color.FromArgb(0xff, 0xd7, 0x00), 2f);
            int x = left + (int)(playhead * FrameWidth);
            g.DrawLine(playPen, x, top, x, y);
        }
    }

    private static void DrawRowLabel(Graphics g, Font font, string text, int y, int height)
    {
        using var brush = new SolidBrush(EditorPalette.Heading);
        var rect = new Rectangle(4, y, LabelWidth - 8, height);
        var format = new StringFormat { LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, rect, format);
    }

    private void DrawWrites(Graphics g, Font font, int left, int y, int height)
    {
        using var tickPen = new Pen(EditorPalette.Mono, 1.5f);
        using var textBrush = new SolidBrush(EditorPalette.Subtitle);

        for (int i = 0; i < _trace!.Frames.Count; i++)
        {
            var frame = _trace.Frames[i];
            if (frame.Writes.Count == 0)
            {
                continue;
            }

            int x = left + i * FrameWidth + FrameWidth / 2;
            g.DrawLine(tickPen, x, y + 2, x, y + height - 2);
        }
    }

    private void DrawGate(Graphics g, Font font, int left, int y, int height)
    {
        using var onBrush = new SolidBrush(Color.FromArgb(0x2f, 0x6b, 0x2f));
        using var offBrush = new SolidBrush(EditorPalette.FieldBackground);
        using var onText = new SolidBrush(Color.FromArgb(0x9a, 0xff, 0x9a));
        using var offText = new SolidBrush(EditorPalette.Subtitle);

        bool? prevGate = null;
        for (int i = 0; i < _trace!.Frames.Count; i++)
        {
            var frame = _trace.Frames[i];
            int x = left + i * FrameWidth;
            g.FillRectangle(frame.GateOn ? onBrush : offBrush, x, y + 2, FrameWidth, height - 4);

            if (prevGate != frame.GateOn)
            {
                g.DrawString(frame.GateOn ? "on" : "off", font, frame.GateOn ? onText : offText, x + 2, y + 5);
            }
            prevGate = frame.GateOn;
        }
    }

    private void DrawEnvelope(Graphics g, Font font, int left, int y, int height)
    {
        using var linePen = new Pen(EditorPalette.Title, 1.4f);
        using var stateText = new SolidBrush(EditorPalette.Subtitle);

        var points = new List<PointF>();
        EnvelopeGenerator.EnvelopeState? prevState = null;

        for (int i = 0; i < _trace!.Frames.Count; i++)
        {
            var frame = _trace.Frames[i];
            var samples = frame.EnvelopeSamples;
            if (samples.Length == 0)
            {
                continue;
            }

            if (prevState != frame.EnvelopeState)
            {
                string label = frame.EnvelopeState switch
                {
                    EnvelopeGenerator.EnvelopeState.Attack => "A",
                    EnvelopeGenerator.EnvelopeState.DecaySustain => "D/S",
                    _ => "R",
                };
                int lx = left + i * FrameWidth;
                g.DrawString(label, font, stateText, lx + 2, y + 2);
            }
            prevState = frame.EnvelopeState;

            for (int s = 0; s < samples.Length; s++)
            {
                float x = left + i * FrameWidth + (float)s / samples.Length * FrameWidth;
                float norm = samples[s] / 255f;
                float py = y + height - 4 - norm * (height - 20);
                points.Add(new PointF(x, py));
            }
        }

        if (points.Count > 1)
        {
            g.DrawLines(linePen, points.ToArray());
        }
    }

    /// <summary>The filter's cutoff ($D416) as a step chart - one value per frame, not
    /// sub-sampled like the envelope/oscillator/output rows above, because the filter genuinely
    /// only moves once per frame (see FrameData.FilterCutoff's own comment).</summary>
    private void DrawFilterCutoff(Graphics g, int left, int y, int height)
    {
        using var linePen = new Pen(EditorPalette.Title, 1.4f);

        int plotBottom = y + height - 6;
        int plotTop = y + 6;
        float ScaleY(int cutoff) => plotBottom - cutoff / 255f * (plotBottom - plotTop);

        var points = new List<PointF>();
        for (int i = 0; i < _trace!.Frames.Count; i++)
        {
            var frame = _trace.Frames[i];
            int x0 = left + i * FrameWidth;
            int x1 = x0 + FrameWidth;
            float py = ScaleY(frame.FilterCutoff);
            points.Add(new PointF(x0, py));
            points.Add(new PointF(x1, py));
        }

        if (points.Count > 1)
        {
            g.DrawLines(linePen, points.ToArray());
        }
    }

    /// <summary>Filter mode + master volume ($D418), the high nibble being the actual filter
    /// type (low/band/high-pass) - plotted the same way as cutoff, a step chart of the whole
    /// byte, since that is what a single register write actually carries. Res+Route ($D417)
    /// rides along as a text label wherever it changes: it is single-byte state, not a value
    /// worth a line of its own, but a Setup step always writes both together so it belongs on
    /// this row rather than getting a third graph.</summary>
    private void DrawFilterMode(Graphics g, Font font, int left, int y, int height)
    {
        using var linePen = new Pen(EditorPalette.Title, 1.4f);
        using var labelText = new SolidBrush(EditorPalette.Subtitle);

        int plotBottom = y + height - 6;
        int plotTop = y + 20;
        float ScaleY(int value) => plotBottom - value / 255f * (plotBottom - plotTop);

        var points = new List<PointF>();
        for (int i = 0; i < _trace!.Frames.Count; i++)
        {
            var frame = _trace.Frames[i];
            int x0 = left + i * FrameWidth;
            int x1 = x0 + FrameWidth;
            float py = ScaleY(frame.FilterModeVol);
            points.Add(new PointF(x0, py));
            points.Add(new PointF(x1, py));

            if (frame.FilterResRouteChanged)
            {
                g.DrawString($"res:${frame.FilterResRoute:x2}", font, labelText, x0 + 2, y + 2);
            }
        }

        if (points.Count > 1)
        {
            g.DrawLines(linePen, points.ToArray());
        }
    }

    private void DrawWaveformRow(
        Graphics g, int left, int y, int height,
        Func<FrameData, int[]?> selector, Func<FrameData, bool> isTest,
        Color lineColor, Color testColor)
    {
        using var linePen = new Pen(lineColor, 1f);
        using var testBrush = testColor == Color.Empty ? null : new SolidBrush(testColor);
        using var testText = new SolidBrush(EditorPalette.Body);

        int mid = y + height / 2;

        // Global scale so a quiet instrument doesn't look identical to a loud one.
        int maxAbs = 1;
        foreach (var frame in _trace!.Frames)
        {
            var samples = selector(frame);
            if (samples is null)
            {
                continue;
            }
            foreach (int v in samples)
            {
                maxAbs = Math.Max(maxAbs, Math.Abs(v - Center(samples)));
            }
        }

        bool testActiveLabelDrawn = false;
        var points = new List<PointF>();

        for (int i = 0; i < _trace.Frames.Count; i++)
        {
            var frame = _trace.Frames[i];
            int x = left + i * FrameWidth;

            if (isTest(frame))
            {
                if (testBrush is not null)
                {
                    g.FillRectangle(testBrush, x, y + 1, FrameWidth, height - 2);
                    if (!testActiveLabelDrawn)
                    {
                        g.DrawString("TEST", new Font("Segoe UI", 7.5f), testText, x + 2, y + 3);
                        testActiveLabelDrawn = true;
                    }
                }
                continue;
            }
            testActiveLabelDrawn = false;

            var samples = selector(frame);
            if (samples is null || samples.Length == 0)
            {
                continue;
            }

            int c = Center(samples);
            for (int s = 0; s < samples.Length; s++)
            {
                float sx = left + i * FrameWidth + (float)s / samples.Length * FrameWidth;
                float norm = (samples[s] - c) / (float)maxAbs;
                float sy = mid - norm * (height / 2f - 2);
                points.Add(new PointF(sx, sy));
            }
        }

        if (points.Count > 1)
        {
            g.DrawLines(linePen, points.ToArray());
        }
    }

    private static int Center(int[] samples)
    {
        long sum = 0;
        foreach (int v in samples)
        {
            sum += v;
        }
        return samples.Length == 0 ? 0 : (int)(sum / samples.Length);
    }

    private sealed record WriteEvent(int Register, int Value);

    private sealed class FrameData
    {
        public List<WriteEvent> Writes = [];
        public bool GateOn;
        public bool TestBit;
        public int ControlRegister;
        public int EnvelopeValue;
        public EnvelopeGenerator.EnvelopeState EnvelopeState;
        public int[] OscSamples = [];
        public int[] EnvelopeSamples = [];
        public int[] OutSamples = [];

        /// <summary>$D416 - the running cutoff, held between steps like the real one-shot filter
        /// program does (see Filter_Play's own comment in Code\musicplayer.s).</summary>
        public int FilterCutoff;

        /// <summary>$D417 - resonance (high nibble) + which voices are routed through the
        /// filter (low nibble).</summary>
        public int FilterResRoute;

        /// <summary>$D418 - filter mode (high nibble) + master volume (low nibble).</summary>
        public int FilterModeVol;

        public bool FilterResRouteChanged;
    }

    private sealed class TraceResult
    {
        public List<FrameData> Frames = [];
        public int TriggerFrame = -1;
        public int MasterVolume;
        public string InstrumentLabel = "";
        public string NoteLabel = "";
    }
}
