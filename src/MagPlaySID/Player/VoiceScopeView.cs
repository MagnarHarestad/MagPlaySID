using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MagSid.Playback;

namespace MagPlaySid.Player;

/// <summary>
/// One SID voice's own waveform, before the filter and the mix, triggered on a rising zero crossing
/// so a steady note stands still. A click mutes the voice. A port of the Windows editor's view.
/// A voice past the third - on a second or third SID - takes the colour of its place on its chip.
/// </summary>
internal sealed class VoiceScopeView : Control
{
    private const int VisibleSamples = 620;
    private const int TriggerSearch = SidFilePlaybackEngine.ScopeSamples - VisibleSamples - 1;

    private static readonly Typeface LabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    private readonly float[] _samples = new float[SidFilePlaybackEngine.ScopeSamples];
    private readonly int _voice;
    private readonly string _name;

    /// <summary>What the label shortens to where the scope is too narrow for the name: a module's
    /// channel number alone, when there are many.</summary>
    private readonly string _shortName;
    private readonly Color _colour;
    private readonly IBrush _fill = SidPlayerPalette.PanelFill();
    private readonly IPen _border = SidPlayerPalette.Pen(SidPlayerPalette.PanelBorder);
    private readonly IPen _centre = SidPlayerPalette.Pen(Color.FromRgb(0x20, 0x20, 0x30));
    private readonly IPen _glow;
    private readonly IPen _trace;

    /// <summary>The label, laid out once - again only when muting or the text's size changes it.</summary>
    private TextLayout? _label;
    private (bool Muted, double Scale, double Width) _labelFor;

    private bool _muted;
    private readonly bool _mutable;

    /// <summary>1 for the small text, more for bigger: the voice's name in the corner.</summary>
    public double TextScale { get; set; } = 1;
    private float _level;

    /// <param name="name">What the scope is labelled; VOICE and the voice's number when left out.</param>
    /// <param name="colour">Its colour; the voice's place on its chip's when left out.</param>
    /// <param name="mutable">Whether a click mutes it - not a digi's, which plays through the volume.</param>
    /// <param name="shortName">The label where the name does not fit; the name when left out.</param>
    public VoiceScopeView(int voice, string? name = null, Color? colour = null, bool mutable = true, string? shortName = null)
    {
        _voice = voice;
        _mutable = mutable;
        _name = name ?? $"VOICE {voice + 1}";
        _shortName = shortName ?? _name;
        _colour = colour ?? SidPlayerPalette.VoiceColour(voice % 3);
        _glow = SidPlayerPalette.Pen(SidPlayerPalette.WithAlpha(_colour, 40), 3.5);
        _trace = SidPlayerPalette.Pen(_colour, 1.4);
        if (mutable)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, "Click to mute, right-click to hear it alone");
        }
    }

    /// <summary>A click: the voice muted, or heard again.</summary>
    public event Action<int, bool>? MuteToggled;

    /// <summary>A right-click: the voice to be heard alone - or, alone already, with the rest again.</summary>
    public event Action<int>? SoloAsked;

    public bool Mutable => _mutable;

    /// <summary>Whether the voice is muted - set by the window too, for a solo.</summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value)
                return;
            _muted = value;
            InvalidateVisual();
        }
    }

    public void Update(IScopeSource engine, long atSample)
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
        var properties = e.GetCurrentPoint(this).Properties;
        if (_mutable && properties.IsLeftButtonPressed)
        {
            _muted = !_muted;
            MuteToggled?.Invoke(_voice, _muted);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (_mutable && properties.IsRightButtonPressed)
        {
            SoloAsked?.Invoke(_voice);
            e.Handled = true;
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
        context.FillRectangle(SidPlayerPalette.Brush(Color.FromRgb(0x1A, 0x1A, 0x28)), new Rect(1, 1, 3, bounds.Height - 2));
        double level = bounds.Height * Math.Clamp(_level, 0f, 1f);
        if (level > 1 && !_muted)
            context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(_colour, 190)), new Rect(1, bounds.Height - 1 - level, 3, level));

        if (!_muted)
            DrawTrace(context, bounds, midY);

        context.DrawRectangle(_border, bounds.Deflate(0.5));

        if (_label is null || _labelFor != (_muted, TextScale, bounds.Width))
        {
            _labelFor = (_muted, TextScale, bounds.Width);
            _label?.Dispose();
            // The longest that fits: the name and MUTED, the name, the short name - which is drawn
            // even where it does not fit, as the least there is to say.
            var brush = SidPlayerPalette.Brush(_muted ? SidPlayerPalette.ControlDisabled : SidPlayerPalette.WithAlpha(_colour, 170));
            string[] texts = _muted ? [$"{_name}  MUTED", _name, _shortName] : [_name, _shortName];
            foreach (string text in texts)
            {
                _label?.Dispose();
                _label = new TextLayout(text, LabelFace, 10 * TextScale, brush);
                if (_label.Width + 12 <= bounds.Width)
                    break;
            }
        }
        _label.Draw(context, new Point(8, bounds.Height - _label.Height - 4));
    }

    private void DrawTrace(DrawingContext context, Rect bounds, double midY)
    {
        int start = FindTrigger();
        double scale = (bounds.Height / 2) - 6;
        double step = (bounds.Width - 10) / (VisibleSamples - 1);

        // Where the samples are closer than a pixel apart, only each pixel column's lowest and
        // highest, in the order they come: the same picture, with fewer lines to stroke - a module's
        // spiky samples cost three times a smooth wave's.
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            bool begun = false;
            void To(int i)
            {
                double value = Math.Clamp(_samples[start + i], -1f, 1f);
                var point = new Point(6 + (i * step), midY - (value * scale));
                if (begun)
                    path.LineTo(point);
                else
                {
                    path.BeginFigure(point, false);
                    begun = true;
                }
            }

            int column = int.MinValue, low = 0, high = 0;
            for (int i = 0; i < VisibleSamples; i++)
            {
                int at = (int)(i * step);
                if (at != column)
                {
                    if (column != int.MinValue)
                        Flush(low, high);
                    (column, low, high) = (at, i, i);
                }
                else if (_samples[start + i] < _samples[start + low])
                    low = i;
                else if (_samples[start + i] > _samples[start + high])
                    high = i;
            }
            Flush(low, high);

            void Flush(int a, int b)
            {
                To(Math.Min(a, b));
                if (a != b)
                    To(Math.Max(a, b));
            }

            path.EndFigure(false);
        }

        context.DrawGeometry(null, _glow, geometry);
        context.DrawGeometry(null, _trace, geometry);
    }
}
