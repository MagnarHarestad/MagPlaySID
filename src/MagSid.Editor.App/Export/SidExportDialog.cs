using MagSid.Core;

namespace MagSid.Editor.App.Export;

/// <summary>Small modal prompt for the PSID header's Name/Author/Released text fields and the
/// address the tune loads at, before writing a .sid file. Each text field is a fixed 32-byte
/// ASCII field in the header (see SidExporter.PutAsciiField), so the limits are the format's, not
/// this dialog's: the boxes refuse a 32nd character rather than letting the exporter quietly drop
/// it, and anything that is not ASCII is caught here too - ASCII encoding turns an "a" into a
/// question mark without complaint, which would put "Magn?r" in a released .sid.</summary>
public sealed class SidExportDialog : Form
{
    /// <summary>32-byte field, one byte of which SidExporter.PutAsciiField keeps for the
    /// terminating null.</summary>
    private const int MaxFieldLength = 31;

    private readonly TextBox _name = new() { Width = 260, MaxLength = MaxFieldLength };
    private readonly TextBox _author = new() { Width = 260, MaxLength = MaxFieldLength };
    private readonly TextBox _released = new() { Width = 260, MaxLength = MaxFieldLength };
    private readonly TextBox _loadAddress = new() { Width = 60, MaxLength = 5, TextAlign = HorizontalAlignment.Center };

    /// <summary>Needed to tell a usable address from one that would run the tune into I/O or off
    /// the top of memory, which depends on how long this particular song is.</summary>
    private readonly int _songByteLength;

    public string SongName => _name.Text;
    public string Author => _author.Text;
    public string Released => _released.Text;

    /// <summary>Only meaningful once the dialog closed with OK - the OK handler refuses to close
    /// on anything SidLoadAddress rejects.</summary>
    public int LoadAddress => SidLoadAddress.TryParse(_loadAddress.Text) ?? SidLoadAddress.Default;

    public SidExportDialog(
        string defaultName, string defaultAuthor, string defaultReleased,
        int defaultLoadAddress, int songByteLength)
    {
        _songByteLength = songByteLength;
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
        _loadAddress.Text = $"${defaultLoadAddress:X4}";

        foreach (var box in new[] { _name, _author, _released, _loadAddress })
            DarkTheme.Apply(box);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 5,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        layout.Controls.Add(new Label { Text = "Name:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 0);
        layout.Controls.Add(_name, 1, 0);
        layout.Controls.Add(new Label { Text = "Author:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 1);
        layout.Controls.Add(_author, 1, 1);
        layout.Controls.Add(new Label { Text = "Released:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 2);
        layout.Controls.Add(_released, 1, 2);

        // The range is spelled out beside the box as it is typed: "$4400" on its own does not say
        // whether this song still clears the I/O area, and the length is the half of that answer
        // the user cannot see.
        var rangeLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = EditorPalette.Subtitle,
            Margin = new Padding(8, 8, 3, 3),
        };
        var addressRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        addressRow.Controls.Add(_loadAddress);
        addressRow.Controls.Add(rangeLabel);
        layout.Controls.Add(new Label { Text = "Load address:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, 3);
        layout.Controls.Add(addressRow, 1, 3);

        void UpdateRange()
        {
            if (SidLoadAddress.TryParse(_loadAddress.Text) is { } address)
            {
                rangeLabel.Text = $"${address:X4}-${address + _songByteLength - 1:X4}  ({_songByteLength} bytes)";
                rangeLabel.ForeColor = SidLoadAddress.Validate(address, _songByteLength) is null
                    ? EditorPalette.Subtitle
                    : EditorPalette.Warning;
            }
            else
            {
                rangeLabel.Text = $"{_songByteLength} bytes - address must be 4 hex digits";
                rangeLabel.ForeColor = EditorPalette.Warning;
            }
        }

        _loadAddress.TextChanged += (_, _) => UpdateRange();
        UpdateRange();

        layout.Controls.Add(new Label
        {
            Text = $"Name, Author and Released hold {MaxFieldLength} characters each, ASCII only.",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = EditorPalette.Subtitle,
            Margin = new Padding(3, 6, 3, 3),
        }, 1, 4);

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
            if (SidLoadAddress.TryParse(_loadAddress.Text) is not { } address)
            {
                MessageBox.Show(this,
                    "The load address must be a hexadecimal number of up to four digits, " +
                    "written as $2000, 2000 or 0x2000.",
                    "Export to .sid", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _loadAddress.Focus();
                _loadAddress.SelectAll();
                return;
            }

            if (SidLoadAddress.Validate(address, _songByteLength) is { } addressProblem)
            {
                MessageBox.Show(this, addressProblem,
                    "Export to .sid", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _loadAddress.Focus();
                _loadAddress.SelectAll();
                return;
            }

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
