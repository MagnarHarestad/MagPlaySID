// The rest of System.Windows.Forms the owner-drawn views lean on: a UI-thread timer, the standard
// cursors, a message box and TextRenderer.
using System.Drawing;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using AvCursor = Avalonia.Input.Cursor;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using PointF = System.Drawing.PointF;
using Size = System.Drawing.Size;
using SizeF = System.Drawing.SizeF;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;

namespace System.Windows.Forms;

/// <summary>WinForms' UI-thread timer: a DispatcherTimer.</summary>
public sealed class Timer : IDisposable
{
    private readonly DispatcherTimer _timer = new();
    private int _interval = 100;

    public Timer()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(_interval);
        _timer.Tick += (_, e) => Tick?.Invoke(this, e);
    }

    public event EventHandler? Tick;

    public int Interval
    {
        get => _interval;
        set
        {
            _interval = Math.Max(1, value);
            _timer.Interval = TimeSpan.FromMilliseconds(_interval);
        }
    }

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set => _timer.IsEnabled = value;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose() => _timer.Stop();
}

/// <summary>The standard cursors, as Avalonia ones - one instance each, so comparing them works.</summary>
public static class Cursors
{
    public static AvCursor Default { get; } = AvCursor.Default;
    public static AvCursor Arrow { get; } = new(StandardCursorType.Arrow);
    public static AvCursor Hand { get; } = new(StandardCursorType.Hand);
    public static AvCursor IBeam { get; } = new(StandardCursorType.Ibeam);
    public static AvCursor Cross { get; } = new(StandardCursorType.Cross);
    public static AvCursor SizeWE { get; } = new(StandardCursorType.SizeWestEast);
    public static AvCursor SizeNS { get; } = new(StandardCursorType.SizeNorthSouth);
    public static AvCursor SizeAll { get; } = new(StandardCursorType.SizeAll);
    public static AvCursor VSplit { get; } = new(StandardCursorType.SizeWestEast);
    public static AvCursor HSplit { get; } = new(StandardCursorType.SizeNorthSouth);
    public static AvCursor No { get; } = new(StandardCursorType.No);
    public static AvCursor WaitCursor { get; } = new(StandardCursorType.Wait);
}

public enum DialogResult
{
    None,
    OK,
    Cancel,
    Abort,
    Retry,
    Ignore,
    Yes,
    No,
}

public enum MessageBoxButtons
{
    OK,
    OKCancel,
    AbortRetryIgnore,
    YesNoCancel,
    YesNo,
    RetryCancel,
}

public enum MessageBoxIcon
{
    None = 0,
    Error = 16,
    Hand = 16,
    Stop = 16,
    Question = 32,
    Exclamation = 48,
    Warning = 48,
    Asterisk = 64,
    Information = 64,
}

/// <summary>
/// A message box. Only the plain notice is truly modal-looking here: Avalonia has no blocking dialog,
/// so the box opens over its owner and the call returns OK at once - which is all the views use it
/// for, telling the user why a keystroke did nothing.
/// </summary>
public static class MessageBox
{
    public static DialogResult Show(string text, string caption = "", MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None) => Show(null, text, caption, buttons, icon);

    public static DialogResult Show(IWin32Window? owner, string text, string caption = "",
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        var parent = owner is Avalonia.Visual visual ? TopLevel.GetTopLevel(visual) as Window : null;
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, MinWidth = 80 };
        var window = new Window
        {
            Title = caption,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = parent is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 16,
                Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, ok },
            },
        };
        ok.Click += (_, _) => window.Close();
        if (parent is not null)
            _ = window.ShowDialog(parent);
        else
            window.Show();
        return DialogResult.OK;
    }
}

[Flags]
public enum TextFormatFlags
{
    Default = 0,
    Left = 0,
    Top = 0,
    GlyphOverhangPadding = 0,
    HorizontalCenter = 0x1,
    Right = 0x2,
    VerticalCenter = 0x4,
    Bottom = 0x8,
    WordBreak = 0x10,
    SingleLine = 0x20,
    ExpandTabs = 0x40,
    NoClipping = 0x100,
    NoPrefix = 0x800,
    EndEllipsis = 0x8000,
    PreserveGraphicsClipping = 0x1000000,
    NoPadding = 0x10000000,
    LeftAndRightPadding = 0x20000000,
}

/// <summary>GDI's text drawing, through the same Avalonia text layout as Graphics.DrawString. Without
/// NoPadding, GDI leaves a sixth of the font's height either side of the text, and that is kept: the
/// views line their columns up against it.</summary>
public static class TextRenderer
{
    private static float Padding(Font font, TextFormatFlags flags) =>
        flags.HasFlag(TextFormatFlags.NoPadding) ? 0f : font.PixelSize / 6f;

    public static Size MeasureText(string? text, Font font) => MeasureText(text, font, Size.Empty, TextFormatFlags.Default);

    public static Size MeasureText(string? text, Font font, Size proposed) => MeasureText(text, font, proposed, TextFormatFlags.Default);

    public static Size MeasureText(string? text, Font font, Size proposed, TextFormatFlags flags)
    {
        var formatted = Drawing.Shim.Text(string.IsNullOrEmpty(text) ? " " : text, font, Avalonia.Media.Brushes.White);
        double width = string.IsNullOrEmpty(text) ? 0 : formatted.WidthIncludingTrailingWhitespace;
        return new Size((int)Math.Ceiling(width + (2 * Padding(font, flags))), (int)Math.Ceiling(formatted.Height));
    }

    public static Size MeasureText(Graphics graphics, string? text, Font font) => MeasureText(text, font);

    public static Size MeasureText(Graphics graphics, string? text, Font font, Size proposed) => MeasureText(text, font, proposed);

    public static Size MeasureText(Graphics graphics, string? text, Font font, Size proposed, TextFormatFlags flags) =>
        MeasureText(text, font, proposed, flags);

    public static void DrawText(Graphics graphics, string? text, Font font, Point point, Color color) =>
        DrawText(graphics, text, font, point, color, TextFormatFlags.Default);

    public static void DrawText(Graphics graphics, string? text, Font font, Point point, Color color, TextFormatFlags flags)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var formatted = Drawing.Shim.Text(text, font, new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Drawing.Shim.ToAvalonia(color)));
        graphics.Context.DrawText(formatted, new Avalonia.Point(point.X + Padding(font, flags), point.Y));
    }

    public static void DrawText(Graphics graphics, string? text, Font font, Point point, Color foreColor, Color backColor) =>
        DrawText(graphics, text, font, point, foreColor);

    public static void DrawText(Graphics graphics, string? text, Font font, Rectangle bounds, Color color) =>
        DrawText(graphics, text, font, bounds, color, TextFormatFlags.Default);

    public static void DrawText(Graphics graphics, string? text, Font font, Rectangle bounds, Color color, Color backColor, TextFormatFlags flags) =>
        DrawText(graphics, text, font, bounds, color, flags);

    public static void DrawText(Graphics graphics, string? text, Font font, Rectangle bounds, Color color, TextFormatFlags flags)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var formatted = Drawing.Shim.Text(text, font, new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Drawing.Shim.ToAvalonia(color)));
        float pad = Padding(font, flags);
        double width = formatted.WidthIncludingTrailingWhitespace, height = formatted.Height;

        double x = flags.HasFlag(TextFormatFlags.HorizontalCenter) ? bounds.X + ((bounds.Width - width) / 2)
            : flags.HasFlag(TextFormatFlags.Right) ? bounds.Right - width - pad
            : bounds.X + pad;
        double y = flags.HasFlag(TextFormatFlags.VerticalCenter) ? bounds.Y + ((bounds.Height - height) / 2)
            : flags.HasFlag(TextFormatFlags.Bottom) ? bounds.Bottom - height
            : bounds.Y;

        if (flags.HasFlag(TextFormatFlags.NoClipping))
        {
            graphics.Context.DrawText(formatted, new Avalonia.Point(x, y));
            return;
        }

        using (graphics.Context.PushClip(new Avalonia.Rect(bounds.X, bounds.Y, Math.Max(0, bounds.Width), Math.Max(0, bounds.Height))))
            graphics.Context.DrawText(formatted, new Avalonia.Point(x, y));
    }
}
