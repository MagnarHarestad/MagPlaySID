using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using MagPlaySid.Player;
using Avalonia.Rendering;

namespace MagPlaySid;

/// <summary>
/// How far apart an Amiga module's left and right are, beside the volume: two dots on a line, the
/// channels' two sides, together in the middle for mono and at the ends for the Amiga's own hard
/// stereo - anywhere between. Dragged or clicked along the line, or scrolled; a double-click goes
/// back to the usual. Its word above - Mono, Stereo, or the number while the pointer is on it.
/// Drawn, as the knob is, and only when something about it has changed.
/// </summary>
internal sealed class StereoSlider : Control, ICustomHitTest
{
    /// <summary>Under the pointer anywhere in its bounds: tested so, not against each of the
    /// lines and shapes it last drew - every move of the mouse tested them all.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>Where it stands until it is moved: the Amiga's channels a good deal apart, but not
    /// each in one ear alone.</summary>
    public const double Usual = 0.6;

    private const double SliderWidth = 78;
    private const double SliderHeight = 38;
    private const double Inset = 7;
    private const double LineY = 27;
    private const double Dot = 4;

    private static readonly Color Accent = SidPlayerPalette.VoiceColour(1);
    private static readonly IPen TrackPen = RoundPen(SidPlayerPalette.PanelBorder);
    private static readonly IPen LevelPen = RoundPen(Accent);
    private static readonly Typeface WordFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private readonly Dictionary<string, TextLayout> _words = [];
    private double _value = Usual;
    private bool _hovered;
    private bool _dragging;

    public StereoSlider()
    {
        Width = SliderWidth;
        Height = SliderHeight;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        AutomationProperties.SetName(this, "Stereo");
        ShowValue();
    }

    /// <summary>The width changed: while it is dragged, or on a click.</summary>
    public event Action? Changed;

    /// <summary>The width has come to rest, to be kept: a drag let go, a scroll, a double-click.</summary>
    public event Action? Committed;

    /// <summary>Where a double-click puts it back: <see cref="Usual"/> for an Amiga module, full
    /// width for one panned by its instruments.</summary>
    public double Reset { get; set; } = Usual;

    /// <summary>Left and right apart: 0 mono, 1 the Amiga's own stereo.</summary>
    public double Value
    {
        get => _value;
        set
        {
            value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : Usual;
            if (value == _value)
                return;
            _value = value;
            ShowValue();
        }
    }

    private int Percent => (int)Math.Round(_value * 100);

    private void ShowValue()
    {
        ToolTip.SetTip(this, (Percent == 0 ? "Mono" : $"Stereo {Percent}%") +
            " - the module's left and right: drag towards the middle for mono, out to the ends for the Amiga's own stereo. Double-click for the usual 60%.");
        InvalidateVisual();
    }

    private double Half => SliderWidth / 2 - Inset;

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
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            Move(Reset);
            Committed?.Invoke();
            return;
        }
        _dragging = true;
        e.Pointer.Capture(this);
        MoveTo(e.GetPosition(this).X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
            MoveTo(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        Committed?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragging)
        {
            _dragging = false;
            InvalidateVisual();
            Committed?.Invoke();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Move(_value + (e.Delta.Y + e.Delta.X) * 0.05);
        e.Handled = true;
        Committed?.Invoke();
    }

    /// <summary>The dots where the pointer is: as far apart as it is from the middle, on either side.</summary>
    private void MoveTo(double x) => Move(Math.Abs(x - SliderWidth / 2) / Half);

    private void Move(double value)
    {
        double before = _value;
        Value = value;
        if (_value != before)
            Changed?.Invoke();
    }

    public override void Render(DrawingContext context)
    {
        double centre = SliderWidth / 2, apart = _value * Half;
        // Its whole area takes the pointer, not only the thin line and the dots drawn.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        context.DrawLine(TrackPen, new Point(Inset, LineY), new Point(SliderWidth - Inset, LineY));
        if (apart > 0.5)
            context.DrawLine(LevelPen, new Point(centre - apart, LineY), new Point(centre + apart, LineY));

        bool active = _hovered || _dragging;
        var fill = SidPlayerPalette.Brush(SidPlayerPalette.Title);
        var edge = SidPlayerPalette.Pen(active ? SidPlayerPalette.WithAlpha(Accent, 200) : SidPlayerPalette.PanelBackground);
        context.DrawEllipse(fill, edge, new Point(centre - apart, LineY), Dot, Dot);
        context.DrawEllipse(fill, edge, new Point(centre + apart, LineY), Dot, Dot);

        var word = Word(active ? (Percent == 0 ? "Mono" : $"{Percent}%") : Percent == 0 ? "Mono" : "Stereo",
                        active ? SidPlayerPalette.Title : SidPlayerPalette.Subtitle);
        word.Draw(context, new Point(centre - word.Width / 2, 3));
    }

    /// <summary>A word, laid out once: dragging from end to end draws each number.</summary>
    private TextLayout Word(string text, Color ink)
    {
        string key = text + (ink == SidPlayerPalette.Title ? "!" : "");
        if (!_words.TryGetValue(key, out var word))
            _words[key] = word = new TextLayout(text, WordFace, 10.5, SidPlayerPalette.Brush(ink));
        return word;
    }

    private static IPen RoundPen(Color colour) =>
        new ImmutablePen(new ImmutableSolidColorBrush(colour), 3, lineCap: PenLineCap.Round);
}
