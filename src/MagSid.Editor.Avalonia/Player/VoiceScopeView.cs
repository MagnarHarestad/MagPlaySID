using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MagSid.Playback;

namespace MagSid.Editor.Avalonia.Player;

/// <summary>
/// One SID voice's own waveform, before the filter and the mix, triggered on a rising zero crossing
/// so a steady note stands still. A click mutes the voice. A port of the Windows editor's view.
/// </summary>
internal sealed class VoiceScopeView : Control
{
    private const int VisibleSamples = 620;
    private const int TriggerSearch = SidFilePlaybackEngine.ScopeSamples - VisibleSamples - 1;

    private static readonly Typeface LabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    private readonly float[] _samples = new float[SidFilePlaybackEngine.ScopeSamples];
    private readonly int _voice;
    private readonly Color _colour;
    private readonly IBrush _fill = SidPlayerPalette.PanelFill();
    private readonly Pen _border = new(SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder));
    private readonly Pen _centre = new(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x30)));
    private readonly Pen _glow;
    private readonly Pen _trace;

    private bool _muted;
    private float _level;

    public VoiceScopeView(int voice)
    {
        _voice = voice;
        _colour = SidPlayerPalette.VoiceColour(voice);
        _glow = new Pen(new SolidColorBrush(SidPlayerPalette.WithAlpha(_colour, 40)), 3.5);
        _trace = new Pen(new SolidColorBrush(_colour), 1.4);
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public event Action<int, bool>? MuteToggled;

    public void Update(SidFilePlaybackEngine engine, long atSample)
    {
        engine.CaptureVoiceScope(_voice, _samples, atSample);

        // AC coupling: a 6581 voice sits on a large DC offset, which a scope's input removes.
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

        _level = peak > _level ? peak : Math.Max(peak, _level - 0.04f);
        InvalidateVisual();
    }

    public void Clear()
    {
        Array.Clear(_samples);
        _level = 0f;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _muted = !_muted;
            MuteToggled?.Invoke(_voice, _muted);
            InvalidateVisual();
        }

        base.OnPointerPressed(e);
    }

    private int FindTrigger()
    {
        for (int i = 1; i < TriggerSearch; i++)
        {
            if (_samples[i - 1] <= 0f && _samples[i] > 0f)
                return i;
        }

        return 0;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(0, 0, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));
        context.FillRectangle(_fill, bounds);

        double midY = bounds.Height / 2;
        context.DrawLine(_centre, new Point(0, midY), new Point(bounds.Width, midY));

        // The voice's level down the left edge, so a silent or muted voice is obvious.
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x28)), new Rect(1, 1, 3, bounds.Height - 2));
        double level = bounds.Height * Math.Clamp(_level, 0f, 1f);
        if (level > 1 && !_muted)
            context.FillRectangle(new SolidColorBrush(SidPlayerPalette.WithAlpha(_colour, 190)), new Rect(1, bounds.Height - 1 - level, 3, level));

        if (!_muted)
            DrawTrace(context, bounds, midY);

        context.DrawRectangle(_border, bounds.Deflate(0.5));

        string text = _muted ? $"VOICE {_voice + 1}  MUTED" : $"VOICE {_voice + 1}";
        var label = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelFace, 10, new SolidColorBrush(_muted ? SidPlayerPalette.ControlDisabled : SidPlayerPalette.WithAlpha(_colour, 170)));
        context.DrawText(label, new Point(8, bounds.Height - 17));
    }

    private void DrawTrace(DrawingContext context, Rect bounds, double midY)
    {
        int start = FindTrigger();
        double scale = (bounds.Height / 2) - 6;
        double step = (bounds.Width - 10) / (VisibleSamples - 1);

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (int i = 0; i < VisibleSamples; i++)
            {
                double value = Math.Clamp(_samples[start + i], -1f, 1f);
                var point = new Point(6 + (i * step), midY - (value * scale));
                if (i == 0)
                    path.BeginFigure(point, false);
                else
                    path.LineTo(point);
            }

            path.EndFigure(false);
        }

        context.DrawGeometry(null, _glow, geometry);
        context.DrawGeometry(null, _trace, geometry);
    }
}
