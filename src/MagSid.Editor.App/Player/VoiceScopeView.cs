using System.Drawing.Drawing2D;
using MagSid.Playback;

namespace MagSid.Editor.App.Player;

/// <summary>
/// One SID voice's own waveform, in the spirit of Protracker's quadrascope: a single channel, before
/// the filter and before the mix, so what each voice is actually doing is legible on its own.
///
/// The trace is triggered on a rising zero crossing rather than drawn from wherever the buffer
/// happens to start. Without that the waveform slides sideways every frame and reads as noise; with
/// it a steady note stands still, which is the entire point of a scope.
/// </summary>
internal sealed class VoiceScopeView : Control
{
    /// <summary>How much of the captured window is drawn. About 14ms at 44.1kHz - a few cycles of a
    /// mid note, and short enough that a fast arpeggio still shows as movement.</summary>
    private const int VisibleSamples = 620;

    /// <summary>How far into the buffer the trigger may hunt for a crossing. The rest is the window
    /// it then draws.</summary>
    private const int TriggerSearch = SidFilePlaybackEngine.ScopeSamples - VisibleSamples - 1;

    private readonly float[] _samples = new float[SidFilePlaybackEngine.ScopeSamples];
    private readonly PointF[] _trace = new PointF[VisibleSamples];
    private readonly int _voice;
    private readonly Color _colour;

    private bool _muted;
    private float _level;

    public VoiceScopeView(int voice)
    {
        _voice = voice;
        _colour = SidPlayerPalette.VoiceColour(voice);

        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = SidPlayerPalette.Background;
        Cursor = Cursors.Hand;
    }

    /// <summary>Clicking a scope mutes its voice - the listening aid the tracker already has, where
    /// the eye is already looking.</summary>
    public event Action<int, bool>? MuteToggled;

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            Invalidate();
        }
    }

    /// <param name="atSample">Where the listener actually is, from the output device - see
    /// SidPlayerForm.</param>
    public void Update(SidFilePlaybackEngine engine, long atSample)
    {
        engine.CaptureVoiceScope(_voice, _samples, atSample);

        // AC coupling, as a scope's input stage does it. A 6581 voice never goes negative - it sits
        // on a large DC offset - so without this the trace would sit off the top of the panel and a
        // 6581 tune would appear to have no waveform at all. Removing the window's own mean is exact
        // for what is drawn and needs no filter state.
        float mean = 0f;
        for (int i = 0; i < _samples.Length; i++)
            mean += _samples[i];
        mean /= _samples.Length;

        float peak = 0f;
        for (int i = 0; i < _samples.Length; i++)
        {
            _samples[i] -= mean;
            peak = Math.Max(peak, Math.Abs(_samples[i]));
        }

        // The level meter follows the peak up at once and drifts down, so a decaying note reads as
        // decaying rather than blinking off.
        _level = peak > _level ? peak : Math.Max(peak, _level - 0.04f);

        Invalidate();
    }

    public void Clear()
    {
        Array.Clear(_samples);
        _level = 0f;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        Muted = !Muted;
        MuteToggled?.Invoke(_voice, Muted);
        base.OnMouseClick(e);
    }

    /// <summary>The first rising crossing of zero, which is where the drawn window starts. Falls back
    /// to the head of the buffer when the voice is silent or has no crossing to find.</summary>
    private int FindTrigger()
    {
        for (int i = 1; i < TriggerSearch; i++)
        {
            if (_samples[i - 1] <= 0f && _samples[i] > 0f)
                return i;
        }

        return 0;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height));

        using (var fill = SidPlayerPalette.PanelFill(bounds))
            g.FillRectangle(fill, bounds);

        int midY = bounds.Height / 2;

        using (var centre = new Pen(Color.FromArgb(0x20, 0x20, 0x30)))
            g.DrawLine(centre, 0, midY, bounds.Width, midY);

        // A bar down the left edge carrying the voice's colour and its current level, so a muted or
        // silent voice is obvious even when the trace is flat.
        DrawLevelBar(g, bounds);

        if (!_muted)
            DrawTrace(g, bounds, midY);

        using (var border = new Pen(SidPlayerPalette.PanelBorder))
            g.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);

        DrawLabel(g, bounds);
    }

    private void DrawLevelBar(Graphics g, Rectangle bounds)
    {
        const int barWidth = 3;
        int height = (int)(bounds.Height * Math.Clamp(_level, 0f, 1f));

        using var track = new SolidBrush(Color.FromArgb(0x1A, 0x1A, 0x28));
        g.FillRectangle(track, 1, 1, barWidth, bounds.Height - 2);

        if (height > 1 && !_muted)
        {
            using var brush = new SolidBrush(SidPlayerPalette.WithAlpha(_colour, 190));
            g.FillRectangle(brush, 1, bounds.Height - 1 - height, barWidth, height);
        }
    }

    private void DrawTrace(Graphics g, Rectangle bounds, int midY)
    {
        int start = FindTrigger();
        float scale = (bounds.Height / 2f) - 6f;
        float step = (bounds.Width - 10f) / (VisibleSamples - 1);

        for (int i = 0; i < VisibleSamples; i++)
        {
            float value = Math.Clamp(_samples[start + i], -1f, 1f);
            _trace[i] = new PointF(6f + (i * step), midY - (value * scale));
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;

        // A wide, faint pass under the line reads as glow without a blur, which would cost more than
        // this whole window does.
        using (var glow = new Pen(SidPlayerPalette.WithAlpha(_colour, 40), 3.5f))
            g.DrawLines(glow, _trace);

        using (var pen = new Pen(_colour, 1.4f))
            g.DrawLines(pen, _trace);

        g.SmoothingMode = SmoothingMode.Default;
    }

    private void DrawLabel(Graphics g, Rectangle bounds)
    {
        string text = _muted ? $"VOICE {_voice + 1}  MUTED" : $"VOICE {_voice + 1}";
        using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var brush = new SolidBrush(_muted
            ? SidPlayerPalette.ControlDisabled
            : SidPlayerPalette.WithAlpha(_colour, 170));

        g.DrawString(text, font, brush, 8, bounds.Height - 16);
    }
}
