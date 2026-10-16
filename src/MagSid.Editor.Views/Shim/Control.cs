// A WinForms Control on top of an Avalonia one. The editor's owner-drawn views - the tracker grid,
// later the piano roll and the arrange - were written against WinForms: they paint in OnPaint with a
// Graphics, read the mouse in OnMouseDown and the keys in OnKeyDown. Underneath, this is an Avalonia
// control: Render hands OnPaint a Graphics over the DrawingContext, pointer and key events arrive
// translated, and the view's own source compiles here unchanged.
using System.Drawing;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvControl = Avalonia.Controls.Control;
using AvKeyEventArgs = Avalonia.Input.KeyEventArgs;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using PointF = System.Drawing.PointF;
using Size = System.Drawing.Size;
using SizeF = System.Drawing.SizeF;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;

namespace System.Windows.Forms;

[Flags]
public enum ControlStyles
{
    ContainerControl = 0x1,
    UserPaint = 0x2,
    Opaque = 0x4,
    ResizeRedraw = 0x10,
    FixedWidth = 0x20,
    FixedHeight = 0x40,
    StandardClick = 0x100,
    Selectable = 0x200,
    UserMouse = 0x400,
    SupportsTransparentBackColor = 0x800,
    StandardDoubleClick = 0x1000,
    AllPaintingInWmPaint = 0x2000,
    CacheText = 0x4000,
    EnableNotifyMessage = 0x8000,
    DoubleBuffer = 0x10000,
    OptimizedDoubleBuffer = 0x20000,
}

public interface IWin32Window
{
}

public sealed class PaintEventArgs(Graphics graphics, Rectangle clipRectangle) : EventArgs
{
    public Graphics Graphics { get; } = graphics;
    public Rectangle ClipRectangle { get; } = clipRectangle;
}

public class MouseEventArgs(MouseButtons button, int clicks, int x, int y, int delta) : EventArgs
{
    public MouseButtons Button { get; } = button;
    public int Clicks { get; } = clicks;
    public int X { get; } = x;
    public int Y { get; } = y;
    public int Delta { get; } = delta;
    public Point Location => new(X, Y);
}

public class KeyEventArgs(Keys keyData) : EventArgs
{
    public Keys KeyData { get; } = keyData;
    public Keys KeyCode => KeyData & Keys.KeyCode;
    public Keys Modifiers => KeyData & Keys.Modifiers;
    public int KeyValue => (int)KeyCode;
    public bool Control => (KeyData & Keys.Control) != 0;
    public bool Shift => (KeyData & Keys.Shift) != 0;
    public bool Alt => (KeyData & Keys.Alt) != 0;
    public bool Handled { get; set; }
    public bool SuppressKeyPress { get; set; }
}

public delegate void MouseEventHandler(object? sender, MouseEventArgs e);

public delegate void KeyEventHandler(object? sender, KeyEventArgs e);

public delegate void PaintEventHandler(object? sender, PaintEventArgs e);

public class Control : AvControl, IWin32Window, IDisposable
{
    private IPointer? _pointer;
    private MouseButtons _pressed;
    private Size _lastSize;

    public Control()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    // ---------------------------------------------------------------- what WinForms code reads

    public new int Width
    {
        get => (int)Math.Round(Bounds.Width);
        set => base.Width = value;
    }

    public new int Height
    {
        get => (int)Math.Round(Bounds.Height);
        set => base.Height = value;
    }

    public Size Size
    {
        get => new(Width, Height);
        set
        {
            base.Width = value.Width;
            base.Height = value.Height;
        }
    }

    public Size ClientSize
    {
        get => Size;
        set => Size = value;
    }

    public Rectangle ClientRectangle => new(0, 0, Width, Height);

    public Rectangle DisplayRectangle => ClientRectangle;

    public Color BackColor { get; set; } = Color.Empty;

    public Color ForeColor { get; set; } = Color.Black;

    public Font Font { get; set; } = new("Segoe UI", 9f);

    public bool TabStop
    {
        get => Focusable;
        set => Focusable = value;
    }

    public bool Visible
    {
        get => IsVisible;
        set => IsVisible = value;
    }

    public bool Enabled
    {
        get => IsEnabled;
        set => IsEnabled = value;
    }

    public bool Focused => IsFocused;

    public bool ContainsFocus => IsKeyboardFocusWithin;

    public bool IsHandleCreated => this.GetVisualRoot() is not null;

    public bool IsDisposed { get; private set; }

    /// <summary>The mouse stays with this control until let go - Avalonia captures on press already,
    /// so this only has to release, or capture again for a drag begun some other way.</summary>
    public bool Capture
    {
        get => _pointer?.Captured == this;
        set
        {
            if (_pointer is null)
                return;
            _pointer.Capture(value ? this : null);
        }
    }

    public static Keys ModifierKeys { get; private set; }

    public static MouseButtons MouseButtons { get; private set; }

    protected void SetStyle(ControlStyles styles, bool value)
    {
    }

    protected void UpdateStyles()
    {
    }

    protected bool DoubleBuffered { get; set; }

    protected bool ResizeRedraw { get; set; }

    protected virtual bool IsInputKey(Keys keyData) => false;

    public void Invalidate() => InvalidateVisual();

    public void Invalidate(bool invalidateChildren) => InvalidateVisual();

    public void Invalidate(Rectangle rect) => InvalidateVisual();

    public void Refresh() => InvalidateVisual();

    public void Update()
    {
    }

    public bool Select() => Focus();

    public void BeginInvoke(Action action) => Dispatcher.UIThread.Post(action);

    public void Invoke(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Invoke(action);
    }

    public T Invoke<T>(Func<T> action) => Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.Invoke(action);

    public Point PointToClient(Point screen)
    {
        var point = this.PointToClient(new PixelPoint(screen.X, screen.Y));
        return new Point((int)point.X, (int)point.Y);
    }

    public Point PointToScreen(Point client)
    {
        var point = this.PointToScreen(new Avalonia.Point(client.X, client.Y));
        return new Point(point.X, point.Y);
    }

    // ---------------------------------------------------------------- events WinForms code raises and handles

    public event MouseEventHandler? MouseDown;
    public event MouseEventHandler? MouseUp;
    public event MouseEventHandler? MouseMove;
    public event MouseEventHandler? MouseClick;
    public event MouseEventHandler? MouseDoubleClick;
    public event MouseEventHandler? MouseWheel;
    public event EventHandler? MouseEnter;
    public event EventHandler? MouseLeave;
    public event EventHandler? Click;
    public event EventHandler? DoubleClick;
    public event KeyEventHandler? KeyDown;
    public event KeyEventHandler? KeyUp;
    public event EventHandler? Resize;
    public event EventHandler? GotFocus;
    public event EventHandler? LostFocus;
    public event PaintEventHandler? Paint;

    protected virtual void OnPaint(PaintEventArgs e) => Paint?.Invoke(this, e);

    protected virtual void OnMouseDown(MouseEventArgs e) => MouseDown?.Invoke(this, e);

    protected virtual void OnMouseUp(MouseEventArgs e) => MouseUp?.Invoke(this, e);

    protected virtual void OnMouseMove(MouseEventArgs e) => MouseMove?.Invoke(this, e);

    protected virtual void OnMouseClick(MouseEventArgs e) => MouseClick?.Invoke(this, e);

    protected virtual void OnMouseDoubleClick(MouseEventArgs e) => MouseDoubleClick?.Invoke(this, e);

    protected virtual void OnMouseWheel(MouseEventArgs e) => MouseWheel?.Invoke(this, e);

    protected virtual void OnMouseEnter(EventArgs e) => MouseEnter?.Invoke(this, e);

    protected virtual void OnMouseLeave(EventArgs e) => MouseLeave?.Invoke(this, e);

    protected virtual void OnClick(EventArgs e) => Click?.Invoke(this, e);

    protected virtual void OnDoubleClick(EventArgs e) => DoubleClick?.Invoke(this, e);

    protected virtual void OnKeyDown(KeyEventArgs e) => KeyDown?.Invoke(this, e);

    protected virtual void OnKeyUp(KeyEventArgs e) => KeyUp?.Invoke(this, e);

    protected virtual void OnResize(EventArgs e) => Resize?.Invoke(this, e);

    protected virtual void OnSizeChanged(EventArgs e)
    {
    }

    protected virtual void OnGotFocus(EventArgs e) => GotFocus?.Invoke(this, e);

    protected virtual void OnLostFocus(EventArgs e) => LostFocus?.Invoke(this, e);

    protected virtual void OnEnabledChanged(EventArgs e)
    {
    }

    protected virtual void OnVisibleChanged(EventArgs e)
    {
    }

    protected virtual void OnHandleCreated(EventArgs e)
    {
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        Dispose(true);
    }

    // ---------------------------------------------------------------- Avalonia underneath

    public override void Render(DrawingContext context)
    {
        using var graphics = new Graphics(context, Width, Height);
        if (BackColor.A > 0)
            graphics.FillRectangle(new SolidBrush(BackColor), ClientRectangle);
        OnPaint(new PaintEventArgs(graphics, ClientRectangle));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        OnHandleCreated(EventArgs.Empty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            var size = new Size(Width, Height);
            if (size != _lastSize)
            {
                _lastSize = size;
                OnSizeChanged(EventArgs.Empty);
                OnResize(EventArgs.Empty);
                InvalidateVisual();
            }
        }
        else if (change.Property == IsEnabledProperty)
        {
            OnEnabledChanged(EventArgs.Empty);
        }
        else if (change.Property == IsVisibleProperty)
        {
            OnVisibleChanged(EventArgs.Empty);
        }
    }

    private MouseEventArgs MouseArgs(PointerEventArgs e, MouseButtons button, int clicks, int delta = 0)
    {
        var position = e.GetPosition(this);
        return new MouseEventArgs(button, clicks, (int)Math.Floor(position.X), (int)Math.Floor(position.Y), delta);
    }

    private static MouseButtons ButtonOf(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => MouseButtons.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => MouseButtons.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => MouseButtons.Middle,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => MouseButtons.XButton1,
        PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => MouseButtons.XButton2,
        _ => MouseButtons.None,
    };

    private static MouseButtons ButtonsHeld(PointerPointProperties properties) =>
        (properties.IsLeftButtonPressed ? MouseButtons.Left : 0)
        | (properties.IsRightButtonPressed ? MouseButtons.Right : 0)
        | (properties.IsMiddleButtonPressed ? MouseButtons.Middle : 0);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _pointer = e.Pointer;
        var point = e.GetCurrentPoint(this);
        var button = ButtonOf(point.Properties.PointerUpdateKind);
        MouseButtons = ButtonsHeld(point.Properties);
        ModifierKeys = KeyMap.Modifiers(e.KeyModifiers);
        _pressed = button;

        var args = MouseArgs(e, button, e.ClickCount);
        OnMouseDown(args);
        if (e.ClickCount == 2)
        {
            OnDoubleClick(EventArgs.Empty);
            OnMouseDoubleClick(args);
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetCurrentPoint(this);
        var button = ButtonOf(point.Properties.PointerUpdateKind);
        MouseButtons = ButtonsHeld(point.Properties);
        ModifierKeys = KeyMap.Modifiers(e.KeyModifiers);

        var args = MouseArgs(e, button, 1);
        OnMouseUp(args);

        var position = e.GetPosition(this);
        if (_pressed == button && position.X >= 0 && position.Y >= 0 && position.X < Bounds.Width && position.Y < Bounds.Height)
        {
            OnClick(EventArgs.Empty);
            OnMouseClick(args);
        }

        _pressed = MouseButtons.None;
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetCurrentPoint(this);
        MouseButtons = ButtonsHeld(point.Properties);
        ModifierKeys = KeyMap.Modifiers(e.KeyModifiers);
        OnMouseMove(MouseArgs(e, MouseButtons, 0));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ModifierKeys = KeyMap.Modifiers(e.KeyModifiers);
        OnMouseWheel(MouseArgs(e, MouseButtons.None, 0, (int)Math.Round(e.Delta.Y * 120)));
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        OnMouseEnter(EventArgs.Empty);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        OnMouseLeave(EventArgs.Empty);
    }

    protected override void OnKeyDown(AvKeyEventArgs e)
    {
        ModifierKeys = KeyMap.Modifiers(e.KeyModifiers);
        var key = KeyMap.From(e);
        if (key == Keys.None)
        {
            base.OnKeyDown(e);
            return;
        }

        var args = new KeyEventArgs(key | ModifierKeys);
        OnKeyDown(args);

        // Arrows, Tab and the rest are the control's own once it takes them, as IsInputKey
        // promises in WinForms - Avalonia would otherwise move the focus with them.
        if (args.Handled || args.SuppressKeyPress || IsInputKey(args.KeyData))
            e.Handled = true;
        else
            base.OnKeyDown(e);
    }

    protected override void OnKeyUp(AvKeyEventArgs e)
    {
        ModifierKeys = KeyMap.Modifiers(e.KeyModifiers);
        var key = KeyMap.From(e);
        if (key == Keys.None)
        {
            base.OnKeyUp(e);
            return;
        }

        var args = new KeyEventArgs(key | ModifierKeys);
        OnKeyUp(args);
        if (args.Handled)
            e.Handled = true;
        else
            base.OnKeyUp(e);
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        OnGotFocus((EventArgs)e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        OnLostFocus((EventArgs)e);
        InvalidateVisual();
    }
}

public class UserControl : Control
{
}

/// <summary>Avalonia's keys as WinForms ones: the key as the keyboard layout names it, the way
/// WinForms' Keys do - so the piano's Z is wherever Z is printed - and where Avalonia has no name
/// for it, the key at that place on a US keyboard.</summary>
internal static class KeyMap
{
    public static Keys Modifiers(KeyModifiers modifiers)
    {
        var keys = Keys.None;
        if (modifiers.HasFlag(KeyModifiers.Shift))
            keys |= Keys.Shift;
        if (modifiers.HasFlag(KeyModifiers.Alt))
            keys |= Keys.Alt;

        // Command on a Mac is what Control is on Windows.
        if (modifiers.HasFlag(KeyModifiers.Control) || (OperatingSystem.IsMacOS() && modifiers.HasFlag(KeyModifiers.Meta)))
            keys |= Keys.Control;
        return keys;
    }

    public static Keys From(AvKeyEventArgs e)
    {
        if (FromKey(e.Key) is var named and not Keys.None)
            return named;

        string physical = e.PhysicalKey.ToString();
        if (physical.Length == 1 && physical[0] is >= 'A' and <= 'Z')
            return Keys.A + (physical[0] - 'A');
        if (physical.StartsWith("Digit", StringComparison.Ordinal) && physical.Length == 6)
            return Keys.D0 + (physical[5] - '0');
        if (physical.StartsWith("NumPad", StringComparison.Ordinal) && physical.Length == 7 && char.IsDigit(physical[6]))
            return Keys.NumPad0 + (physical[6] - '0');

        switch (physical)
        {
            case "Quote": return Keys.OemQuotes;
            case "Semicolon": return Keys.OemSemicolon;
            case "Minus": return Keys.OemMinus;
            case "Equal": return Keys.Oemplus;
            case "Comma": return Keys.Oemcomma;
            case "Period": return Keys.OemPeriod;
            case "Slash": return Keys.OemQuestion;
            case "Backquote": return Keys.Oemtilde;
            case "BracketLeft": return Keys.OemOpenBrackets;
            case "BracketRight": return Keys.OemCloseBrackets;
            case "Backslash": return Keys.OemPipe;
            case "IntlBackslash": return Keys.OemBackslash;
            case "NumPadAdd": return Keys.Add;
            case "NumPadSubtract": return Keys.Subtract;
            case "NumPadMultiply": return Keys.Multiply;
            case "NumPadDivide": return Keys.Divide;
            case "NumPadDecimal": return Keys.Decimal;
            case "NumPadEnter": return Keys.Return;
        }

        return Keys.None;
    }

    private static Keys FromKey(Key key)
    {
        return key switch
        {
            Key.OemQuotes => Keys.OemQuotes,
            Key.OemSemicolon => Keys.OemSemicolon,
            Key.OemComma => Keys.Oemcomma,
            Key.OemPeriod => Keys.OemPeriod,
            Key.OemQuestion => Keys.OemQuestion,
            Key.OemTilde => Keys.Oemtilde,
            Key.OemOpenBrackets => Keys.OemOpenBrackets,
            Key.OemCloseBrackets => Keys.OemCloseBrackets,
            Key.OemPipe => Keys.OemPipe,
            Key.OemBackslash => Keys.OemBackslash,
            Key.Back => Keys.Back,
            Key.Tab => Keys.Tab,
            Key.Enter => Keys.Return,
            Key.Escape => Keys.Escape,
            Key.Space => Keys.Space,
            Key.PageUp => Keys.PageUp,
            Key.PageDown => Keys.PageDown,
            Key.End => Keys.End,
            Key.Home => Keys.Home,
            Key.Left => Keys.Left,
            Key.Up => Keys.Up,
            Key.Right => Keys.Right,
            Key.Down => Keys.Down,
            Key.Insert => Keys.Insert,
            Key.Delete => Keys.Delete,
            Key.CapsLock => Keys.CapsLock,
            Key.Add => Keys.Add,
            Key.Subtract => Keys.Subtract,
            Key.Multiply => Keys.Multiply,
            Key.Divide => Keys.Divide,
            Key.OemPlus => Keys.Oemplus,
            Key.OemMinus => Keys.OemMinus,
            >= Key.F1 and <= Key.F24 => Keys.F1 + (key - Key.F1),
            >= Key.A and <= Key.Z => Keys.A + (key - Key.A),
            >= Key.D0 and <= Key.D9 => Keys.D0 + (key - Key.D0),
            >= Key.NumPad0 and <= Key.NumPad9 => Keys.NumPad0 + (key - Key.NumPad0),
            Key.LeftShift or Key.RightShift => Keys.ShiftKey,
            Key.LeftCtrl or Key.RightCtrl => Keys.ControlKey,
            Key.LeftAlt or Key.RightAlt => Keys.Menu,
            _ => Keys.None,
        };
    }
}
