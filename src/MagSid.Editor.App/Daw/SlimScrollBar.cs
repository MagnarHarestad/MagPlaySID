using System.Drawing.Drawing2D;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// A slim dark scroll bar: a rounded thumb on a flat track, no arrow buttons, lighter under the
/// pointer and lighter still while dragged. The native one is light-themed and cannot be restyled,
/// and a grey Windows bar along the bottom of the piano roll is the one part of the DAW view that
/// looks like a different program. Ported from MagSpriteEd's DarkScrollBar, with both orientations
/// and this view's own colours.
///
/// Deliberately mimics <see cref="ScrollBar"/>'s own API - Minimum, Maximum, LargeChange,
/// SmallChange, Value and a Scroll event, with Value clamped to Maximum - LargeChange + 1. The
/// views that use it do a good deal of arithmetic against those properties, and a control with
/// tidier semantics would have meant rewriting every one of those sums for no visible gain.
/// </summary>
internal sealed class SlimScrollBar : Control
{
    private const int MinThumb = 28;
    private const int Inset = 3;

    private static readonly Color Track = Color.FromArgb(0x0C, 0x0F, 0x17);
    private static readonly Color ThumbIdle = Color.FromArgb(0x3C, 0x4A, 0x6C);
    private static readonly Color ThumbHover = Color.FromArgb(0x55, 0x68, 0x94);
    private static readonly Color ThumbDrag = Color.FromArgb(0x72, 0x8B, 0xC2);

    private readonly Orientation _orientation;

    private int _value;
    private int _minimum;
    private int _maximum = 100;
    private int _largeChange = 10;
    private bool _dragging;
    private bool _hovered;
    private int _dragStart;
    private int _dragStartValue;

    public SlimScrollBar(Orientation orientation)
    {
        _orientation = orientation;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Track;
        Cursor = Cursors.Hand;
    }

    /// <summary>Same shape as <see cref="ScrollBar.Scroll"/>, so a call site that only invalidates
    /// reads identically whichever bar it is wired to.</summary>
    public event EventHandler? Scroll;

    public int Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            Value = _value;
            Invalidate();
        }
    }

    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            Value = _value;
            Invalidate();
        }
    }

    /// <summary>The viewport, in the same units as <see cref="Maximum"/>. Also what one click on
    /// the track pages by, and what sets the thumb's length.</summary>
    public int LargeChange
    {
        get => _largeChange;
        set
        {
            _largeChange = Math.Max(1, value);
            Value = _value;
            Invalidate();
        }
    }

    public int SmallChange { get; set; } = 1;

    /// <summary>The last position the thumb can reach - <see cref="ScrollBar"/>'s own rule, where
    /// Maximum is the end of the content rather than the end of the travel.</summary>
    private int MaxValue => Math.Max(_minimum, _maximum - _largeChange + 1);

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, _minimum, MaxValue);
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            Invalidate();
            Scroll?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool CanScroll => Enabled && MaxValue > _minimum;

    private bool Horizontal => _orientation == Orientation.Horizontal;

    private int TrackLength => Math.Max(1, (Horizontal ? Width : Height) - (Inset * 2));

    private Rectangle ThumbRect()
    {
        int track = TrackLength;
        int span = Math.Max(1, _maximum - _minimum);
        int thumb = Math.Clamp((int)((long)track * _largeChange / span), MinThumb, track);
        int travel = track - thumb;
        int travelled = MaxValue == _minimum
            ? 0
            : (int)((long)travel * (_value - _minimum) / (MaxValue - _minimum));

        return Horizontal
            ? new Rectangle(Inset + travelled, Inset, thumb, Math.Max(1, Height - (Inset * 2)))
            : new Rectangle(Inset, Inset + travelled, Math.Max(1, Width - (Inset * 2)), thumb);
    }

    private int Along(Point point) => Horizontal ? point.X : point.Y;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        if (!CanScroll)
        {
            return;
        }

        var thumb = ThumbRect();
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = Rounded(thumb, Math.Min(5, (Horizontal ? thumb.Height : thumb.Width) / 2));
        using var fill = new SolidBrush(_dragging ? ThumbDrag : _hovered ? ThumbHover : ThumbIdle);
        g.FillPath(fill, path);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, radius) * 2;

        if (d >= Math.Min(r.Width, r.Height))
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left || !CanScroll)
        {
            return;
        }

        var thumb = ThumbRect();
        if (thumb.Contains(e.Location))
        {
            _dragging = true;
            _dragStart = Along(e.Location);
            _dragStartValue = _value;
            Capture = true;
            Invalidate();
            return;
        }

        // Page towards the click, the way the native bar does when the track is clicked.
        Value += Along(e.Location) < (Horizontal ? thumb.Left : thumb.Top) ? -_largeChange : _largeChange;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        bool hovered = CanScroll && ThumbRect().Contains(e.Location);
        if (hovered != _hovered)
        {
            _hovered = hovered;
            Invalidate();
        }

        if (!_dragging)
        {
            return;
        }

        var thumb = ThumbRect();
        int travel = TrackLength - (Horizontal ? thumb.Width : thumb.Height);
        if (travel <= 0)
        {
            return;
        }

        Value = _dragStartValue
                + (int)((long)(Along(e.Location) - _dragStart) * (MaxValue - _minimum) / travel);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hovered)
        {
            _hovered = false;
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Value -= Math.Sign(e.Delta) * Math.Max(1, SmallChange) * 3;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }
}
