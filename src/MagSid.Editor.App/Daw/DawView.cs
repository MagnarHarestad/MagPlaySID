using MagSid.Core.Model;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// The DAW view: an instrument list down the side and one of the DAW's own editors filling the rest.
///
/// It edits the same <see cref="Song"/> the tracker does - no conversion, no second format, nothing
/// that can fall out of step. That is the same rule that keeps the C# player and the assembly player
/// honest, and it is the reason switching views can never lose or change anything.
///
/// Only the instrument editor is built so far. The arrangement and the piano roll get their own tabs
/// beside it; until then the tabs say so rather than showing an empty panel.
/// </summary>
internal sealed class DawView : Panel
{
    private readonly DawInstrumentView _instrument = new();
    private readonly Panel _placeholder = new();
    private readonly FlowLayoutPanel _tabs = new();
    private readonly ListBox _instrumentList = new();

    private readonly List<TabButton> _tabButtons = [];

    private Song? _song;
    private DawTab _current = DawTab.Instrument;

    public DawView()
    {
        BackColor = DawPalette.Background;
        BuildLayout();
    }

    private enum DawTab
    {
        Arrange,
        Instrument,
        PianoRoll,
    }

    /// <summary>Raised when an edit here changes the song, so the host can mark it dirty and
    /// refresh whatever else is showing the same data.</summary>
    public event Action<Instrument>? InstrumentEdited;

    public void LoadSong(Song song)
    {
        _song = song;
        RefreshInstrumentList();
    }

    /// <summary>Follows the retro view's own selection, so switching views lands on the instrument
    /// that was already being worked on rather than resetting to the first.</summary>
    public void SelectInstrument(int number)
    {
        if (_song is null)
            return;

        int index = _song.Instruments.FindIndex(i => i.Number == number);
        if (index >= 0 && index != _instrumentList.SelectedIndex)
            _instrumentList.SelectedIndex = index;
    }

    public int? SelectedInstrumentNumber =>
        _instrumentList.SelectedIndex >= 0 && _song is { } song
        && _instrumentList.SelectedIndex < song.Instruments.Count
            ? song.Instruments[_instrumentList.SelectedIndex].Number
            : null;

    private void RefreshInstrumentList()
    {
        if (_song is null)
            return;

        int previous = _instrumentList.SelectedIndex;

        _instrumentList.BeginUpdate();
        _instrumentList.Items.Clear();
        foreach (var instrument in _song.Instruments)
        {
            _instrumentList.Items.Add(string.IsNullOrWhiteSpace(instrument.Name)
                ? $"{instrument.Number:00}"
                : $"{instrument.Number:00}   {instrument.Name}");
        }

        _instrumentList.EndUpdate();

        if (_instrumentList.Items.Count > 0)
            _instrumentList.SelectedIndex = Math.Clamp(previous, 0, _instrumentList.Items.Count - 1);
    }

    private void BuildLayout()
    {
        _tabs.Dock = DockStyle.Top;
        _tabs.Height = 36;
        _tabs.BackColor = DawPalette.PanelHeader;
        _tabs.Padding = new Padding(10, 5, 0, 0);
        _tabs.WrapContents = false;

        foreach (var (tab, label, ready) in new[]
                 {
                     (DawTab.Arrange, "ARRANGE", false),
                     (DawTab.Instrument, "INSTRUMENT", true),
                     (DawTab.PianoRoll, "PIANO ROLL", false),
                 })
        {
            var button = new TabButton(label) { Tag = tab, Enabled = ready };
            button.Click += (_, _) => Show(tab);
            _tabButtons.Add(button);
            _tabs.Controls.Add(button);
        }

        _instrumentList.Dock = DockStyle.Left;
        _instrumentList.Width = 186;
        _instrumentList.BackColor = Color.FromArgb(0x12, 0x17, 0x22);
        _instrumentList.ForeColor = DawPalette.Text;
        _instrumentList.BorderStyle = BorderStyle.None;
        _instrumentList.Font = new Font("Consolas", 9f);
        _instrumentList.IntegralHeight = false;
        _instrumentList.ItemHeight = 22;
        _instrumentList.SelectedIndexChanged += (_, _) => ShowSelectedInstrument();

        _instrument.Dock = DockStyle.Fill;
        _instrument.Edited += instrument =>
        {
            RefreshInstrumentList();
            InstrumentEdited?.Invoke(instrument);
        };

        _placeholder.Dock = DockStyle.Fill;
        _placeholder.BackColor = DawPalette.Background;
        _placeholder.Visible = false;
        _placeholder.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = DawPalette.TextFaint,
            Font = new Font("Segoe UI", 10f),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Not built yet — use RETRO for this.",
        });

        Controls.Add(_instrument);
        Controls.Add(_placeholder);
        Controls.Add(_instrumentList);
        Controls.Add(_tabs);

        Show(DawTab.Instrument);
    }

    private void ShowSelectedInstrument()
    {
        if (_song is null || _instrumentList.SelectedIndex < 0
            || _instrumentList.SelectedIndex >= _song.Instruments.Count)
        {
            _instrument.LoadInstrument(null);
            return;
        }

        _instrument.LoadInstrument(_song.Instruments[_instrumentList.SelectedIndex]);
    }

    private void Show(DawTab tab)
    {
        _current = tab;
        _instrument.Visible = tab == DawTab.Instrument;
        _placeholder.Visible = tab != DawTab.Instrument;
        _instrumentList.Visible = tab == DawTab.Instrument;

        foreach (var button in _tabButtons)
            button.Active = (DawTab)button.Tag! == tab;
    }

    /// <summary>One of the view tabs across the top of the DAW.</summary>
    private sealed class TabButton : Control
    {
        private bool _active;
        private bool _hovered;

        public TabButton(string caption)
        {
            Text = caption;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = DawPalette.PanelHeader;
            Size = new Size(108, 26);
            Cursor = Cursors.Hand;
        }

        public bool Active
        {
            get => _active;
            set
            {
                _active = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            var box = new Rectangle(0, 0, Width - 2, Height - 1);
            if (_active || (_hovered && Enabled))
            {
                using var fill = new SolidBrush(_active
                    ? Color.FromArgb(0x1D, 0x2A, 0x3E)
                    : Color.FromArgb(0x18, 0x20, 0x30));
                g.FillRectangle(fill, box);
            }

            using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            using var ink = new SolidBrush(!Enabled
                ? DawPalette.TextFaint
                : _active ? DawPalette.Oscillator : DawPalette.TextDim);
            var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Text, font, ink, box, centred);
        }
    }
}
