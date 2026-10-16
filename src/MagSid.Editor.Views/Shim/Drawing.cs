// The part of System.Drawing the editor's owner-drawn controls paint with - brushes, pens, fonts and
// a Graphics - drawn through Avalonia instead of GDI+, so the same control source runs on a Mac.
// Color, Point, Size and Rectangle are the real System.Drawing.Primitives types, which .NET ships on
// every system; only what GDI+ itself provided is stood in for here.
using System.Drawing.Drawing2D;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvColor = Avalonia.Media.Color;
using AvPoint = Avalonia.Point;
using AvRect = Avalonia.Rect;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using PointF = System.Drawing.PointF;
using Size = System.Drawing.Size;
using SizeF = System.Drawing.SizeF;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;
using DashStyle = System.Drawing.Drawing2D.DashStyle;
using LineCap = System.Drawing.Drawing2D.LineCap;
using LineJoin = System.Drawing.Drawing2D.LineJoin;
using FontStyle = System.Drawing.FontStyle;
using Brush = System.Drawing.Brush;
using Pen = System.Drawing.Pen;
using Brushes = System.Drawing.Brushes;

namespace System.Drawing;

public abstract class Brush : IDisposable
{
    internal abstract IBrush ToAvalonia();

    public void Dispose()
    {
    }
}

public sealed class SolidBrush(Color color) : Brush
{
    private IBrush? _brush;

    public Color Color { get; } = color;

    internal override IBrush ToAvalonia() => _brush ??= new ImmutableSolidColorBrush(Shim.ToAvalonia(Color));
}

public static class Brushes
{
    public static Brush Transparent { get; } = new SolidBrush(Color.Transparent);
    public static Brush Black { get; } = new SolidBrush(Color.Black);
    public static Brush White { get; } = new SolidBrush(Color.White);
}

public sealed class Pen : IDisposable
{
    private IPen? _pen;

    public Pen(Color color, float width = 1f)
    {
        Color = color;
        Width = width;
    }

    public Pen(Brush brush, float width = 1f)
        : this(brush is SolidBrush solid ? solid.Color : Color.White, width)
    {
    }

    public Color Color { get; }
    public float Width { get; }
    public DashStyle DashStyle { get => _dash; set { _dash = value; _pen = null; } }
    public LineCap StartCap { get; set; }
    public LineCap EndCap { get => _endCap; set { _endCap = value; _pen = null; } }
    public LineJoin LineJoin { get => _join; set { _join = value; _pen = null; } }
    public float[]? DashPattern { get => _pattern; set { _pattern = value; _pen = null; } }

    private DashStyle _dash;
    private LineCap _endCap;
    private LineJoin _join;
    private float[]? _pattern;

    internal IPen ToAvalonia() => _pen ??= new ImmutablePen(
        new ImmutableSolidColorBrush(Shim.ToAvalonia(Color)), Width,
        _pattern is { Length: > 0 } custom ? new ImmutableDashStyle(custom.Select(v => (double)v), 0)
        : _dash switch
        {
            DashStyle.Dash => new ImmutableDashStyle([3, 1], 0),
            DashStyle.Dot => new ImmutableDashStyle([1, 1], 0),
            DashStyle.DashDot => new ImmutableDashStyle([3, 1, 1, 1], 0),
            _ => null,
        },
        _endCap == LineCap.Round ? PenLineCap.Round : _endCap == LineCap.Square ? PenLineCap.Square : PenLineCap.Flat,
        _join == LineJoin.Round ? PenLineJoin.Round : _join == LineJoin.Bevel ? PenLineJoin.Bevel : PenLineJoin.Miter);

    public void Dispose()
    {
    }
}

[Flags]
public enum FontStyle
{
    Regular = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strikeout = 8,
}

public enum GraphicsUnit
{
    World,
    Display,
    Pixel,
    Point,
}

public sealed class Font : IDisposable
{
    private static readonly FontFamily MonoFamily = new("Consolas, Menlo, DejaVu Sans Mono, monospace");
    private static readonly FontFamily UiFamily = new("Segoe UI, Inter, Helvetica Neue, Arial, sans-serif");
    private static readonly FontFamily IconFamily = new("Segoe MDL2 Assets, Segoe UI Symbol, Apple Symbols, sans-serif");

    public Font(string name, float emSize, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
    {
        Name = name;
        Size = emSize;
        Style = style;
        Unit = unit;
        var family = name switch
        {
            "Consolas" or "Courier New" or "Lucida Console" => MonoFamily,
            "Segoe MDL2 Assets" => IconFamily,
            _ => name is "Segoe UI" or "Microsoft Sans Serif" or "Tahoma" ? UiFamily : new FontFamily($"{name}, Inter, sans-serif"),
        };
        Typeface = new Typeface(family,
            style.HasFlag(FontStyle.Italic) ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal,
            style.HasFlag(FontStyle.Bold) ? FontWeight.Bold : FontWeight.Normal);

        // GDI+ sizes fonts in points at 96 DPI; Avalonia in device-independent pixels.
        PixelSize = unit == GraphicsUnit.Pixel ? emSize : emSize * 96f / 72f;
    }

    public Font(Font prototype, FontStyle style)
        : this(prototype.Name, prototype.Size, style, prototype.Unit)
    {
    }

    public string Name { get; }
    public float Size { get; }
    public float SizeInPoints => Unit == GraphicsUnit.Pixel ? Size * 72f / 96f : Size;
    public FontStyle Style { get; }
    public GraphicsUnit Unit { get; }
    public bool Bold => Style.HasFlag(FontStyle.Bold);
    public int Height => (int)Math.Ceiling(PixelSize * 1.2f);

    internal Typeface Typeface { get; }
    internal float PixelSize { get; }

    public void Dispose()
    {
    }
}

public enum StringAlignment
{
    Near,
    Center,
    Far,
}

[Flags]
public enum StringFormatFlags
{
    None = 0,
    NoWrap = 0x1000,
    NoClip = 0x4000,
    LineLimit = 0x2000,
}

public enum StringTrimming
{
    None,
    Character,
    Word,
    EllipsisCharacter,
}

public sealed class StringFormat : IDisposable
{
    public StringFormat()
    {
    }

    public StringFormat(StringFormat other)
    {
        Alignment = other.Alignment;
        LineAlignment = other.LineAlignment;
        FormatFlags = other.FormatFlags;
        Trimming = other.Trimming;
    }

    public static StringFormat GenericTypographic => new();
    public static StringFormat GenericDefault => new();

    public StringAlignment Alignment { get; set; }
    public StringAlignment LineAlignment { get; set; }
    public StringFormatFlags FormatFlags { get; set; }
    public StringTrimming Trimming { get; set; }

    public void Dispose()
    {
    }
}

public sealed class Region(RectangleF bounds)
{
    public Region(Rectangle bounds)
        : this((RectangleF)bounds)
    {
    }

    public RectangleF Bounds { get; } = bounds;

    public RectangleF GetBounds(Graphics g) => Bounds;
}

/// <summary>
/// A GDI+ Graphics, painting through an Avalonia DrawingContext. Thin lines land on whole pixels the
/// way GDI+ puts them, half a pixel in, so a one-pixel grid stays crisp rather than smeared over
/// two rows; text is laid out with Avalonia's FormattedText and cached, since a tracker repaints
/// the same few strings sixty times a second.
/// </summary>
public sealed class Graphics : IDisposable
{
    private readonly DrawingContext _context;
    private readonly Stack<(int Depth, RectangleF? Clip)> _saved = new();
    private readonly Stack<DrawingContext.PushedState> _pushed = new();
    private RectangleF? _clip;

    internal Graphics(DrawingContext context, float width, float height)
    {
        _context = context;
        VisibleClipBounds = new RectangleF(0, 0, width, height);
    }

    public SmoothingMode SmoothingMode { get; set; }
    public Text.TextRenderingHint TextRenderingHint { get; set; }
    public Drawing2D.CompositingQuality CompositingQuality { get; set; }
    public Drawing2D.InterpolationMode InterpolationMode { get; set; }
    public Drawing2D.PixelOffsetMode PixelOffsetMode { get; set; }
    public RectangleF VisibleClipBounds { get; }

    public Region Clip
    {
        get => new(_clip ?? VisibleClipBounds);
        set => SetClip(value.Bounds);
    }

    public RectangleF ClipBounds => _clip ?? VisibleClipBounds;

    // ---------------------------------------------------------------- state

    public GraphicsState Save()
    {
        var state = new GraphicsState(_pushed.Count);
        _saved.Push((_pushed.Count, _clip));
        return state;
    }

    public void Restore(GraphicsState state)
    {
        while (_saved.Count > 0)
        {
            var (depth, clip) = _saved.Pop();
            PopTo(depth);
            _clip = clip;
            if (depth == state.Depth)
                break;
        }
    }

    public void SetClip(Rectangle rect) => SetClip((RectangleF)rect);

    public void SetClip(RectangleF rect)
    {
        // GDI+ replaces the clip; Avalonia's nest. Drop back to before this clip, then push it.
        int floor = _saved.Count > 0 ? _saved.Peek().Depth : 0;
        PopTo(floor);
        _clip = rect;
        _pushed.Push(_context.PushClip(new AvRect(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height))));
    }

    public void SetClip(Region region) => SetClip(region.Bounds);

    public void ResetClip()
    {
        int floor = _saved.Count > 0 ? _saved.Peek().Depth : 0;
        PopTo(floor);
        _clip = null;
    }

    public void IntersectClip(Rectangle rect) => SetClip(_clip is { } clip ? RectangleF.Intersect(clip, rect) : rect);

    private void PopTo(int depth)
    {
        while (_pushed.Count > depth)
            _pushed.Pop().Dispose();
    }

    public void TranslateTransform(float dx, float dy) =>
        _pushed.Push(_context.PushTransform(Avalonia.Matrix.CreateTranslation(dx, dy)));

    // ---------------------------------------------------------------- fills

    public void Clear(Color color) =>
        _context.FillRectangle(new ImmutableSolidColorBrush(Shim.ToAvalonia(color)), new AvRect(VisibleClipBounds.X, VisibleClipBounds.Y, VisibleClipBounds.Width, VisibleClipBounds.Height));

    public void FillRectangle(Brush brush, Rectangle rect) => FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);
    public void FillRectangle(Brush brush, RectangleF rect) => FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);
    public void FillRectangle(Brush brush, int x, int y, int width, int height) => FillRectangle(brush, (float)x, y, width, height);

    public void FillRectangle(Brush brush, float x, float y, float width, float height)
    {
        if (width <= 0 || height <= 0)
            return;
        _context.FillRectangle(brush.ToAvalonia(), new AvRect(x, y, width, height));
    }

    public void FillRectangles(Brush brush, Rectangle[] rects)
    {
        foreach (var rect in rects)
            FillRectangle(brush, rect);
    }

    public void FillEllipse(Brush brush, Rectangle rect) => FillEllipse(brush, rect.X, rect.Y, rect.Width, rect.Height);
    public void FillEllipse(Brush brush, RectangleF rect) => FillEllipse(brush, rect.X, rect.Y, rect.Width, rect.Height);
    public void FillEllipse(Brush brush, int x, int y, int width, int height) => FillEllipse(brush, (float)x, y, width, height);

    public void FillEllipse(Brush brush, float x, float y, float width, float height) =>
        _context.DrawEllipse(brush.ToAvalonia(), null, new AvRect(x, y, width, height));

    public void FillPolygon(Brush brush, PointF[] points) => _context.DrawGeometry(brush.ToAvalonia(), null, Polygon(points, close: true));
    public void FillPolygon(Brush brush, Point[] points) => FillPolygon(brush, points.Select(p => (PointF)p).ToArray());

    public void FillPath(Brush brush, GraphicsPath path) => _context.DrawGeometry(brush.ToAvalonia(), null, path.ToGeometry());

    public void FillPie(Brush brush, float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        var path = new GraphicsPath();
        path.AddPie(x, y, width, height, startAngle, sweepAngle);
        FillPath(brush, path);
    }

    // ---------------------------------------------------------------- lines

    /// <summary>Where a thin line's centre goes so that it covers the pixel GDI+ would light.</summary>
    private static double Snap(float value, Pen pen) => pen.Width <= 1.5f && Math.Abs(value - Math.Round(value)) < 0.01 ? value + 0.5 : value;

    public void DrawLine(Pen pen, Point a, Point b) => DrawLine(pen, a.X, a.Y, b.X, b.Y);
    public void DrawLine(Pen pen, PointF a, PointF b) => DrawLine(pen, a.X, a.Y, b.X, b.Y);
    public void DrawLine(Pen pen, int x1, int y1, int x2, int y2) => DrawLine(pen, (float)x1, y1, x2, y2);

    public void DrawLine(Pen pen, float x1, float y1, float x2, float y2)
    {
        double sx1 = x1, sy1 = y1, sx2 = x2, sy2 = y2;
        if (y1 == y2)
        {
            sy1 = sy2 = Snap(y1, pen);
            if (x2 > x1) sx2 += pen.Width >= 1 ? 1 : 0; else if (x1 > x2) sx1 += 1;
        }
        else if (x1 == x2)
        {
            sx1 = sx2 = Snap(x1, pen);
            if (y2 > y1) sy2 += pen.Width >= 1 ? 1 : 0; else if (y1 > y2) sy1 += 1;
        }

        _context.DrawLine(pen.ToAvalonia(), new AvPoint(sx1, sy1), new AvPoint(sx2, sy2));
    }

    public void DrawLines(Pen pen, PointF[] points)
    {
        if (points.Length < 2)
            return;
        _context.DrawGeometry(null, pen.ToAvalonia(), Polygon(points, close: false));
    }

    public void DrawLines(Pen pen, Point[] points) => DrawLines(pen, points.Select(p => (PointF)p).ToArray());

    public void DrawRectangle(Pen pen, Rectangle rect) => DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    public void DrawRectangle(Pen pen, int x, int y, int width, int height) => DrawRectangle(pen, (float)x, y, width, height);

    public void DrawRectangle(Pen pen, float x, float y, float width, float height)
    {
        // GDI+ draws a w-by-h rectangle covering w+1 by h+1 pixels, its edges on the pixel centres.
        double inset = pen.Width <= 1.5f ? 0.5 : 0;
        _context.DrawRectangle(null, pen.ToAvalonia(), new AvRect(x + inset, y + inset, width, height));
    }

    public void DrawEllipse(Pen pen, Rectangle rect) => DrawEllipse(pen, rect.X, rect.Y, rect.Width, rect.Height);
    public void DrawEllipse(Pen pen, RectangleF rect) => DrawEllipse(pen, rect.X, rect.Y, rect.Width, rect.Height);
    public void DrawEllipse(Pen pen, int x, int y, int width, int height) => DrawEllipse(pen, (float)x, y, width, height);

    public void DrawEllipse(Pen pen, float x, float y, float width, float height) =>
        _context.DrawEllipse(null, pen.ToAvalonia(), new AvRect(x, y, width, height));

    public void DrawPolygon(Pen pen, PointF[] points) => _context.DrawGeometry(null, pen.ToAvalonia(), Polygon(points, close: true));
    public void DrawPolygon(Pen pen, Point[] points) => DrawPolygon(pen, points.Select(p => (PointF)p).ToArray());

    public void DrawPath(Pen pen, GraphicsPath path) => _context.DrawGeometry(null, pen.ToAvalonia(), path.ToGeometry());

    public void DrawArc(Pen pen, Rectangle rect, float startAngle, float sweepAngle) =>
        DrawArc(pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    public void DrawArc(Pen pen, RectangleF rect, float startAngle, float sweepAngle) =>
        DrawArc(pen, rect.X, rect.Y, rect.Width, rect.Height, startAngle, sweepAngle);

    public void DrawArc(Pen pen, float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        var path = new GraphicsPath();
        path.AddArc(x, y, width, height, startAngle, sweepAngle);
        DrawPath(pen, path);
    }

    public void DrawBezier(Pen pen, PointF p1, PointF p2, PointF p3, PointF p4)
    {
        var path = new GraphicsPath();
        path.AddBezier(p1, p2, p3, p4);
        DrawPath(pen, path);
    }

    private static StreamGeometry Polygon(PointF[] points, bool close)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(new AvPoint(points[0].X, points[0].Y), close);
        for (int i = 1; i < points.Length; i++)
            context.LineTo(new AvPoint(points[i].X, points[i].Y));
        context.EndFigure(close);
        return geometry;
    }

    // ---------------------------------------------------------------- text

    public void DrawString(string? text, Font font, Brush brush, float x, float y) =>
        DrawString(text, font, brush, new PointF(x, y));

    public void DrawString(string? text, Font font, Brush brush, PointF point, StringFormat? format = null)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var formatted = Shim.Text(text, font, brush.ToAvalonia());

        // GDI+ leaves a sixth of an em in front of a string drawn at a point.
        double x = point.X + (font.PixelSize / 6.0);
        if (format?.Alignment == StringAlignment.Center)
            x = point.X - (formatted.WidthIncludingTrailingWhitespace / 2);
        else if (format?.Alignment == StringAlignment.Far)
            x = point.X - formatted.WidthIncludingTrailingWhitespace;

        _context.DrawText(formatted, new AvPoint(x, point.Y));
    }

    public void DrawString(string? text, Font font, Brush brush, RectangleF layout, StringFormat? format = null)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var formatted = Shim.Text(text, font, brush.ToAvalonia());
        double width = formatted.WidthIncludingTrailingWhitespace, height = formatted.Height;
        bool typographic = format is not null;
        double pad = typographic ? 0 : font.PixelSize / 6.0;

        double x = format?.Alignment switch
        {
            StringAlignment.Center => layout.X + ((layout.Width - width) / 2),
            StringAlignment.Far => layout.Right - width - pad,
            _ => layout.X + pad,
        };
        double y = format?.LineAlignment switch
        {
            StringAlignment.Center => layout.Y + ((layout.Height - height) / 2),
            StringAlignment.Far => layout.Bottom - height,
            _ => layout.Y,
        };

        using (_context.PushClip(new AvRect(layout.X, layout.Y, Math.Max(0, layout.Width), Math.Max(0, layout.Height + 2))))
            _context.DrawText(formatted, new AvPoint(x, y));
    }

    public SizeF MeasureString(string? text, Font font) => MeasureString(text, font, int.MaxValue);

    public SizeF MeasureString(string? text, Font font, int width, StringFormat? format = null)
    {
        if (string.IsNullOrEmpty(text))
            return new SizeF(0, font.Height);
        var formatted = Shim.Text(text, font, Avalonia.Media.Brushes.White);
        float pad = format is null ? font.PixelSize / 3f : 0f;
        return new SizeF((float)formatted.WidthIncludingTrailingWhitespace + pad, (float)formatted.Height);
    }

    public SizeF MeasureString(string? text, Font font, SizeF area, StringFormat? format = null) =>
        MeasureString(text, font, (int)area.Width, format);

    // ---------------------------------------------------------------- images

    public void DrawImage(Image image, Rectangle dest) => _context.DrawImage(image.Bitmap, new AvRect(dest.X, dest.Y, dest.Width, dest.Height));
    public void DrawImage(Image image, int x, int y) => _context.DrawImage(image.Bitmap, new AvRect(x, y, image.Width, image.Height));

    internal DrawingContext Context => _context;

    public void Dispose()
    {
        PopTo(0);
    }
}

/// <summary>An image to draw - only what the controls use.</summary>
public class Image : IDisposable
{
    internal Image(Avalonia.Media.Imaging.Bitmap bitmap) => Bitmap = bitmap;

    internal Avalonia.Media.Imaging.Bitmap Bitmap { get; }
    public int Width => Bitmap.PixelSize.Width;
    public int Height => Bitmap.PixelSize.Height;

    public void Dispose() => Bitmap.Dispose();
}

public sealed class Icon : IDisposable
{
    public void Dispose()
    {
    }
}

public static class SystemColors
{
    public static Color Highlight => Color.FromArgb(0x00, 0x78, 0xD7);
    public static Color HighlightText => Color.White;
    public static Color Window => Color.White;
    public static Color WindowText => Color.Black;
    public static Color Control => Color.FromArgb(0xF0, 0xF0, 0xF0);
    public static Color ControlText => Color.Black;
    public static Color GrayText => Color.FromArgb(0x6D, 0x6D, 0x6D);
}

/// <summary>Conversions and the text-layout cache the stand-ins share.</summary>
internal static class Shim
{
    private static readonly Dictionary<(string Text, Typeface Face, float Size, IBrush Brush), FormattedText> TextCache = [];

    public static AvColor ToAvalonia(Color color) => AvColor.FromArgb(color.A, color.R, color.G, color.B);

    public static FormattedText Text(string text, Font font, IBrush brush)
    {
        var key = (text, font.Typeface, font.PixelSize, brush);
        if (TextCache.TryGetValue(key, out var cached))
            return cached;

        // A plain bound rather than an LRU: the strings a tracker draws come from a small set, and
        // when they do not, starting over is cheaper than book-keeping every hit.
        if (TextCache.Count > 8192)
            TextCache.Clear();

        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, Avalonia.Media.FlowDirection.LeftToRight, font.Typeface, font.PixelSize, brush);
        TextCache[key] = formatted;
        return formatted;
    }
}
