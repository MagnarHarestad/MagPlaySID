using System.Drawing.Drawing2D;
using MagSid.Core.Midi;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// The sound bank as a sheet of cards, dropped from the DAW's SOUNDBANK button: one card per
/// sound, its family in colour over the instrument's name and the demo song it comes from, and
/// small tags for what it carries. A click plays it, so a sound is heard before it is taken; a
/// double-click, or the + that appears on the card under the mouse, adds it to the song. Loading
/// a file and opening the bank folder sit along the bottom. Grouped as the bank groups them:
/// instruments, drums, arpeggios.
/// </summary>
internal sealed class SoundBankPicker : Control
{
    private const int Columns = 3;
    private const int SectionHeight = 24;
    private const int CardWidth = 236;
    private const int CardHeight = 56;
    private const int Gap = 8;
    private const int Margin_ = 12;
    private const int HeaderHeight = 46;
    private const int FooterHeight = 38;
    private const int AddSize = 24;

    private static readonly Font TitleFont = new("Segoe UI", 10f, FontStyle.Bold);
    private static readonly Font NameFont = new("Segoe UI", 8.5f);
    private static readonly Font SmallFont = new("Segoe UI", 7.5f, FontStyle.Bold);
    private static readonly Font HintFont = new("Segoe UI", 8f);

    private readonly IReadOnlyList<GmBank.Sound> _sounds;
    private readonly Rectangle[] _cards;
    private readonly List<(string Title, int Top)> _sections = [];
    private int _hovered = -1;
    private int _hoveredLink = -1;
    private int _playing = -1;

    /// <summary>A click: play the sound.</summary>
    public event Action<GmBank.Sound>? Auditioned;

    /// <summary>A double-click or the card's +: add it to the song.</summary>
    public event Action<GmBank.Sound>? Picked;

    /// <summary>The line under the title: what a click and a pick do where the sheet is opened.</summary>
    public string Hint { get; init; } = "Click to hear  ·  double-click or + to add to the song";

    /// <summary>The swap button beside +: put the sound on the instrument <see cref="ChangeTarget"/>
    /// names, in place of what it plays now.</summary>
    public event Action<GmBank.Sound>? Changed;

    /// <summary>"02 Flute" - the instrument the swap button puts a sound on. Null hides the button.</summary>
    public string? ChangeTarget { get; init; }

    public event Action? LoadRequested;
    public event Action? FolderRequested;

    public SoundBankPicker(IReadOnlyList<GmBank.Sound> sounds)
    {
        _sounds = sounds;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = DawPalette.Panel;

        // Each section starts a fresh row under its own title.
        _cards = new Rectangle[sounds.Count];
        int y = HeaderHeight, column = 0;
        GmBank.BankSection? section = null;
        for (int i = 0; i < sounds.Count; i++)
        {
            if (sounds[i].Section != section)
            {
                if (section is not null)
                    y += CardHeight + Gap;
                section = sounds[i].Section;
                _sections.Add((section.ToString()!.ToUpperInvariant(), y));
                y += SectionHeight;
                column = 0;
            }
            else if (column == Columns)
            {
                y += CardHeight + Gap;
                column = 0;
            }

            _cards[i] = new Rectangle(Margin_ + column * (CardWidth + Gap), y, CardWidth, CardHeight);
            column++;
        }

        Size = new Size(Margin_ * 2 + Columns * CardWidth + (Columns - 1) * Gap,
                        y + CardHeight + Margin_ + FooterHeight);
    }

    /// <summary>What a sound is, as the card's top line names it.</summary>
    private static string Family(string key) =>
        MagSid.Editor.App.Controls.InstrumentListHeader.SoundLabel(key).ToUpperInvariant();

    private static Color Accent(int index) => DawPalette.ForInstrument(index + 1);

    private Rectangle CardBounds(int index) => _cards[index];

    private Rectangle AddBounds(int index)
    {
        var card = CardBounds(index);
        return new Rectangle(card.Right - AddSize - 10, card.Top + (card.Height - AddSize) / 2, AddSize, AddSize);
    }

    /// <summary>The swap button, left of the +; only when there is an instrument to change.</summary>
    private Rectangle ChangeBounds(int index)
    {
        var add = AddBounds(index);
        return ChangeTarget is null ? Rectangle.Empty : new Rectangle(add.Left - AddSize - 6, add.Top, AddSize, AddSize);
    }

    private Rectangle LinkBounds(int link)
    {
        int y = Height - FooterHeight + 10;
        return link == 0 ? new Rectangle(Margin_, y, 120, 18) : new Rectangle(Margin_ + 136, y, 130, 18);
    }

    private int CardAt(Point p)
    {
        for (int i = 0; i < _sounds.Count; i++)
            if (CardBounds(i).Contains(p))
                return i;
        return -1;
    }

    private int LinkAt(Point p) => LinkBounds(0).Contains(p) ? 0 : LinkBounds(1).Contains(p) ? 1 : -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int card = CardAt(e.Location), link = LinkAt(e.Location);
        if (card != _hovered || link != _hoveredLink)
        {
            _hovered = card;
            _hoveredLink = link;
            Cursor = card >= 0 || link >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = _hoveredLink = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
            return;

        switch (LinkAt(e.Location))
        {
            case 0: LoadRequested?.Invoke(); return;
            case 1: FolderRequested?.Invoke(); return;
        }

        int card = CardAt(e.Location);
        if (card < 0)
            return;

        if (AddBounds(card).Contains(e.Location))
        {
            Picked?.Invoke(_sounds[card]);
            return;
        }

        if (ChangeBounds(card).Contains(e.Location))
        {
            Changed?.Invoke(_sounds[card]);
            return;
        }

        _playing = card;
        Invalidate();
        Auditioned?.Invoke(_sounds[card]);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int card = CardAt(e.Location);
        if (e.Button == MouseButtons.Left && card >= 0 && !AddBounds(card).Contains(e.Location)
            && !ChangeBounds(card).Contains(e.Location))
            Picked?.Invoke(_sounds[card]);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var border = new Pen(DawPalette.Border))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        TextRenderer.DrawText(g, "SOUND BANK", TitleFont, new Point(Margin_ - 2, 10), DawPalette.Text);
        TextRenderer.DrawText(g, Hint, HintFont,
            new Point(Margin_ - 1, 27), DawPalette.TextDim);

        foreach (var (title, top) in _sections)
        {
            var size = TextRenderer.MeasureText(g, title, SmallFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, title, SmallFont, new Point(Margin_, top + 4), DawPalette.TextDim, TextFormatFlags.NoPadding);
            using var rule = new Pen(DawPalette.BorderSoft);
            int middle = top + 4 + size.Height / 2;
            g.DrawLine(rule, Margin_ + size.Width + 8, middle, Width - Margin_, middle);
        }

        for (int i = 0; i < _sounds.Count; i++)
            DrawCard(g, i);

        using (var rule = new Pen(DawPalette.BorderSoft))
            g.DrawLine(rule, Margin_, Height - FooterHeight, Width - Margin_, Height - FooterHeight);
        DrawLink(g, 0, "Load from file...");
        DrawLink(g, 1, "Open bank folder");
    }

    private void DrawCard(Graphics g, int index)
    {
        var sound = _sounds[index];
        var card = CardBounds(index);
        var accent = Accent(index);
        bool hovered = index == _hovered;

        using (var path = Rounded(card, 6))
        {
            using var fill = new SolidBrush(hovered ? DawPalette.BorderSoft : DawPalette.PanelHeader);
            g.FillPath(fill, path);
            using var edge = new Pen(hovered || index == _playing ? accent : DawPalette.Border, hovered ? 1.5f : 1f);
            g.DrawPath(edge, path);
        }

        // The family's colour down the left edge, the way the strip marks an instrument.
        using (var stripe = new SolidBrush(accent))
            g.FillRectangle(stripe, card.Left + 1, card.Top + 8, 3, card.Height - 16);

        TextRenderer.DrawText(g, Family(sound.Key), SmallFont, new Point(card.Left + 14, card.Top + 9), accent);
        TextRenderer.DrawText(g, sound.Instrument.Name, NameFont, new Point(card.Left + 13, card.Top + 24), DawPalette.Text);

        // What it carries, in small chips beside its origin.
        var tags = new List<string> { sound.Origin };
        if (sound.Instrument.HasVibrato) tags.Add("VIB");
        if (sound.Instrument.Filter.Count > 0) tags.Add("FLT");
        if (sound.Instrument.HasArpeggio && sound.Instrument.Arpeggio.Count > 0) tags.Add("ARP");
        int x = card.Left + 14;
        foreach (var tag in tags)
        {
            var size = TextRenderer.MeasureText(g, tag, SmallFont, Size.Empty, TextFormatFlags.NoPadding);
            var chip = new Rectangle(x, card.Top + 40, size.Width + 8, 12);
            if (tag != sound.Origin)
            {
                using var chipFill = new SolidBrush(DawPalette.WithAlpha(accent, 40));
                g.FillRectangle(chipFill, chip);
            }
            TextRenderer.DrawText(g, tag, SmallFont, chip, tag == sound.Origin ? DawPalette.TextDim : accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x = chip.Right + 4;
        }

        if (!hovered)
            return;

        var add = AddBounds(index);
        using (var circle = new SolidBrush(accent))
            g.FillEllipse(circle, add);
        using var plus = new Pen(DawPalette.Panel, 2f);
        int cx = add.Left + add.Width / 2, cy = add.Top + add.Height / 2;
        g.DrawLine(plus, cx - 6, cy, cx + 6, cy);
        g.DrawLine(plus, cx, cy - 6, cx, cy + 6);

        if (ChangeTarget is null)
            return;

        // Swap: two arrows, one each way - the sound goes onto the selected instrument.
        var change = ChangeBounds(index);
        using (var ring = new Pen(accent, 2f))
            g.DrawEllipse(ring, change.Left + 1, change.Top + 1, change.Width - 2, change.Height - 2);
        using var arrow = new Pen(accent, 1.6f);
        int sx = change.Left + change.Width / 2, sy = change.Top + change.Height / 2;
        g.DrawLine(arrow, sx - 6, sy - 3, sx + 6, sy - 3);
        g.DrawLine(arrow, sx + 6, sy - 3, sx + 3, sy - 6);
        g.DrawLine(arrow, sx + 6, sy + 3, sx - 6, sy + 3);
        g.DrawLine(arrow, sx - 6, sy + 3, sx - 3, sy + 6);
    }

    private void DrawLink(Graphics g, int link, string text) =>
        TextRenderer.DrawText(g, text, HintFont, LinkBounds(link).Location,
            link == _hoveredLink ? DawPalette.Oscillator : DawPalette.TextDim);

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
