// System.Drawing.Drawing2D and System.Drawing.Text, as far as the owner-drawn controls use them.
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvPoint = Avalonia.Point;
using AvSize = Avalonia.Size;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using PointF = System.Drawing.PointF;
using Size = System.Drawing.Size;
using SizeF = System.Drawing.SizeF;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;
using Brush = System.Drawing.Brush;

namespace System.Drawing.Drawing2D
{
    public enum SmoothingMode
    {
        Invalid = -1,
        Default = 0,
        HighSpeed = 1,
        HighQuality = 2,
        None = 3,
        AntiAlias = 4,
    }

    public enum DashStyle
    {
        Solid,
        Dash,
        Dot,
        DashDot,
        DashDotDot,
        Custom,
    }

    public enum LineCap
    {
        Flat = 0,
        Square = 1,
        Round = 2,
        Triangle = 3,
    }

    public enum LineJoin
    {
        Miter,
        Bevel,
        Round,
        MiterClipped,
    }

    public enum CompositingQuality
    {
        Default,
        HighSpeed,
        HighQuality,
    }

    public enum InterpolationMode
    {
        Default,
        Low,
        High,
        NearestNeighbor = 5,
        HighQualityBicubic = 7,
    }

    public enum PixelOffsetMode
    {
        Default,
        HighSpeed,
        HighQuality,
        None,
        Half,
    }

    public enum LinearGradientMode
    {
        Horizontal,
        Vertical,
        ForwardDiagonal,
        BackwardDiagonal,
    }

    public sealed class GraphicsState
    {
        internal GraphicsState(int depth) => Depth = depth;

        internal int Depth { get; }
    }

    public sealed class LinearGradientBrush : Brush
    {
        private readonly RectangleF _rect;
        private readonly Color _from;
        private readonly Color _to;
        private readonly LinearGradientMode _mode;
        private IBrush? _brush;

        public LinearGradientBrush(Rectangle rect, Color from, Color to, LinearGradientMode mode)
            : this((RectangleF)rect, from, to, mode)
        {
        }

        public LinearGradientBrush(RectangleF rect, Color from, Color to, LinearGradientMode mode)
        {
            _rect = rect;
            _from = from;
            _to = to;
            _mode = mode;
        }

        public LinearGradientBrush(Rectangle rect, Color from, Color to, float angle)
            : this(rect, from, to, angle is > 45 and < 135 ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal)
        {
        }

        public LinearGradientBrush(RectangleF rect, Color from, Color to, float angle)
            : this(rect, from, to, angle is > 45 and < 135 ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal)
        {
        }

        public LinearGradientBrush(PointF a, PointF b, Color from, Color to)
            : this(new RectangleF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(1, Math.Abs(b.X - a.X)), Math.Max(1, Math.Abs(b.Y - a.Y))),
                from, to, Math.Abs(b.Y - a.Y) > Math.Abs(b.X - a.X) ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal)
        {
        }

        internal override IBrush ToAvalonia() => _brush ??= new ImmutableLinearGradientBrush(
            [new ImmutableGradientStop(0, Shim.ToAvalonia(_from)), new ImmutableGradientStop(1, Shim.ToAvalonia(_to))],
            startPoint: new Avalonia.RelativePoint(_rect.X, _rect.Y, Avalonia.RelativeUnit.Absolute),
            endPoint: _mode switch
            {
                LinearGradientMode.Vertical => new Avalonia.RelativePoint(_rect.X, _rect.Bottom, Avalonia.RelativeUnit.Absolute),
                LinearGradientMode.Horizontal => new Avalonia.RelativePoint(_rect.Right, _rect.Y, Avalonia.RelativeUnit.Absolute),
                LinearGradientMode.ForwardDiagonal => new Avalonia.RelativePoint(_rect.Right, _rect.Bottom, Avalonia.RelativeUnit.Absolute),
                _ => new Avalonia.RelativePoint(_rect.X, _rect.Bottom, Avalonia.RelativeUnit.Absolute),
            });
    }

    /// <summary>A figure built from lines, arcs and curves, the way GDI+ builds one.</summary>
    public sealed class GraphicsPath : IDisposable
    {
        private readonly List<List<Action<StreamGeometryContext>>> _figures = [];
        private readonly List<bool> _closed = [];
        private readonly List<AvPoint> _starts = [];
        private AvPoint? _last;

        private List<Action<StreamGeometryContext>> Current
        {
            get
            {
                if (_figures.Count == 0 || _closed[^1])
                    StartFigure();
                return _figures[^1];
            }
        }

        public void StartFigure()
        {
            _figures.Add([]);
            _closed.Add(false);
            _starts.Add(default);
            _last = null;
        }

        private void MoveOrLine(AvPoint point)
        {
            var figure = Current;
            if (_last is null)
            {
                _starts[^1] = point;
                _last = point;
                return;
            }

            if (_last.Value != point)
                figure.Add(c => c.LineTo(point));
            _last = point;
        }

        public void AddLine(float x1, float y1, float x2, float y2)
        {
            MoveOrLine(new AvPoint(x1, y1));
            MoveOrLine(new AvPoint(x2, y2));
        }

        public void AddLine(PointF a, PointF b) => AddLine(a.X, a.Y, b.X, b.Y);

        public void AddLines(PointF[] points)
        {
            foreach (var point in points)
                MoveOrLine(new AvPoint(point.X, point.Y));
        }

        public void AddPolygon(PointF[] points)
        {
            StartFigure();
            AddLines(points);
            CloseFigure();
        }

        public void AddPolygon(Point[] points) => AddPolygon(points.Select(p => (PointF)p).ToArray());

        public void AddRectangle(RectangleF rect)
        {
            StartFigure();
            AddLines([new(rect.Left, rect.Top), new(rect.Right, rect.Top), new(rect.Right, rect.Bottom), new(rect.Left, rect.Bottom)]);
            CloseFigure();
        }

        public void AddRectangle(Rectangle rect) => AddRectangle((RectangleF)rect);

        public void AddEllipse(RectangleF rect)
        {
            StartFigure();
            AddArc(rect.X, rect.Y, rect.Width, rect.Height, 0, 360);
            CloseFigure();
        }

        public void AddEllipse(float x, float y, float width, float height) => AddEllipse(new RectangleF(x, y, width, height));

        public void AddArc(Rectangle rect, float startAngle, float sweepAngle) => AddArc(rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
        public void AddArc(RectangleF rect, float startAngle, float sweepAngle) => AddArc(rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);
        public void AddArc(int x, int y, int width, int height, float startAngle, float sweepAngle) => AddArc((float)x, y, width, height, startAngle, sweepAngle);

        /// <summary>An arc of the ellipse in the rectangle, from <paramref name="startAngle"/>
        /// degrees clockwise through <paramref name="sweepAngle"/> - GDI+'s convention. Drawn as
        /// short line segments, which at the sizes these controls use cannot be told from a curve.</summary>
        public void AddArc(float x, float y, float width, float height, float startAngle, float sweepAngle)
        {
            double cx = x + (width / 2.0), cy = y + (height / 2.0), rx = width / 2.0, ry = height / 2.0;
            int steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(sweepAngle) / 6.0));
            for (int i = 0; i <= steps; i++)
            {
                double angle = (startAngle + (sweepAngle * i / steps)) * Math.PI / 180.0;
                MoveOrLine(new AvPoint(cx + (rx * Math.Cos(angle)), cy + (ry * Math.Sin(angle))));
            }
        }

        public void AddPie(float x, float y, float width, float height, float startAngle, float sweepAngle)
        {
            StartFigure();
            MoveOrLine(new AvPoint(x + (width / 2.0), y + (height / 2.0)));
            AddArc(x, y, width, height, startAngle, sweepAngle);
            CloseFigure();
        }

        public void AddBezier(PointF p1, PointF p2, PointF p3, PointF p4)
        {
            MoveOrLine(new AvPoint(p1.X, p1.Y));
            var (c1, c2, end) = (new AvPoint(p2.X, p2.Y), new AvPoint(p3.X, p3.Y), new AvPoint(p4.X, p4.Y));
            Current.Add(c => c.CubicBezierTo(c1, c2, end));
            _last = end;
        }

        public void CloseFigure()
        {
            if (_figures.Count > 0)
                _closed[^1] = true;
            _last = null;
        }

        public void CloseAllFigures()
        {
            for (int i = 0; i < _closed.Count; i++)
                _closed[i] = true;
            _last = null;
        }

        internal Geometry ToGeometry()
        {
            var geometry = new StreamGeometry();
            using var context = geometry.Open();
            for (int i = 0; i < _figures.Count; i++)
            {
                context.BeginFigure(_starts[i], isFilled: true);
                foreach (var segment in _figures[i])
                    segment(context);
                context.EndFigure(_closed[i]);
            }

            return geometry;
        }

        public void Dispose()
        {
        }
    }
}

namespace System.Drawing.Text
{
    public enum TextRenderingHint
    {
        SystemDefault,
        SingleBitPerPixelGridFit,
        SingleBitPerPixel,
        AntiAliasGridFit,
        AntiAlias,
        ClearTypeGridFit,
    }
}

namespace System.Media
{
    /// <summary>The system beep, where a tracker refuses a keystroke.</summary>
    public static class SystemSounds
    {
        public static Sound Beep { get; } = new();
        public static Sound Asterisk { get; } = new();
        public static Sound Exclamation { get; } = new();

        public sealed class Sound
        {
            public void Play()
            {
            }
        }
    }
}
