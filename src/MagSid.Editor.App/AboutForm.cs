using System.Drawing.Text;

namespace MagSid.Editor.App;

/// <summary>
/// The About window: what the editor is, how a song is put together, and every key binding.
///
/// Built as formatted text in a read-only RichTextBox rather than a pile of Labels, so the
/// whole document can be written in one place and scrolls as one. It carries the tracker's own
/// dark palette so it reads as part of the app rather than a stock Windows dialog.
/// </summary>
public sealed class AboutForm : Form
{
    // Shared with every other dark window in the app via EditorPalette so they cannot drift
    // into near-miss shades of each other.
    private static readonly Color ColorBackground = EditorPalette.Background;
    private static readonly Color ColorTitle = EditorPalette.Title;
    private static readonly Color ColorSubtitle = EditorPalette.Subtitle;
    private static readonly Color ColorHeading = EditorPalette.Heading;
    private static readonly Color ColorBody = EditorPalette.Body;
    private static readonly Color ColorMono = EditorPalette.Mono;

    private static readonly Font TitleFont = new("Segoe UI", 18f, FontStyle.Bold);
    private static readonly Font SubtitleFont = new("Segoe UI", 9.5f, FontStyle.Regular);
    private static readonly Font HeadingFont = new("Segoe UI", 12f, FontStyle.Bold);
    private static readonly Font BodyFont = new("Segoe UI", 10f, FontStyle.Regular);
    private static readonly Font MonoFont = new("Consolas", 10f, FontStyle.Regular);

    private readonly RichTextBox _text = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        BackColor = ColorBackground,
        TabStop = false,
        Margin = new Padding(0),
    };

    public AboutForm()
    {
        Text = "About MagSID Editor";
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 780);
        MinimumSize = new Size(520, 400);
        BackColor = ColorBackground;
        DarkTheme.ApplyTitleBar(this);

        // Styled explicitly: a default WinForms button on this dark background renders as dark
        // grey text on dark grey and is essentially invisible.
        var close = new Button
        {
            Text = "Close",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(8),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0x28, 0x28, 0x3C),
            ForeColor = ColorBody,
            Padding = new Padding(14, 4, 14, 4),
            UseVisualStyleBackColor = false,
        };
        close.FlatAppearance.BorderColor = Color.FromArgb(0x4A, 0x4A, 0x6E);
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x36, 0x36, 0x50);
        close.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            BackColor = ColorBackground,
            Padding = new Padding(8, 4, 8, 8),
        };
        buttons.Controls.Add(close);

        var padding = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 12, 8), BackColor = ColorBackground };
        padding.Controls.Add(_text);

        Controls.Add(padding);
        Controls.Add(buttons);

        // Escape closes, and the button holds focus so the read-only text doesn't sit there
        // blinking a caret at content nobody can edit.
        AcceptButton = close;
        CancelButton = close;

        BuildDocument();
        Shown += (_, _) => close.Focus();
    }

    private void BuildDocument()
    {
        _text.Clear();
        foreach (var run in AboutDocument.Build(typeof(AboutForm).Assembly.GetName().Version, AboutDocument.Windows))
        {
            var (font, color) = run.Style switch
            {
                AboutDocument.Style.Title => (TitleFont, ColorTitle),
                AboutDocument.Style.Subtitle => (SubtitleFont, ColorSubtitle),
                AboutDocument.Style.Heading => (HeadingFont, ColorHeading),
                AboutDocument.Style.Mono => (MonoFont, ColorMono),
                _ => (BodyFont, ColorBody),
            };
            Write(run.Text, font, color);
        }

        _text.SelectionStart = 0;
        _text.ScrollToCaret();
    }

    /// <summary>Appends a run and then formats exactly that range. Setting SelectionFont
    /// *before* AppendText looks like it should work but doesn't reliably: AppendText applies
    /// the formatting already in force at the insertion point, so runs silently inherit the
    /// previous one's font - which is how the key tables ended up rendering half in Consolas
    /// and half in the proportional body font.</summary>
    private void Write(string text, Font font, Color color)
    {
        int start = _text.TextLength;
        _text.AppendText(text);
        _text.Select(start, text.Length);
        _text.SelectionFont = font;
        _text.SelectionColor = color;
        _text.Select(_text.TextLength, 0);
    }
}
