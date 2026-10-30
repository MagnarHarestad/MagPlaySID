using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MagPlaySid.Player;

namespace MagPlaySid;

/// <summary>
/// One of a tune's songs, as a round button with its number - drawn, as the transport is, so it is
/// sharp at any size. The song playing is filled in the accent, a ring round it filling clockwise
/// as the song goes on; the others light their edge under the pointer.
/// </summary>
internal sealed class SongPill : Control
{
    private static readonly Typeface Mono = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"));
    private static readonly Typeface MonoBold = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"), FontStyle.Normal, FontWeight.Bold);

    private readonly double _scale;
    private bool _lit;
    private double _progress;
    private bool _hovered;
    private bool _pressed;

    /// <summary>The number, laid out once for each look it takes - lit, hovered, plain.</summary>
    private readonly Dictionary<(bool Lit, bool Hovered), TextLayout> _numbers = [];
    private IPen? _arc;

    public SongPill(int song, double scale)
    {
        Song = song;
        _scale = scale;
        Width = Height = 26 * scale;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public int Song { get; }

    /// <summary>A click on it: play this song.</summary>
    public event Action<int>? Click;

    /// <summary>The song playing.</summary>
    public bool Lit
    {
        get => _lit;
        set
        {
            if (_lit == value)
                return;
            _lit = value;
            InvalidateVisual();
        }
    }

    /// <summary>How far into the song the listener is, 0-1 - drawn as the ring round it.</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(value - _progress) < 0.002)
                return;
            _progress = value;
            InvalidateVisual();
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        _hovered = true;
        InvalidateVisual();
        base.OnPointerEntered(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hovered = _pressed = false;
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        InvalidateVisual();
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        bool click = _pressed && _hovered;
        _pressed = false;
        InvalidateVisual();
        base.OnPointerReleased(e);
        if (click)
            Click?.Invoke(Song);
    }

    public override void Render(DrawingContext context)
    {
        var accent = SidPlayerPalette.VoiceColour(1);
        double size = Math.Min(Bounds.Width, Bounds.Height);
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double ring = 2 * _scale;
        double radius = size / 2 - ring / 2 - (_pressed ? 1 : 0);

        if (_lit)
        {
            // A disc in the accent inside a dark track, the song's progress going round the track.
            context.DrawEllipse(null, SidPlayerPalette.Pen(SidPlayerPalette.PanelBorder, ring), centre, radius, radius);
            double disc = radius - ring * 1.5;
            context.DrawEllipse(SidPlayerPalette.Brush(accent), null, centre, disc, disc);
        }
        else
        {
            var fill = _hovered ? SidPlayerPalette.WithAlpha(accent, 40) : SidPlayerPalette.PanelBackground;
            var edge = _hovered ? SidPlayerPalette.WithAlpha(accent, 170) : SidPlayerPalette.PanelBorder;
            context.DrawEllipse(SidPlayerPalette.Brush(fill), SidPlayerPalette.Pen(edge, ring), centre, radius, radius);
        }

        // The song's progress, clockwise from the top, in the accent on the dark track.
        if (_lit && _progress > 0)
        {
            var pen = _arc ??= new Avalonia.Media.Immutable.ImmutablePen(
                (Avalonia.Media.IImmutableBrush)SidPlayerPalette.Brush(accent), ring, lineCap: PenLineCap.Round);
            if (_progress >= 0.999)
                context.DrawEllipse(null, pen, centre, radius, radius);
            else
            {
                double angle = _progress * 2 * Math.PI;
                var end = new Point(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));
                var arc = new StreamGeometry();
                using (var path = arc.Open())
                {
                    path.BeginFigure(new Point(centre.X, centre.Y - radius), false);
                    path.ArcTo(end, new Size(radius, radius), 0, _progress > 0.5, SweepDirection.Clockwise);
                    path.EndFigure(false);
                }
                context.DrawGeometry(null, pen, arc);
            }
        }

        if (!_numbers.TryGetValue((_lit, _hovered), out var text))
        {
            var ink = _lit ? SidPlayerPalette.Background : _hovered ? SidPlayerPalette.ControlHover : SidPlayerPalette.Control;
            _numbers[(_lit, _hovered)] = text = new TextLayout(Song.ToString(CultureInfo.InvariantCulture), _lit ? MonoBold : Mono,
                (Song >= 100 ? 9.5 : Song >= 10 ? 11 : 12) * _scale, SidPlayerPalette.Brush(ink));
        }
        text.Draw(context, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }
}
