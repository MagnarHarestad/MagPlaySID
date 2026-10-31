using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using MagPlaySid.Player;

namespace MagPlaySid;

/// <summary>
/// The volume, as a knob: a ring that fills in the accent from lower left round to lower right, a
/// dark cap with a dot pointing to where it stands and a speaker on it - its number while the
/// pointer is on it. Dragged up or down or scrolled it turns; a click without a drag mutes it.
/// Drawn, as the transport is, and only when something about it has changed.
/// </summary>
internal sealed class VolumeKnob : Control
{
    private const double Size = 38;
    private const double Ring = 3;

    /// <summary>The ring runs from this many degrees before twelve o'clock to as many after it.</summary>
    private const double Sweep = 135;

    /// <summary>How far a drag goes from silence to full.</summary>
    private const double DragRange = 150;

    /// <summary>How far a drag goes before it is a drag and not a click.</summary>
    private const double DragStart = 3;

    private static readonly Color Accent = SidPlayerPalette.VoiceColour(1);
    private static readonly IPen TrackPen = RoundPen(SidPlayerPalette.PanelBorder);
    private static readonly IPen LevelPen = RoundPen(Accent);
    private static readonly IPen MutedPen = RoundPen(SidPlayerPalette.ControlDisabled);
    private static readonly IPen WavePen = RoundPen(SidPlayerPalette.Control, 1.1);
    private static readonly IPen SlashPen = RoundPen(SidPlayerPalette.ControlDisabled, 1.3);
    private static readonly Geometry Speaker = Geometry.Parse("M-5,-1.8 H-2.6 L0.6,-4.4 V4.4 L-2.6,1.8 H-5 Z");
    private static readonly Geometry WaveNear = Geometry.Parse("M2.2,-2 A2.9,2.9 0 0 1 2.2,2");
    private static readonly Geometry WaveFar = Geometry.Parse("M3.8,-3.6 A5.2,5.2 0 0 1 3.8,3.6");
    private static readonly Typeface NumberFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private readonly Dictionary<int, TextLayout> _numbers = [];
    private readonly Geometry _track;
    private Geometry? _level;
    private double _value = 1;
    private bool _muted;
    private bool _hovered;
    private bool _pressed;
    private bool _dragging;
    private double _pressY;
    private double _pressValue;

    public VolumeKnob()
    {
        Width = Height = Size;
        Cursor = new Cursor(StandardCursorType.Hand);
        _track = Arc(1);
        _level = Arc(_value);
        AutomationProperties.SetName(this, "Volume");
        ShowValue();
    }

    /// <summary>The volume changed: while it turns, or on a click.</summary>
    public event Action? Changed;

    /// <summary>The volume has come to rest, to be kept: a drag let go, a scroll, a click.</summary>
    public event Action? Committed;

    /// <summary>Where the knob stands, 0-1.</summary>
    public double Value
    {
        get => _value;
        set
        {
            value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;
            if (value == _value)
                return;
            _value = value;
            _level = value > 0 ? Arc(value) : null;
            ShowValue();
        }
    }

    public bool Muted => _muted;

    /// <summary>What the sound is multiplied by: the knob's square, as an ear hears loudness, so
    /// half way round is a good deal quieter and not nearly as loud.</summary>
    public float Gain => _muted ? 0f : (float)(_value * _value);

    private void ShowValue()
    {
        ToolTip.SetTip(this, _muted ? "Muted - click to turn the sound on" : $"Volume {Math.Round(_value * 100)}% - drag or scroll, click to mute");
        InvalidateVisual();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hovered = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _pressed = true;
        _dragging = false;
        _pressY = e.GetPosition(this).Y;
        _pressValue = _value;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed)
            return;
        double moved = _pressY - e.GetPosition(this).Y;
        if (!_dragging && Math.Abs(moved) < DragStart)
            return;
        _dragging = true;
        Turn(_pressValue + moved / DragRange);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed)
            return;
        _pressed = false;
        e.Pointer.Capture(null);
        if (!_dragging)
            ToggleMute();
        _dragging = false;
        InvalidateVisual();
        Committed?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pressed)
        {
            _pressed = _dragging = false;
            InvalidateVisual();
            Committed?.Invoke();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Turn(_value + (e.Delta.Y + e.Delta.X) * 0.05);
        e.Handled = true;
        Committed?.Invoke();
    }

    /// <summary>Turned by the user: the sound comes back if it was muted.</summary>
    private void Turn(double value)
    {
        _muted = false;
        Value = value;
        ShowValue();
        Changed?.Invoke();
    }

    private void ToggleMute()
    {
        _muted = !_muted;
        // Nothing to come back to at zero: half way round, rather than a click that does nothing.
        if (!_muted && _value <= 0)
            Value = 0.5;
        ShowValue();
        Changed?.Invoke();
    }

    public override void Render(DrawingContext context)
    {
        var centre = new Point(Size / 2, Size / 2);
        var active = _hovered || _dragging;

        context.DrawGeometry(null, TrackPen, _track);
        if (_level is not null)
            context.DrawGeometry(null, _muted ? MutedPen : LevelPen, _level);

        double cap = Size / 2 - Ring - 3.5;
        var edge = active ? SidPlayerPalette.WithAlpha(Accent, 150) : SidPlayerPalette.PanelBorder;
        context.DrawEllipse(SidPlayerPalette.Brush(SidPlayerPalette.PanelBackground), SidPlayerPalette.Pen(edge), centre, cap, cap);

        if (active && !_muted)
        {
            var number = Number((int)Math.Round(_value * 100));
            number.Draw(context, new Point(centre.X - number.Width / 2, centre.Y - number.Height / 2));
            return;
        }

        double angle = (_value * 2 - 1) * Sweep * Math.PI / 180;
        var dot = new Point(centre.X + (cap - 2.6) * Math.Sin(angle), centre.Y - (cap - 2.6) * Math.Cos(angle));
        context.DrawEllipse(SidPlayerPalette.Brush(_muted ? SidPlayerPalette.ControlDisabled : SidPlayerPalette.Title), null, dot, 1.3, 1.3);

        using (context.PushTransform(Matrix.CreateTranslation(centre.X - 0.4, centre.Y)))
        {
            context.DrawGeometry(SidPlayerPalette.Brush(_muted ? SidPlayerPalette.ControlDisabled : SidPlayerPalette.Control), null, Speaker);
            if (_muted || _value <= 0)
                context.DrawLine(SlashPen, new Point(1.8, -2.2), new Point(5.2, 2.2));
            else
            {
                context.DrawGeometry(null, WavePen, WaveNear);
                if (_value > 0.5)
                    context.DrawGeometry(null, WavePen, WaveFar);
            }
        }
    }

    /// <summary>A number's text, laid out once: dragging from end to end draws each of them.</summary>
    private TextLayout Number(int value)
    {
        if (!_numbers.TryGetValue(value, out var number))
            _numbers[value] = number = new TextLayout(value.ToString(), NumberFace, value >= 100 ? 9.5 : 10.5, SidPlayerPalette.Brush(SidPlayerPalette.Title));
        return number;
    }

    /// <summary>The ring from its start round to <paramref name="value"/> of the way.</summary>
    private static Geometry Arc(double value)
    {
        double radius = Size / 2 - Ring / 2 - 0.5;
        Point At(double degrees) => new(
            Size / 2 + radius * Math.Sin(degrees * Math.PI / 180),
            Size / 2 - radius * Math.Cos(degrees * Math.PI / 180));

        double end = -Sweep + value * 2 * Sweep;
        var arc = new StreamGeometry();
        using (var path = arc.Open())
        {
            path.BeginFigure(At(-Sweep), false);
            path.ArcTo(At(end), new Size(radius, radius), 0, end + Sweep > 180, SweepDirection.Clockwise);
            path.EndFigure(false);
        }
        return arc;
    }

    private static IPen RoundPen(Color colour, double thickness = Ring) =>
        new ImmutablePen(new ImmutableSolidColorBrush(colour), thickness, lineCap: PenLineCap.Round);
}
