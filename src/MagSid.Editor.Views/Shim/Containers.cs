// The WinForms building blocks the DAW lays itself out with: panels docked inside panels, a flow of
// buttons, labels, a text box, an owner-drawn list, and right-click menus. Enough of each for the
// editor's own views - not WinForms entire.
using System.Collections;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvControl = Avalonia.Controls.Control;
using AvTextBox = Avalonia.Controls.TextBox;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using Rectangle = System.Drawing.Rectangle;

namespace System.Windows.Forms;

public enum DockStyle
{
    None,
    Top,
    Bottom,
    Left,
    Right,
    Fill,
}

[Flags]
public enum AnchorStyles
{
    None = 0,
    Top = 1,
    Bottom = 2,
    Left = 4,
    Right = 8,
}

public enum AutoSizeMode
{
    GrowAndShrink,
    GrowOnly,
}

public enum BorderStyle
{
    None,
    FixedSingle,
    Fixed3D,
}

public enum FlowDirection
{
    LeftToRight,
    TopDown,
    RightToLeft,
    BottomUp,
}

public enum SizeType
{
    AutoSize,
    Absolute,
    Percent,
}

public enum DrawMode
{
    Normal,
    OwnerDrawFixed,
    OwnerDrawVariable,
}

[Flags]
public enum DrawItemState
{
    None = 0,
    Selected = 1,
    Grayed = 2,
    Disabled = 4,
    Checked = 8,
    Focus = 16,
    Default = 32,
    HotLight = 64,
}

public readonly record struct Padding(int Left, int Top, int Right, int Bottom)
{
    public Padding(int all)
        : this(all, all, all, all)
    {
    }

    public static Padding Empty { get; } = new(0);

    public int Horizontal => Left + Right;
    public int Vertical => Top + Bottom;
    public int All => Left == Top && Top == Right && Right == Bottom ? Left : -1;
}

public sealed class ControlCollection : IEnumerable<Control>
{
    private readonly Control _owner;
    private readonly List<Control> _items = [];

    internal ControlCollection(Control owner) => _owner = owner;

    public int Count => _items.Count;

    public Control this[int index] => _items[index];

    public void Add(Control child)
    {
        if (child.Parent is { } old && old != _owner)
            old.Controls.Remove(child);
        if (_items.Contains(child))
            return;

        // WinForms puts a new control at the end of the collection - the bottom of the z-order, the
        // first to be docked.
        _items.Add(child);
        _owner.AttachChild(child);
    }

    public void AddRange(Control[] children)
    {
        foreach (var child in children)
            Add(child);
    }

    public void Remove(Control child)
    {
        if (_items.Remove(child))
            _owner.DetachChild(child);
    }

    public void Clear()
    {
        foreach (var child in _items.ToList())
            Remove(child);
    }

    public bool Contains(Control child) => _items.Contains(child);

    public int IndexOf(Control child) => _items.IndexOf(child);

    public int GetChildIndex(Control child) => _items.IndexOf(child);

    public void SetChildIndex(Control child, int index)
    {
        if (!_items.Remove(child))
            return;
        _items.Insert(Math.Clamp(index, 0, _items.Count), child);
        _owner.UpdateChildOrder();
    }

    public IEnumerator<Control> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class Panel : Control
{
    public Panel()
    {
        Focusable = false;
    }

    public BorderStyle BorderStyle { get; set; }

    public bool AutoScroll { get; set; }

    protected override Size DefaultSize => new(200, 100);
}

/// <summary>Children in a row (or a column), each its own size plus its margin. No wrapping - the
/// editor's flows never wrap.</summary>
public class FlowLayoutPanel : Panel
{
    public bool WrapContents { get; set; } = true;

    /// <summary>WinForms' flow: each child after the last, by its own size and margin; with
    /// WrapContents a child that would run past the far edge starts a new column (TopDown) or row,
    /// as wide or tall as the biggest child of the one before.</summary>
    protected override void LayoutChildren(Avalonia.Size size)
    {
        bool down = FlowDirection == FlowDirection.TopDown;
        double x = Padding.Left, y = Padding.Top, line = 0;
        double limit = down ? size.Height - Padding.Bottom : size.Width - Padding.Right;
        bool lineStarted = false;
        foreach (var child in Controls)
        {
            if (!child.IsVisible)
                continue;

            var margin = child.Margin;
            var childSize = child.AutoSize && child is Label label ? label.PreferredSize : new Size(child.RequestedWidth, child.RequestedHeight);
            double along = down ? margin.Vertical + childSize.Height : margin.Horizontal + childSize.Width;
            double across = down ? margin.Horizontal + childSize.Width : margin.Vertical + childSize.Height;

            if (WrapContents && lineStarted && (down ? y : x) + along > limit)
            {
                if (down)
                {
                    x += line;
                    y = Padding.Top;
                }
                else
                {
                    y += line;
                    x = Padding.Left;
                }
                line = 0;
            }

            child.ArrangeTo(new Avalonia.Rect(x + margin.Left, y + margin.Top, childSize.Width, childSize.Height));
            if (down)
                y += along;
            else
                x += along;
            line = Math.Max(line, across);
            lineStarted = true;
        }
    }
}

public sealed class ColumnStyle(SizeType type = SizeType.AutoSize, float width = 0)
{
    public SizeType SizeType { get; set; } = type;
    public float Width { get; set; } = width;
}

public sealed class RowStyle(SizeType type = SizeType.AutoSize, float height = 0)
{
    public SizeType SizeType { get; set; } = type;
    public float Height { get; set; } = height;
}

/// <summary>A grid of cells; the editor uses it as a single row or column of equal shares.</summary>
public class TableLayoutPanel : Panel
{
    private readonly Dictionary<Control, (int Column, int Row)> _cells = [];

    public int ColumnCount { get; set; } = 1;
    public int RowCount { get; set; } = 1;
    public List<ColumnStyle> ColumnStyles { get; } = [];
    public List<RowStyle> RowStyles { get; } = [];

    public void SetCell(Control child, int column, int row) => _cells[child] = (column, row);

    public new TableControls Controls => new(this);

    public readonly struct TableControls(TableLayoutPanel owner) : IEnumerable<Control>
    {
        public int Count => ((Control)owner).Controls.Count;

        public Control this[int index] => ((Control)owner).Controls[index];

        public void Add(Control child) => ((Control)owner).Controls.Add(child);

        public void Add(Control child, int column, int row)
        {
            ((Control)owner).Controls.Add(child);
            owner.SetCell(child, column, row);
        }

        public void Remove(Control child) => ((Control)owner).Controls.Remove(child);

        public IEnumerator<Control> GetEnumerator() => ((Control)owner).Controls.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    protected override void LayoutChildren(Avalonia.Size size)
    {
        int columns = Math.Max(1, ColumnCount), rows = Math.Max(1, RowCount);
        double cellWidth = (size.Width - Padding.Horizontal) / columns;
        double cellHeight = (size.Height - Padding.Vertical) / rows;
        int next = 0;
        foreach (var child in ((Control)this).Controls)
        {
            var (column, row) = _cells.TryGetValue(child, out var cell) ? cell : (next % columns, next / columns);
            next++;
            if (!child.IsVisible)
                continue;
            var margin = child.Margin;
            var rect = new Avalonia.Rect(
                Padding.Left + (column * cellWidth) + margin.Left,
                Padding.Top + (row * cellHeight) + margin.Top,
                Math.Max(0, cellWidth - margin.Horizontal),
                Math.Max(0, cellHeight - margin.Vertical));
            child.ArrangeTo(rect);
        }
    }
}

public enum ContentAlignment
{
    TopLeft = 1,
    TopCenter = 2,
    TopRight = 4,
    MiddleLeft = 16,
    MiddleCenter = 32,
    MiddleRight = 64,
    BottomLeft = 256,
    BottomCenter = 512,
    BottomRight = 1024,
}

public class Label : Control
{
    public Label()
    {
        Focusable = false;
        IsHitTestVisible = true;
    }

    public ContentAlignment TextAlign { get; set; } = ContentAlignment.TopLeft;

    protected override Size DefaultSize => new(100, 23);

    public Size PreferredSize
    {
        get
        {
            var measured = TextRenderer.MeasureText(Text, Font);
            return new Size(measured.Width + Padding.Horizontal, measured.Height + Padding.Vertical);
        }
    }

    public int PreferredWidth => PreferredSize.Width;

    public int PreferredHeight => PreferredSize.Height;

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize)
    {
        if (AutoSize)
        {
            var preferred = PreferredSize;
            return new Avalonia.Size(preferred.Width, preferred.Height);
        }

        return base.MeasureOverride(availableSize);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var flags = TextFormatFlags.NoPrefix | (TextAlign switch
        {
            ContentAlignment.MiddleCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter,
            ContentAlignment.MiddleLeft => TextFormatFlags.VerticalCenter,
            ContentAlignment.MiddleRight => TextFormatFlags.Right | TextFormatFlags.VerticalCenter,
            ContentAlignment.TopCenter => TextFormatFlags.HorizontalCenter,
            ContentAlignment.TopRight => TextFormatFlags.Right,
            ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom,
            _ => TextFormatFlags.Default,
        });
        TextRenderer.DrawText(e.Graphics, Text, Font, DisplayRectangle, Enabled ? ForeColor : SystemColors.GrayText, flags | TextFormatFlags.WordBreak);
    }
}

/// <summary>A text box: Avalonia's own, inside, so typing, selection and the caret are the real
/// thing on every system. Its keys come out as WinForms key events.</summary>
public class TextBox : Control
{
    private readonly AvTextBox _box = new()
    {
        Padding = new Avalonia.Thickness(2, 1),
        MinHeight = 0,
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    public TextBox()
    {
        Focusable = false;
        Controls.Add(new Host(_box) { Dock = DockStyle.Fill });
        _box.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        _box.AddHandler(KeyDownEvent, (_, e) =>
        {
            var key = KeyMap.From(e);
            if (key == Keys.None)
                return;
            var args = new KeyEventArgs(key | KeyMap.Modifiers(e.KeyModifiers));
            OnKeyDown(args);
            if (args.Handled || args.SuppressKeyPress)
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
        _box.LostFocus += (_, _) => OnLostFocus(EventArgs.Empty);
        _box.GotFocus += (_, _) => OnGotFocus(EventArgs.Empty);
        ApplyLook();
    }

    protected override Size DefaultSize => new(100, 23);

    public override string Text
    {
        get => _box.Text ?? string.Empty;
        set => _box.Text = value;
    }

    public BorderStyle BorderStyle
    {
        get => _border;
        set
        {
            _border = value;
            ApplyLook();
        }
    }

    private BorderStyle _border = BorderStyle.Fixed3D;

    public bool ReadOnly
    {
        get => _box.IsReadOnly;
        set => _box.IsReadOnly = value;
    }

    public int MaxLength
    {
        get => _box.MaxLength;
        set => _box.MaxLength = value;
    }

    public System.Windows.Forms.HorizontalAlignment TextAlign
    {
        get => _box.TextAlignment switch
        {
            TextAlignment.Center => System.Windows.Forms.HorizontalAlignment.Center,
            TextAlignment.Right => System.Windows.Forms.HorizontalAlignment.Right,
            _ => System.Windows.Forms.HorizontalAlignment.Left,
        };
        set => _box.TextAlignment = value switch
        {
            System.Windows.Forms.HorizontalAlignment.Center => TextAlignment.Center,
            System.Windows.Forms.HorizontalAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
    }

    public bool Multiline { get; set; }

    public CharacterCasing CharacterCasing
    {
        get => _casing;
        set
        {
            _casing = value;
            _box.TextChanged -= ApplyCasing;
            if (value != CharacterCasing.Normal)
                _box.TextChanged += ApplyCasing;
        }
    }

    private CharacterCasing _casing;

    private void ApplyCasing(object? sender, EventArgs e)
    {
        string text = _box.Text ?? "";
        string cased = _casing == CharacterCasing.Upper ? text.ToUpperInvariant() : text.ToLowerInvariant();
        if (cased != text)
        {
            int caret = _box.CaretIndex;
            _box.Text = cased;
            _box.CaretIndex = caret;
        }
    }

    public void SelectAll() => _box.SelectAll();

    public Point GetPositionFromCharIndex(int index) => new(2, 0);

    protected override bool FocusCore() => _box.Focus();

    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    /// <summary>Colours and font follow the WinForms properties, read when the box is shown.</summary>
    private void ApplyLook()
    {
        _box.BorderThickness = new Avalonia.Thickness(_border == BorderStyle.None ? 0 : 1);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (BackColor.A > 0)
            _box.Background = new SolidColorBrush(Drawing.Shim.ToAvalonia(BackColor));
        _box.Foreground = new SolidColorBrush(Drawing.Shim.ToAvalonia(ForeColor));
        _box.FontFamily = Font.Typeface.FontFamily;
        _box.FontSize = Font.PixelSize;
        _box.FontWeight = Font.Typeface.Weight;
    }

    /// <summary>Puts an Avalonia control in a shim control's tree.</summary>
    private sealed class Host : Control
    {
        private readonly AvControl _content;

        public Host(AvControl content)
        {
            Focusable = false;
            _content = content;
            VisualChildren.Add(content);
            LogicalChildren.Add(content);
        }

        protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize)
        {
            _content.Measure(availableSize);
            return _content.DesiredSize;
        }

        protected override Avalonia.Size ArrangeOverride(Avalonia.Size finalSize)
        {
            _content.Arrange(new Avalonia.Rect(finalSize));
            return finalSize;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }
    }
}

public enum CharacterCasing
{
    Normal,
    Upper,
    Lower,
}

public enum HorizontalAlignment
{
    Left,
    Right,
    Center,
}

public sealed class DrawItemEventArgs(Graphics graphics, Font font, Rectangle bounds, int index, DrawItemState state, Color foreColor, Color backColor) : EventArgs
{
    public Graphics Graphics { get; } = graphics;
    public Font? Font { get; } = font;
    public Rectangle Bounds { get; } = bounds;
    public int Index { get; } = index;
    public DrawItemState State { get; } = state;
    public Color ForeColor { get; } = foreColor;
    public Color BackColor { get; } = backColor;

    public void DrawBackground()
    {
        var color = (State & DrawItemState.Selected) != 0 ? SystemColors.Highlight : BackColor;
        Graphics.FillRectangle(new SolidBrush(color), Bounds);
    }

    public void DrawFocusRectangle()
    {
    }
}

public delegate void DrawItemEventHandler(object? sender, DrawItemEventArgs e);

/// <summary>A list of rows, drawn by the owner or as plain text, one selected.</summary>
public class ListBox : Control
{
    private int _selectedIndex = -1;
    private int _topIndex;
    private int _itemHeight = 16;

    public ListBox()
    {
        Items = new ObjectCollection(this);
    }

    protected override Size DefaultSize => new(120, 96);

    public ObjectCollection Items { get; }

    public DrawMode DrawMode { get; set; }

    public BorderStyle BorderStyle { get; set; }

    public bool IntegralHeight { get; set; }

    public int ItemHeight
    {
        get => _itemHeight;
        set
        {
            _itemHeight = Math.Max(1, value);
            Invalidate();
        }
    }

    public event DrawItemEventHandler? DrawItem;
    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int next = value < 0 || value >= Items.Count ? -1 : value;
            if (next == _selectedIndex)
                return;
            _selectedIndex = next;
            EnsureVisible(next);
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public object? SelectedItem => _selectedIndex >= 0 ? Items[_selectedIndex] : null;

    public int TopIndex
    {
        get => _topIndex;
        set
        {
            _topIndex = Math.Clamp(value, 0, Math.Max(0, Items.Count - VisibleCount));
            Invalidate();
        }
    }

    private int VisibleCount => Math.Max(1, Height / _itemHeight);

    private void EnsureVisible(int index)
    {
        if (index < 0)
            return;
        if (index < _topIndex)
            TopIndex = index;
        else if (index >= _topIndex + VisibleCount)
            TopIndex = index - VisibleCount + 1;
    }

    public void BeginUpdate()
    {
    }

    public void EndUpdate() => Invalidate();

    public int IndexFromPoint(Point point) => IndexFromPoint(point.X, point.Y);

    public int IndexFromPoint(int x, int y)
    {
        int index = _topIndex + (y / _itemHeight);
        return y >= 0 && index >= 0 && index < Items.Count ? index : -1;
    }

    public Rectangle GetItemRectangle(int index) => new(0, (index - _topIndex) * _itemHeight, Width, _itemHeight);

    internal void ItemsChanged()
    {
        if (_selectedIndex >= Items.Count)
            _selectedIndex = -1;
        TopIndex = _topIndex;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button is MouseButtons.Left or MouseButtons.Right && IndexFromPoint(e.Location) is var index and >= 0)
            SelectedIndex = index;
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        TopIndex -= Math.Sign(e.Delta) * 3;
        base.OnMouseWheel(e);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;
        if (e.KeyCode == Keys.Up && _selectedIndex > 0)
        {
            SelectedIndex = _selectedIndex - 1;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Down && _selectedIndex < Items.Count - 1)
        {
            SelectedIndex = _selectedIndex + 1;
            e.Handled = true;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        for (int index = _topIndex; index < Items.Count; index++)
        {
            var bounds = GetItemRectangle(index);
            if (bounds.Top >= Height)
                break;

            var state = index == _selectedIndex ? DrawItemState.Selected : DrawItemState.None;
            var args = new DrawItemEventArgs(e.Graphics, Font, bounds, index, state, ForeColor, BackColor);
            if (DrawMode != DrawMode.Normal && DrawItem is not null)
            {
                DrawItem(this, args);
            }
            else
            {
                args.DrawBackground();
                TextRenderer.DrawText(e.Graphics, Items[index]?.ToString(), Font, bounds,
                    state == DrawItemState.Selected ? SystemColors.HighlightText : ForeColor, TextFormatFlags.VerticalCenter);
            }
        }
    }

    public sealed class ObjectCollection(ListBox owner) : IEnumerable<object?>
    {
        private readonly List<object?> _items = [];

        public int Count => _items.Count;

        public object? this[int index]
        {
            get => _items[index];
            set
            {
                _items[index] = value;
                owner.ItemsChanged();
            }
        }

        public int Add(object? item)
        {
            _items.Add(item);
            owner.ItemsChanged();
            return _items.Count - 1;
        }

        public void AddRange(object?[] items)
        {
            _items.AddRange(items);
            owner.ItemsChanged();
        }

        public void Insert(int index, object? item)
        {
            _items.Insert(index, item);
            owner.ItemsChanged();
        }

        public void RemoveAt(int index)
        {
            _items.RemoveAt(index);
            owner.ItemsChanged();
        }

        public void Clear()
        {
            _items.Clear();
            owner.ItemsChanged();
        }

        public IEnumerator<object?> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

public static class ControlPaint
{
    public static Color Light(Color color) => Light(color, 0.5f);

    public static Color Light(Color color, float amount) => Color.FromArgb(color.A,
        color.R + (int)((255 - color.R) * Math.Clamp(amount, 0f, 1f) * 0.5f),
        color.G + (int)((255 - color.G) * Math.Clamp(amount, 0f, 1f) * 0.5f),
        color.B + (int)((255 - color.B) * Math.Clamp(amount, 0f, 1f) * 0.5f));

    public static Color Dark(Color color) => Dark(color, 0.5f);

    public static Color Dark(Color color, float amount) => Color.FromArgb(color.A,
        (int)(color.R * (1 - (Math.Clamp(amount, 0f, 1f) * 0.5f))),
        (int)(color.G * (1 - (Math.Clamp(amount, 0f, 1f) * 0.5f))),
        (int)(color.B * (1 - (Math.Clamp(amount, 0f, 1f) * 0.5f))));
}

// ---------------------------------------------------------------- menus

public abstract class ToolStripItem
{
    public string Text { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool Visible { get; set; } = true;
    public object? Tag { get; set; }
    public string? ToolTipText { get; set; }
    public Padding Margin { get; set; }
    public Padding Padding { get; set; }
    public bool AutoSize { get; set; } = true;
    public Size Size { get; set; }
    public Font? Font { get; set; }
    public Color ForeColor { get; set; }
    public Color BackColor { get; set; }

    public event EventHandler? Click;

    public void PerformClick() => Click?.Invoke(this, EventArgs.Empty);
}

public class ToolStripMenuItem : ToolStripItem
{
    public ToolStripMenuItem()
    {
    }

    public ToolStripMenuItem(string text)
    {
        Text = text;
    }

    public ToolStripMenuItem(string text, Drawing.Image? image, EventHandler? onClick)
    {
        Text = text;
        if (onClick is not null)
            Click += onClick;
    }

    public ToolStripMenuItem(string text, Drawing.Image? image, params ToolStripItem[] dropDownItems)
    {
        Text = text;
        DropDownItems.AddRange(dropDownItems);
    }

    public bool Checked { get; set; }
    public bool CheckOnClick { get; set; }
    public string? ShortcutKeyDisplayString { get; set; }
    public Keys ShortcutKeys { get; set; }
    public ToolStripItemCollection DropDownItems { get; } = new();
    public ToolStripDropDownMenu DropDown { get; } = new();
}

public sealed class ToolStripDropDownMenu
{
}

public sealed class ToolStripSeparator : ToolStripItem
{
}

public class ToolStripItemCollection : IEnumerable<ToolStripItem>
{
    private readonly List<ToolStripItem> _items = [];

    public int Count => _items.Count;

    public ToolStripItem this[int index] => _items[index];

    public int Add(ToolStripItem item)
    {
        _items.Add(item);
        return _items.Count - 1;
    }

    public ToolStripItem Add(string text) => _items[Add(new ToolStripMenuItem(text))];

    public void AddRange(ToolStripItem[] items) => _items.AddRange(items);

    public void Insert(int index, ToolStripItem item) => _items.Insert(index, item);

    public void Remove(ToolStripItem item) => _items.Remove(item);

    public void Clear() => _items.Clear();

    public IEnumerator<ToolStripItem> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A right-click menu, shown as an Avalonia context menu built from the items each time it
/// opens - so items changed in Opening show as changed.</summary>
public class ContextMenuStrip : IDisposable
{
    public ToolStripItemCollection Items { get; } = new();

    public bool ShowImageMargin { get; set; }
    public bool ShowCheckMargin { get; set; }
    public Font? Font { get; set; }
    public Color BackColor { get; set; }
    public Color ForeColor { get; set; }

    public event CancelEventHandler? Opening;
    public event EventHandler? Closed;

    public void Show(Control control, Point point)
    {
        var cancel = new CancelEventArgs();
        Opening?.Invoke(this, cancel);
        if (cancel.Cancel || Items.Count == 0)
            return;

        var menu = new ContextMenu
        {
            ItemsSource = Build(Items),
            Placement = PlacementMode.Pointer,
        };
        menu.Closed += (_, _) =>
        {
            Closed?.Invoke(this, EventArgs.Empty);
            control.ContextMenu = null;
        };
        control.ContextMenu = menu;
        menu.Open(control);
    }

    public void Show(Point screen)
    {
    }

    public void Close()
    {
    }

    private static List<AvControl> Build(IEnumerable<ToolStripItem> items)
    {
        var built = new List<AvControl>();
        foreach (var item in items)
        {
            if (!item.Visible)
                continue;

            if (item is ToolStripSeparator)
            {
                built.Add(new Separator());
                continue;
            }

            var menuItem = new MenuItem
            {
                Header = item.Text.Replace("&&", "\u0001").Replace('&', '_').Replace("\u0001", "&"),
                IsEnabled = item.Enabled,
            };

            if (item is ToolStripMenuItem toolItem)
            {
                if (toolItem.Checked)
                {
                    menuItem.ToggleType = MenuItemToggleType.CheckBox;
                    menuItem.IsChecked = true;
                }

                if (!string.IsNullOrEmpty(toolItem.ShortcutKeyDisplayString))
                    menuItem.InputGesture = TryGesture(toolItem.ShortcutKeyDisplayString);

                if (toolItem.DropDownItems.Count > 0)
                    menuItem.ItemsSource = Build(toolItem.DropDownItems);
            }

            menuItem.Click += (_, _) =>
            {
                if (item is ToolStripMenuItem { CheckOnClick: true } checkable)
                    checkable.Checked = !checkable.Checked;
                item.PerformClick();
            };
            built.Add(menuItem);
        }

        return built;
    }

    private static KeyGesture? TryGesture(string text)
    {
        try
        {
            return KeyGesture.Parse(text.Replace("Del", "Delete").Replace("Ins", "Insert"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
    }
}

public sealed class ToolStripControlHost(Control control) : ToolStripItem
{
    public Control Control { get; } = control;
}

/// <summary>A drop-down holding one control - shown as an Avalonia popup under its target.</summary>
public sealed class ToolStripDropDown : IDisposable
{
    private Popup? _popup;

    public ToolStripItemCollection Items { get; } = new();
    public Padding Padding { get; set; }
    public Color BackColor { get; set; }
    public bool AutoClose { get; set; } = true;

    public event EventHandler? Closed;

    public void Show(Control control, Point point)
    {
        var host = Items.OfType<ToolStripControlHost>().FirstOrDefault();
        if (host is null)
            return;

        var content = host.Control;
        var size = host.Size.IsEmpty ? content.Size : host.Size;
        content.Size = size;

        var root = new Border
        {
            Background = new SolidColorBrush(Drawing.Shim.ToAvalonia(BackColor.A > 0 ? BackColor : Color.FromArgb(0x14, 0x1A, 0x27))),
            Width = size.Width,
            Height = size.Height,
            Child = new ShimHost(content),
        };

        _popup = new Popup
        {
            PlacementTarget = control,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft,
            PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight,
            HorizontalOffset = point.X,
            VerticalOffset = point.Y,
            IsLightDismissEnabled = AutoClose,
            Child = root,
        };
        _popup.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);

        if (control.GetVisualRootPanel() is { } panel)
        {
            panel.Children.Add(_popup);
            _popup.Closed += (_, _) => panel.Children.Remove(_popup);
        }

        _popup.IsOpen = true;
    }

    public void Close()
    {
        if (_popup is { IsOpen: true } popup)
            popup.IsOpen = false;
    }

    public void Dispose()
    {
    }

    /// <summary>Lays a shim control out to fill the popup.</summary>
    private sealed class ShimHost : Avalonia.Controls.Decorator
    {
        public ShimHost(Control content) => Child = content;
    }
}

/// <summary>Tooltips, through Avalonia's.</summary>
public sealed class ToolTip : IDisposable
{
    public int InitialDelay { get; set; } = 500;
    public int ReshowDelay { get; set; } = 100;
    public int AutoPopDelay { get; set; } = 5000;
    public bool ShowAlways { get; set; }

    public void SetToolTip(Control control, string? caption)
    {
        Avalonia.Controls.ToolTip.SetTip(control, string.IsNullOrEmpty(caption) ? null : caption);
        Avalonia.Controls.ToolTip.SetShowDelay(control, InitialDelay);
    }

    public string? GetToolTip(Control control) => Avalonia.Controls.ToolTip.GetTip(control) as string;

    public void Dispose()
    {
    }
}

internal static class VisualRoots
{
    /// <summary>A panel at the top of the window a popup can be added to, so it lives in the
    /// logical tree and gets the window's styles.</summary>
    public static Avalonia.Controls.Panel? GetVisualRootPanel(this Control control)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(control) is Window { Content: Avalonia.Controls.Panel panel })
            return panel;
        return null;
    }
}
