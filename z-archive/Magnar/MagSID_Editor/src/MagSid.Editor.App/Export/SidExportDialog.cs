namespace MagSid.Editor.App.Export;

/// <summary>Small modal prompt for the PSID header's Name/Author/Released text fields before
/// writing a .sid file. Each is a fixed 32-byte ASCII field in the header (see
/// SidExporter.PutAsciiField), so the limits are the format's, not this dialog's: the boxes
/// refuse a 32nd character rather than letting the exporter quietly drop it, and anything that
/// is not ASCII is caught here too - ASCII encoding turns an "a" into a question mark without
/// complaint, which would put "Magn?r" in a released .sid.</summary>
public sealed class SidExportDialog : Form
{
    /// <summary>32-byte field, one byte of which SidExporter.PutAsciiField keeps for the
    /// terminating null.</summary>
    private const int MaxFieldLength = 31;

    private readonly TextBox _name = new() { Width = 260, MaxLength = MaxFieldLength };
    private readonly TextBox _author = new() { Width = 260, MaxLength = MaxFieldLength };
    private readonly TextBox _released = new() { Width = 260, MaxLength = MaxFieldLength };

    public string SongName => _name.Text;
    public string Author => _author.Text;
    public string Released => _released.Text;

    public SidExportDialog(string defaultName, string defaultAuthor, string defaultReleased)
    {
        Text = "Export to .sid";
        Icon = AppIcon.Value;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        DarkTheme.ApplyTitleBar(this);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        // Labels and the layout panels pick the dark scheme up from the form, but a TextBox and
        // a Button each default to a system colour instead of inheriting - see DarkTheme.
        BackColor = EditorPalette.Background;
        ForeColor = EditorPalette.Body;

        // MaxLength governs typing, not assignment, so the defaults are cut here too - a long
        // song filename or an author remembered before this limit existed would otherwise sit in
        // the box looking accepted and then lose its tail on export.
        _name.Text = Fit(defaultName);
        _author.Text = Fit(defaultAuthor);
        _released.Text = Fit(defaultReleased);

        foreach (var box in new[] { _name, _author, _released })
            DarkTheme.Apply(box);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        layout.Controls.Add(new Label { Text = "Name:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 0);
        layout.Controls.Add(_name, 1, 0);
        layout.Controls.Add(new Label { Text = "Author:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 1);
        layout.Controls.Add(_author, 1, 1);
        layout.Controls.Add(new Label { Text = "Released:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 2);
        layout.Controls.Add(_released, 1, 2);
        layout.Controls.Add(new Label
        {
            Text = $"Each field holds {MaxFieldLength} characters, ASCII only.",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = EditorPalette.Subtitle,
            Margin = new Padding(3, 6, 3, 3),
        }, 1, 3);

        var okButton = new Button { Text = "Export", DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right };
        DarkTheme.Apply(okButton);
        DarkTheme.Apply(cancelButton);
        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Bottom,
        };
        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(okButton);

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            RowCount = 2,
            ColumnCount = 1,
        };
        root.Controls.Add(layout, 0, 0);
        root.Controls.Add(buttonPanel, 0, 1);
        Controls.Add(root);

        // Validate on the way out rather than as they type: a half-typed name is not an error,
        // and nothing is lost until Export is actually pressed.
        okButton.Click += (_, _) =>
        {
            if (FirstNonAsciiField() is { } bad)
            {
                MessageBox.Show(this,
                    $"The {bad.Field} field contains characters the .sid header cannot hold: " +
                    $"{bad.Characters}." + Environment.NewLine + Environment.NewLine +
                    "PSID fields are plain ASCII, so these would be written as question marks. " +
                    "Please replace them.",
                    "Export to .sid", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private static string Fit(string value) =>
        value.Length <= MaxFieldLength ? value : value[..MaxFieldLength];

    /// <summary>The first field holding anything outside printable ASCII, with the offending
    /// characters listed - so "which character?" is answered rather than left to hunt for.</summary>
    private (string Field, string Characters)? FirstNonAsciiField()
    {
        foreach (var (field, box) in new[]
                 { ("Name", _name), ("Author", _author), ("Released", _released) })
        {
            var bad = box.Text.Where(c => c < 0x20 || c > 0x7E).Distinct().ToArray();
            if (bad.Length > 0)
            {
                return (field, string.Join(" ", bad.Select(c => $"'{c}'")));
            }
        }

        return null;
    }
}
