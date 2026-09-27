using System.Runtime.InteropServices;

namespace MagSid.Editor.App;

/// <summary>
/// Applies <see cref="EditorPalette"/> to the stock WinForms controls that do not take a dark
/// scheme on their own.
///
/// Setting BackColor/ForeColor is enough for a Panel or a Label, but not for the three strip
/// controls or a DataGridView. A MenuStrip painted only via BackColor still renders its
/// drop-downs, hover highlights, separators and the margin behind item images from system
/// colours, so a dark menu bar drops open into a light menu. That is why this goes through a
/// ProfessionalColorTable plus a renderer rather than assigning colours directly - the table is
/// the only place those parts of the paint are reachable from.
///
/// A DataGridView needs EnableHeadersVisualStyles turned off for the same reason: while it is
/// on, the column headers are drawn by the OS theme and ignore the styles set on them.
/// </summary>
public static class DarkTheme
{
    /// <summary>The colours ToolStrip/MenuStrip/StatusStrip reach for while painting. Every
    /// override here is a spot that would otherwise come back light.</summary>
    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => EditorPalette.FieldBackground;
        public override Color MenuStripGradientEnd => EditorPalette.FieldBackground;

        public override Color ToolStripGradientBegin => EditorPalette.FieldBackground;
        public override Color ToolStripGradientMiddle => EditorPalette.FieldBackground;
        public override Color ToolStripGradientEnd => EditorPalette.FieldBackground;
        public override Color ToolStripContentPanelGradientBegin => EditorPalette.Background;
        public override Color ToolStripContentPanelGradientEnd => EditorPalette.Background;
        public override Color ToolStripPanelGradientBegin => EditorPalette.FieldBackground;
        public override Color ToolStripPanelGradientEnd => EditorPalette.FieldBackground;
        public override Color ToolStripBorder => EditorPalette.Border;

        public override Color StatusStripGradientBegin => EditorPalette.FieldBackground;
        public override Color StatusStripGradientEnd => EditorPalette.FieldBackground;

        // The drop-down panel itself, plus the strip down its left edge where item images go.
        public override Color ToolStripDropDownBackground => EditorPalette.FieldBackground;
        public override Color ImageMarginGradientBegin => EditorPalette.FieldBackground;
        public override Color ImageMarginGradientMiddle => EditorPalette.FieldBackground;
        public override Color ImageMarginGradientEnd => EditorPalette.FieldBackground;

        public override Color MenuBorder => EditorPalette.Border;
        public override Color MenuItemBorder => EditorPalette.Border;
        public override Color MenuItemSelected => EditorPalette.ButtonHover;
        public override Color MenuItemSelectedGradientBegin => EditorPalette.ButtonHover;
        public override Color MenuItemSelectedGradientEnd => EditorPalette.ButtonHover;
        public override Color MenuItemPressedGradientBegin => EditorPalette.ButtonBackground;
        public override Color MenuItemPressedGradientMiddle => EditorPalette.ButtonBackground;
        public override Color MenuItemPressedGradientEnd => EditorPalette.ButtonBackground;

        public override Color ButtonSelectedHighlight => EditorPalette.ButtonHover;
        public override Color ButtonSelectedHighlightBorder => EditorPalette.Border;
        public override Color ButtonSelectedGradientBegin => EditorPalette.ButtonHover;
        public override Color ButtonSelectedGradientMiddle => EditorPalette.ButtonHover;
        public override Color ButtonSelectedGradientEnd => EditorPalette.ButtonHover;
        public override Color ButtonSelectedBorder => EditorPalette.Border;
        public override Color ButtonPressedGradientBegin => EditorPalette.ButtonBackground;
        public override Color ButtonPressedGradientMiddle => EditorPalette.ButtonBackground;
        public override Color ButtonPressedGradientEnd => EditorPalette.ButtonBackground;
        public override Color ButtonPressedBorder => EditorPalette.Border;
        public override Color ButtonCheckedGradientBegin => EditorPalette.Selection;
        public override Color ButtonCheckedGradientMiddle => EditorPalette.Selection;
        public override Color ButtonCheckedGradientEnd => EditorPalette.Selection;

        public override Color CheckBackground => EditorPalette.Selection;
        public override Color CheckSelectedBackground => EditorPalette.ButtonHover;
        public override Color CheckPressedBackground => EditorPalette.ButtonBackground;

        public override Color SeparatorDark => EditorPalette.Border;
        public override Color SeparatorLight => EditorPalette.ButtonBackground;
        public override Color GripDark => EditorPalette.Border;
        public override Color GripLight => EditorPalette.ButtonBackground;
    }

    /// <summary>Item text and drop-down arrows are drawn by the renderer, not the colour table,
    /// so they need overriding separately or they stay near-black on the dark strip.</summary>
    private sealed class DarkRenderer() : ToolStripProfessionalRenderer(new DarkColorTable())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? EditorPalette.Body : EditorPalette.Subtitle;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = EditorPalette.Body;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // A StatusStrip paints a 1px pure-white highlight along its own top edge, straight
            // from the renderer - no ProfessionalColorTable entry covers it. Left alone it is a
            // bright seam right across the bottom of an otherwise dark window (measured at
            // 795 white pixels wide before this override existed).
            if (e.ToolStrip is StatusStrip)
            {
                using var pen = new Pen(EditorPalette.Border);
                e.Graphics.DrawLine(pen, 0, 0, e.ToolStrip.Width, 0);
                return;
            }

            base.OnRenderToolStripBorder(e);
        }
    }

    /// <summary>One shared instance - the renderer is stateless, and ToolStrip only ever reads
    /// from it.</summary>
    public static readonly ToolStripRenderer StripRenderer = new DarkRenderer();

    public static void Apply(ToolStrip strip)
    {
        strip.Renderer = StripRenderer;
        strip.BackColor = EditorPalette.FieldBackground;
        strip.ForeColor = EditorPalette.Body;
    }

    public static void Apply(DataGridView grid)
    {
        // Without this the OS theme paints the column headers and quietly ignores
        // ColumnHeadersDefaultCellStyle entirely - the one setting that makes the rest work.
        grid.EnableHeadersVisualStyles = false;

        grid.BackgroundColor = EditorPalette.Background;
        grid.GridColor = EditorPalette.ButtonBackground;
        grid.BorderStyle = BorderStyle.None;
        grid.ForeColor = EditorPalette.Body;

        grid.DefaultCellStyle.BackColor = EditorPalette.Background;
        grid.DefaultCellStyle.ForeColor = EditorPalette.Body;
        grid.DefaultCellStyle.SelectionBackColor = EditorPalette.Selection;
        grid.DefaultCellStyle.SelectionForeColor = EditorPalette.Title;

        grid.ColumnHeadersDefaultCellStyle.BackColor = EditorPalette.FieldBackground;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = EditorPalette.Heading;
        // Headers are not selectable here, but a click still repaints them through the
        // selection style - left equal to the normal one so they do not flash.
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = EditorPalette.FieldBackground;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = EditorPalette.Heading;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // 20 is the documented attribute from Windows 10 20H1 onwards; 19 is what the same flag was
    // called before that. Neither is fatal on an OS that does not recognise it - the call just
    // returns a failure code - so trying one and falling back to the other avoids needing a
    // build-number table.
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>Asks the window manager for a dark title bar. Everything else here is painted by
    /// the app, but the caption is drawn by Windows itself, so a fully dark window still ends up
    /// with a white strip along the top without this. Safe to call from a constructor: it waits
    /// for the handle if one does not exist yet, since the attribute is set per-window.</summary>
    public static void ApplyTitleBar(Form form)
    {
        if (form.IsHandleCreated)
        {
            Set(form.Handle);
        }
        else
        {
            form.HandleCreated += (s, _) => Set(((Form)s!).Handle);
        }

        static void Set(IntPtr handle)
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
    }

    /// <summary>A TextBox does not inherit BackColor from its parent the way a Label does - its
    /// default is the system window colour, so it stays a white box on a dark form until this
    /// is set explicitly.</summary>
    public static void Apply(TextBox box)
    {
        box.BackColor = EditorPalette.FieldBackground;
        box.ForeColor = EditorPalette.Body;
        box.BorderStyle = BorderStyle.FixedSingle;
    }

    /// <summary>The same flat dark button the dialogs already use, so a stock Button dropped in
    /// a panel does not come back as grey-on-grey.</summary>
    public static void Apply(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = EditorPalette.ButtonBackground;
        button.ForeColor = EditorPalette.Body;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderColor = EditorPalette.Border;
        button.FlatAppearance.MouseOverBackColor = EditorPalette.ButtonHover;
    }
}
