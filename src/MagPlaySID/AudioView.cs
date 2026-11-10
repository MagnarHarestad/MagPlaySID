using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MagPlay.Audio;
using MagPlaySid.Player;
using Avalonia.Rendering;

namespace MagPlaySid;

/// <summary>
/// A recording's notes as they are heard: a column for each key of its range, as a piano's lie, and
/// each note a bar in its column, scrolling up through the line where it sounds - as the tracker
/// rows do, the coming notes below. Many at once, as the transcription hears them; those sounding now
/// lit. Beside it, the notes sounding and the chord they make, and how far the hearing has got.
/// </summary>
internal sealed class AudioView : Control, ICustomHitTest
{
    /// <summary>Under the pointer anywhere in its bounds: tested so, not against each of the
    /// lines and shapes it last drew - every move of the mouse tested them all.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private double _scale = 1;

    private double HeaderHeight => 26 * _scale;
    private double FontSize => 13 * _scale;
    private double PixelsPerSecond => 90 * _scale;
    private double Gap => 10 * _scale;

    private static readonly Typeface Mono = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"));
    private static readonly Typeface Bold = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"), FontStyle.Normal, FontWeight.Bold);
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly bool[] Black = [false, true, false, true, false, false, true, false, true, false, true, false];

    private readonly Dictionary<(string Text, Color Ink, bool Bold), TextLayout> _laidOut = [];

    private IReadOnlyList<HeardNote> _notes = [];
    private double _longest;
    private double _heard;
    private bool _complete;

    // The keys shown: whole octaves about the notes heard, three at the least.
    private int _lowKey = 48, _highKey = 84;
    private double _now;
    private double _charWidth;

    // The panel: the notes sounding and their chord, made when they change.
    private readonly List<int> _sounding = [];
    private readonly List<int> _shownSounding = [];
    private string _chordText = "", _notesText = "", _heardText = "";

    public AudioView() => _charWidth = Measure("0").WidthIncludingTrailingWhitespace;

    private double PanelWidth => 30 * _charWidth;

    private double ColumnWidth => Math.Max(6 * _scale, 9 * _scale);

    private double RollWidth => (_highKey - _lowKey + 1) * ColumnWidth;

    /// <summary>The width the roll and the panel take.</summary>
    public double ContentWidth => RollWidth + Gap + PanelWidth;

    public double TextScale
    {
        get => _scale;
        set
        {
            _scale = value;
            Forget();
            _charWidth = Measure("0").WidthIncludingTrailingWhitespace;
            Width = ContentWidth;
            InvalidateVisual();
        }
    }

    /// <summary>A new recording: nothing heard yet.</summary>
    public void Reset()
    {
        _notes = [];
        _longest = 0;
        _heard = 0;
        _complete = false;
        (_lowKey, _highKey) = (48, 84);
        _sounding.Clear();
        _shownSounding.Clear();
        _chordText = _notesText = "";
        _heardText = "Listening...";
        Width = ContentWidth;
        InvalidateVisual();
    }

    /// <summary>The notes heard so far, in order of their start, and how far into the recording the
    /// hearing has got - <paramref name="complete"/> once it is all heard.</summary>
    public void SetNotes(IReadOnlyList<HeardNote> notes, double heardSeconds, bool complete)
    {
        double longest = 0;
        Span<int> perKey = stackalloc int[128];
        foreach (var note in notes)
        {
            longest = Math.Max(longest, note.End - note.Start);
            perKey[Math.Clamp(note.Pitch, 0, 127)]++;
        }
        // The range: from the lowest key to the highest heard more than once - one stray note at
        // the edge does not widen the roll by octaves.
        int low = int.MaxValue, high = int.MinValue;
        for (int key = 0; key < 128; key++)
            if (perKey[key] > 1 || (perKey[key] == 1 && notes.Count < 20))
            {
                low = Math.Min(low, key);
                high = Math.Max(high, key);
            }
        (_notes, _longest, _heard, _complete) = (notes, longest, heardSeconds, complete);
        if (low <= high)
        {
            // Whole octaves, C to B, about what has been heard - growing, never shrinking under the eye.
            int from = Math.Min(_lowKey, (low / 12) * 12), to = Math.Max(_highKey, ((high / 12) * 12) + 11);
            while (to - from + 1 < 36)
            {
                if (to + 12 <= 119)
                    to += 12;
                else
                    from -= 12;
            }
            if ((from, to) != (_lowKey, _highKey))
            {
                (_lowKey, _highKey) = (from, to);
                Width = ContentWidth;
            }
        }
        _heardText = complete ? $"{notes.Count} notes heard" : $"Listening... {Clock(heardSeconds)}";
        InvalidateVisual();
    }

    private static string Clock(double seconds) => $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";

    /// <summary>Where the recording is heard: drawn again when the bars move a pixel, or the notes
    /// sounding change - paused, not at all.</summary>
    public void Show(AudioTune tune, long at)
    {
        double now = at / (double)AudioTune.SampleRate;
        double pixels = Math.Round(now * PixelsPerSecond * 2) / 2;
        bool changed = pixels != Math.Round(_now * PixelsPerSecond * 2) / 2;
        _now = now;

        Span<HeardNote> sounding = stackalloc HeardNote[32];
        int count = tune.NotesAt(now, sounding);
        _sounding.Clear();
        for (int i = 0; i < count; i++)
            if (!_sounding.Contains(sounding[i].Pitch))
                _sounding.Add(sounding[i].Pitch);
        _sounding.Sort();
        if (!Same(_sounding, _shownSounding))
        {
            _shownSounding.Clear();
            _shownSounding.AddRange(_sounding);
            _notesText = string.Join(" ", _sounding.Select(NoteName));
            _chordText = Chord(_sounding) ?? "";
            changed = true;
        }
        if (changed)
            InvalidateVisual();
    }

    private static bool Same(List<int> a, List<int> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    private static string NoteName(int pitch) => $"{Names[pitch % 12]}{(pitch / 12) - 1}";

    /// <summary>The chord the keys make, named as a player would - its root, its kind, and the bass
    /// under it when that is not the root; null when they make none of the common ones.</summary>
    internal static string? Chord(IReadOnlyList<int> keys)
    {
        if (keys.Count < 2)
            return null;
        int classes = 0;
        foreach (int key in keys)
            classes |= 1 << (key % 12);
        int bass = keys.Min() % 12;
        (int Mask, string Name)[] kinds =
        [
            (0b10010001, ""), (0b10001001, "m"), (0b01001001, "dim"), (0b100010001, "aug"),
            (0b10000101, "sus2"), (0b10100001, "sus4"), (0b10010010001, "7"), (0b100010010001, "maj7"),
            (0b10010001001, "m7"), (0b10001001001, "m7b5"), (0b1001001001, "dim7"), (0b1010010001, "6"),
            (0b1010001001, "m6"), (0b10000001, "5"),
        ];
        string? best = null;
        foreach (int root in Enumerable.Range(0, 12).OrderBy(r => r == bass ? 0 : 1))
        {
            int relative = ((classes >> root) | (classes << (12 - root))) & 0xFFF;
            foreach (var (mask, name) in kinds)
            {
                if (relative != mask)
                    continue;
                string chord = Names[root] + name;
                if (root != bass)
                    chord += "/" + Names[bass];
                if (root == bass)
                    return chord;
                best ??= chord;
            }
        }
        return best;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.PanelBackground), bounds);
        double column = ColumnWidth, roll = RollWidth;
        double line = HeaderHeight + Math.Round((bounds.Height - HeaderHeight) / 3);

        // The octaves' names over their Cs.
        for (int key = _lowKey; key <= _highKey; key++)
            if (key % 12 == 0)
                Text(context, NoteName(key), (key - _lowKey) * column, 5 * _scale, SidPlayerPalette.Subtitle);

        using (context.PushClip(new Rect(0, HeaderHeight, roll, bounds.Height - HeaderHeight)))
        {
            // The black keys' columns a shade darker, a line at each C.
            var dark = SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.Background, 150));
            var octave = SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder);
            for (int key = _lowKey; key <= _highKey; key++)
            {
                double x = (key - _lowKey) * column;
                if (Black[key % 12])
                    context.FillRectangle(dark, new Rect(x, HeaderHeight, column, bounds.Height - HeaderHeight));
                if (key % 12 == 0)
                    context.FillRectangle(octave, new Rect(x, HeaderHeight, 1, bounds.Height - HeaderHeight));
            }
            context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 60)), new Rect(0, line, roll, 1));

            // The notes from those starting a screen above the line to those starting at its foot.
            double past = (line - HeaderHeight) / PixelsPerSecond, coming = (bounds.Height - line) / PixelsPerSecond;
            var notes = _notes;
            int first = FirstStartingAfter(notes, _now - past - _longest);
            for (int i = first; i < notes.Count; i++)
            {
                var note = notes[i];
                if (note.Start > _now + coming)
                    break;
                if (note.End < _now - past || note.Pitch < _lowKey || note.Pitch > _highKey)
                    continue;
                double top = line + ((note.Start - _now) * PixelsPerSecond);
                double bottom = line + ((note.End - _now) * PixelsPerSecond);
                double x = ((note.Pitch - _lowKey) * column) + 1;
                bool now = note.Start <= _now && note.End > _now;
                int band = note.Pitch < 48 ? 2 : note.Pitch < 72 ? 1 : 0;
                // Strength in four steps of alpha, so the brushes are a handful, kept.
                int alpha = now ? 255 : 120 + (int)(Math.Round(note.Strength * 3) * 40);
                var colour = now ? SidPlayerPalette.Title : SidPlayerPalette.WithAlpha(SidPlayerPalette.VoiceColour(band), alpha);
                context.FillRectangle(SidPlayerPalette.Brush(colour), new Rect(x, top, Math.Max(2, column - 2), Math.Max(2, bottom - top)), (float)(2 * _scale));
            }
        }

        DrawPanel(context, roll + Gap, bounds.Height);
    }

    /// <summary>The first note starting at or after <paramref name="seconds"/>.</summary>
    private static int FirstStartingAfter(IReadOnlyList<HeardNote> notes, double seconds)
    {
        int low = 0, high = notes.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (notes[mid].Start < seconds)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    private void DrawPanel(DrawingContext context, double left, double height)
    {
        double y = 5 * _scale, row = 17 * _scale;
        Text(context, "NOTES", left, y, SidPlayerPalette.Title, bold: true);
        y += row;
        Text(context, _heardText, left, y, SidPlayerPalette.Subtitle);
        y += row * 1.5;
        if (_chordText.Length > 0)
        {
            Text(context, _chordText, left, y, SidPlayerPalette.VoiceColour(1), bold: true);
            y += row * 1.2;
        }
        if (_notesText.Length > 0 && y < height - row)
            Text(context, _notesText.Length > 30 ? _notesText[..30] : _notesText, left, y, SidPlayerPalette.Control);
    }

    private TextLayout Measure(string text, Color? ink = null, bool bold = false)
    {
        var key = (text, ink ?? SidPlayerPalette.Control, bold);
        if (_laidOut.TryGetValue(key, out var laidOut))
            return laidOut;
        if (_laidOut.Count >= 1024)
            Forget();
        laidOut = new TextLayout(text, bold ? Bold : Mono, FontSize, SidPlayerPalette.Brush(key.Item2));
        _laidOut[key] = laidOut;
        return laidOut;
    }

    private void Forget()
    {
        foreach (var laidOut in _laidOut.Values)
            laidOut.Dispose();
        _laidOut.Clear();
    }

    private void Text(DrawingContext context, string text, double x, double y, Color ink, bool bold = false) =>
        Measure(text, ink, bold).Draw(context, new Point(x, y));
}
