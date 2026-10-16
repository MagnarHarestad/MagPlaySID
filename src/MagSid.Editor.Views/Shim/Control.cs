// A WinForms Control on top of an Avalonia one. The editor's owner-drawn views - the tracker grid,
// later the piano roll and the arrange - were written against WinForms: they paint in OnPaint with a
// Graphics, read the mouse in OnMouseDown and the keys in OnKeyDown. Underneath, this is an Avalonia
// control: Render hands OnPaint a Graphics over the DrawingContext, pointer and key events arrive
// translated, and the view's own source compiles here unchanged.
using System.Drawing;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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
    private ControlCollection? _controls;

    // WinForms sizes and places a control by asking it to be so big at such a place; Avalonia by
    // laying it out. The asked-for values are kept here and used by the parent's layout (see
    // ArrangeOverride); reading Width gives the laid-out width once there is one, the asked-for
    // one until then - the same as a WinForms control that has not been docked yet.
    private int _requestedWidth;
    private int _requestedHeight;
    private bool _widthAsked;
    private bool _heightAsked;
    private Point _location;
    private DockStyle _dock;
    private Padding _padding;
    private Padding _margin = new(3);
    private string _text = string.Empty;

    public Control()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    // ---------------------------------------------------------------- size and place

    protected virtual Size DefaultSize => Size.Empty;

    protected virtual Padding DefaultMargin => new(3);

    internal int RequestedWidth => _widthAsked ? _requestedWidth : DefaultSize.Width;

    internal int RequestedHeight => _heightAsked ? _requestedHeight : DefaultSize.Height;

    public new int Width
    {
        get => _widthAsked || base.Bounds.Width <= 0 ? RequestedWidth : (int)Math.Round(base.Bounds.Width);
        set
        {
            _requestedWidth = Math.Max(0, value);
            _widthAsked = true;
            InvalidateMeasure();
            (base.Parent as Layoutable)?.InvalidateArrange();
        }
    }

    public new int Height
    {
        get => _heightAsked || base.Bounds.Height <= 0 ? RequestedHeight : (int)Math.Round(base.Bounds.Height);
        set
        {
            _requestedHeight = Math.Max(0, value);
            _heightAsked = true;
            InvalidateMeasure();
            (base.Parent as Layoutable)?.InvalidateArrange();
        }
    }

    public Size Size
    {
        get => new(Width, Height);
        set
        {
            Width = value.Width;
            Height = value.Height;
        }
    }

    public Size ClientSize
    {
        get => Size;
        set => Size = value;
    }

    public Point Location
    {
        get => _location;
        set
        {
            _location = value;
            (base.Parent as Layoutable)?.InvalidateArrange();
        }
    }

    public int Left
    {
        get => _location.X;
        set => Location = new Point(value, _location.Y);
    }

    public int Top
    {
        get => _location.Y;
        set => Location = new Point(_location.X, value);
    }

    public int Right => Left + Width;

    public int Bottom => Top + Height;

    /// <summary>Where the control sits in its parent and how big - WinForms' Bounds. Avalonia's own
    /// Bounds (a Rect) stays what the layout gave it.</summary>
    public new Rectangle Bounds
    {
        get => new(Left, Top, Width, Height);
        set
        {
            _location = value.Location;
            Width = value.Width;
            Height = value.Height;
        }
    }

    internal Avalonia.Rect LaidOut => base.Bounds;

    public Rectangle ClientRectangle => new(0, 0, Width, Height);

    public Rectangle DisplayRectangle => new(_padding.Left, _padding.Top,
        Math.Max(0, Width - _padding.Horizontal), Math.Max(0, Height - _padding.Vertical));

    public DockStyle Dock
    {
        get => _dock;
        set
        {
            _dock = value;
            (base.Parent as Layoutable)?.InvalidateArrange();
        }
    }

    public AnchorStyles Anchor { get; set; } = AnchorStyles.Top | AnchorStyles.Left;

    public Padding Padding
    {
        get => _padding;
        set
        {
            _padding = value;
            InvalidateArrange();
        }
    }

    public new Padding Margin
    {
        get => _margin;
        set
        {
            _margin = value;
            (base.Parent as Layoutable)?.InvalidateArrange();
        }
    }

    public virtual bool AutoSize { get; set; }

    /// <summary>A flow panel's direction. On every control because WinForms code names the enum
    /// from inside its own controls, where Avalonia's FlowDirection property would otherwise be
    /// what the name means.</summary>
    public new FlowDirection FlowDirection { get; set; }

    /// <summary>Avalonia's alignment, under WinForms' enum - the name WinForms code uses for a text
    /// box's alignment, which must not find Avalonia's property first.</summary>
    public new HorizontalAlignment HorizontalAlignment
    {
        get => base.HorizontalAlignment switch
        {
            Avalonia.Layout.HorizontalAlignment.Center => HorizontalAlignment.Center,
            Avalonia.Layout.HorizontalAlignment.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
        set => base.HorizontalAlignment = value switch
        {
            HorizontalAlignment.Center => Avalonia.Layout.HorizontalAlignment.Center,
            HorizontalAlignment.Right => Avalonia.Layout.HorizontalAlignment.Right,
            _ => Avalonia.Layout.HorizontalAlignment.Left,
        };
    }

    public AutoSizeMode AutoSizeMode { get; set; }

    public Size MinimumSize { get; set; }

    public Size MaximumSize { get; set; }

    // ---------------------------------------------------------------- children

    public ControlCollection Controls => _controls ??= new ControlCollection(this);

    /// <summary>The WinForms parent - a shim control, or null at the top of the shim's tree.</summary>
    public new Control? Parent => base.Parent as Control;

    internal void AttachChild(Control child)
    {
        VisualChildren.Add(child);
        LogicalChildren.Add(child);
        UpdateChildOrder();
    }

    internal void DetachChild(Control child)
    {
        VisualChildren.Remove(child);
        LogicalChildren.Remove(child);
        UpdateChildOrder();
    }

    /// <summary>WinForms draws index 0 on top; Avalonia draws the highest ZIndex on top.</summary>
    internal void UpdateChildOrder()
    {
        if (_controls is null)
            return;
        for (int i = 0; i < _controls.Count; i++)
            _controls[i].ZIndex = _controls.Count - i;
        InvalidateMeasure();
        InvalidateArrange();
    }

    public void BringToFront() => Parent?.Controls.SetChildIndex(this, 0);

    public void SendToBack()
    {
        if (Parent is { } parent)
            parent.Controls.SetChildIndex(this, parent.Controls.Count - 1);
    }

    public void SuspendLayout()
    {
    }

    public void ResumeLayout(bool performLayout = true)
    {
    }

    public void PerformLayout() => InvalidateArrange();

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize)
    {
        if (_controls is not null)
        {
            foreach (var child in _controls)
                child.Measure(availableSize);
        }

        return new Avalonia.Size(RequestedWidth, RequestedHeight);
    }

    protected override Avalonia.Size ArrangeOverride(Avalonia.Size finalSize)
    {
        if (_controls is { Count: > 0 })
            LayoutChildren(finalSize);
        return finalSize;
    }

    /// <summary>
    /// WinForms docking: from the last child to the first, each docked one takes its strip off the
    /// edge of what is left, and Fill takes the rest; undocked ones keep their own place and size.
    /// </summary>
    protected virtual void LayoutChildren(Avalonia.Size size)
    {
        double x = _padding.Left, y = _padding.Top;
        double width = Math.Max(0, size.Width - _padding.Horizontal), height = Math.Max(0, size.Height - _padding.Vertical);

        for (int i = _controls!.Count - 1; i >= 0; i--)
        {
            var child = _controls[i];
            if (!child.IsVisible)
                continue;

            Avalonia.Rect rect;
            switch (child.Dock)
            {
                case DockStyle.Top:
                    rect = new Avalonia.Rect(x, y, width, Math.Min(height, child.RequestedHeight));
                    y += rect.Height;
                    height -= rect.Height;
                    break;
                case DockStyle.Bottom:
                    rect = new Avalonia.Rect(x, y + height - Math.Min(height, child.RequestedHeight), width, Math.Min(height, child.RequestedHeight));
                    height -= rect.Height;
                    break;
                case DockStyle.Left:
                    rect = new Avalonia.Rect(x, y, Math.Min(width, child.RequestedWidth), height);
                    x += rect.Width;
                    width -= rect.Width;
                    break;
                case DockStyle.Right:
                    rect = new Avalonia.Rect(x + width - Math.Min(width, child.RequestedWidth), y, Math.Min(width, child.RequestedWidth), height);
                    width -= rect.Width;
                    break;
                case DockStyle.Fill:
                    rect = new Avalonia.Rect(x, y, width, height);
                    break;
                default:
                    rect = new Avalonia.Rect(child.Left, child.Top, child.RequestedWidth, child.RequestedHeight);
                    break;
            }

            child.ArrangeTo(rect);
        }
    }

    internal void ArrangeTo(Avalonia.Rect rect)
    {
        // The layout has decided: from now on Width and Height read what it gave.
        if (Dock is DockStyle.Top or DockStyle.Bottom or DockStyle.Fill)
            _widthAsked = false;
        if (Dock is DockStyle.Left or DockStyle.Right or DockStyle.Fill)
            _heightAsked = false;
        if (Dock != DockStyle.None)
            _location = new Point((int)rect.X, (int)rect.Y);
        Arrange(rect);
    }

    // ---------------------------------------------------------------- what WinForms code reads

    public Color BackColor { get; set; } = Color.Empty;

    public Color ForeColor { get; set; } = Color.FromArgb(0xD2, 0xD2, 0xDE);

    public Font Font { get; set; } = new("Segoe UI", 9f);

    public virtual string Text
    {
        get => _text;
        set
        {
            _text = value ?? string.Empty;
            OnTextChanged(EventArgs.Empty);
            InvalidateVisual();
        }
    }

    public bool TabStop
    {
        get => Focusable;
        set => Focusable = value;
    }

    public int TabIndex { get; set; }

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

    public bool Disposing => false;

    public ContextMenuStrip? ContextMenuStrip { get; set; }

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

    public static Keys ModifierKeys { get; internal set; }

    public static MouseButtons MouseButtons { get; internal set; }

    public static Point MousePosition { get; internal set; }

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

    /// <summary>Focus, as WinForms code asks for it - a control that hosts an Avalonia one (a text
    /// box) hands it on.</summary>
    public new bool Focus() => FocusCore();

    protected virtual bool FocusCore() => base.Focus();

    public bool Select() => Focus();

    public void BeginInvoke(Action action) => Dispatcher.UIThread.Post(action);

    public void BeginInvoke(Delegate action) => Dispatcher.UIThread.Post(() => action.DynamicInvoke());

    public void Invoke(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Invoke(action);
    }

    public T Invoke<T>(Func<T> action) => Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.Invoke(action);

    public bool InvokeRequired => !Dispatcher.UIThread.CheckAccess();

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

    public Control? FindForm() => this;

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
    public event EventHandler? SizeChanged2;
    public event EventHandler? GotFocus;
    public event EventHandler? LostFocus;
    public event EventHandler? Enter;
    public event EventHandler? Leave;
    public event EventHandler? TextChanged;
    public event EventHandler? VisibleChanged;
    public event EventHandler? EnabledChanged;
    public event EventHandler? HandleCreated;
    public event PaintEventHandler? Paint;

    protected virtual void OnPaint(PaintEventArgs e) => Paint?.Invoke(this, e);

    protected virtual void OnPaintBackground(PaintEventArgs e)
    {
        if (BackColor.A > 0)
            e.Graphics.FillRectangle(new SolidBrush(BackColor), ClientRectangle);
    }

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

    protected virtual void OnSizeChanged(EventArgs e) => SizeChanged2?.Invoke(this, e);

    protected virtual void OnGotFocus(EventArgs e) => GotFocus?.Invoke(this, e);

    protected virtual void OnLostFocus(EventArgs e) => LostFocus?.Invoke(this, e);

    protected virtual void OnEnter(EventArgs e) => Enter?.Invoke(this, e);

    protected virtual void OnLeave(EventArgs e) => Leave?.Invoke(this, e);

    protected virtual void OnTextChanged(EventArgs e) => TextChanged?.Invoke(this, e);

    protected virtual void OnEnabledChanged(EventArgs e) => EnabledChanged?.Invoke(this, e);

    protected virtual void OnVisibleChanged(EventArgs e) => VisibleChanged?.Invoke(this, e);

    protected virtual void OnHandleCreated(EventArgs e) => HandleCreated?.Invoke(this, e);

    protected virtual void OnLayout(LayoutEventArgs e)
    {
    }

    protected virtual void OnMouseCaptureChanged(EventArgs e)
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
        // WinForms never paints a control with no area, and code written for it does not expect to be.
        if (base.Bounds.Width <= 0 || base.Bounds.Height <= 0)
            return;

        using var graphics = new Graphics(context, Width, Height);
        var args = new PaintEventArgs(graphics, ClientRectangle);
        OnPaintBackground(args);
        OnPaint(args);
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
            var size = new Size((int)Math.Round(base.Bounds.Width), (int)Math.Round(base.Bounds.Height));
            if (size != _lastSize)
            {
                _lastSize = size;
                OnSizeChanged(EventArgs.Empty);
                OnResize(EventArgs.Empty);
                OnLayout(new LayoutEventArgs(this));
                InvalidateVisual();
            }
        }
        else if (change.Property == IsEnabledProperty)
        {
            OnEnabledChanged(EventArgs.Empty);
            InvalidateVisual();
        }
        else if (change.Property == IsVisibleProperty)
        {
            OnVisibleChanged(EventArgs.Empty);
            (base.Parent as Layoutable)?.InvalidateArrange();
        }
    }

    private MouseEventArgs MouseArgs(PointerEventArgs e, MouseButtons button, int clicks, int delta = 0)
    {
        var position = e.GetPosition(this);
        if (TopLevel(e) is { } top)
        {
            var screen = top.PointToScreen(e.GetPosition(top));
            MousePosition = new Point(screen.X, screen.Y);
        }

        return new MouseEventArgs(button, clicks, (int)Math.Floor(position.X), (int)Math.Floor(position.Y), delta);
    }

    private Avalonia.Controls.TopLevel? TopLevel(PointerEventArgs e) => Avalonia.Controls.TopLevel.GetTopLevel(this);

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
        if (e.Handled)
            return;

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
        if (e.Handled)
            return;

        var point = e.GetCurrentPoint(this);
        var button = ButtonOf(point.Properties.PointerUpdateKind);
        MouseButtons = ButtonsHeld(point.Properties);
        ModifierKeys = KeyMap.Modifiers(e.KeyModifiers);

        var args = MouseArgs(e, button, 1);
        OnMouseUp(args);

        var position = e.GetPosition(this);
        bool inside = position.X >= 0 && position.Y >= 0 && position.X < base.Bounds.Width && position.Y < base.Bounds.Height;
        if (_pressed == button && inside)
        {
            OnClick(EventArgs.Empty);
            OnMouseClick(args);
        }

        if (button == MouseButtons.Right && inside && ContextMenuStrip is { } menu)
            menu.Show(this, args.Location);

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
        if (e.Handled)
            return;
        ModifierKeys = KeyMap.Modifiers(e.KeyModifiers);
        OnMouseWheel(MouseArgs(e, MouseButtons.None, 0, (int)Math.Round(e.Delta.Y * 120)));
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        OnMouseCaptureChanged(EventArgs.Empty);
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
        if (key == Keys.None || e.Handled)
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
        if (key == Keys.None || e.Handled)
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
        if (e.Source == this)
        {
            OnGotFocus((EventArgs)e);
            OnEnter(EventArgs.Empty);
        }

        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (e.Source == this)
        {
            OnLostFocus((EventArgs)e);
            OnLeave(EventArgs.Empty);
        }

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
