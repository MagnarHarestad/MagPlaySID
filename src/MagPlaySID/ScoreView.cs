using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MagPlay.Score;
using MagPlaySid.Player;
using Avalonia.Rendering;

namespace MagPlaySid;

/// <summary>
/// The Retro view of a score: each voice's rows - pattern row, note, instrument, effects -
/// scrolling with the music past a line where they sound, and beside them the selected voice's
/// instrument with its wave and pulse programs, and the filter program, the step sounding lit.
/// Rows are placed by when they play, so a row a note splits off its grid row is as tall as it is
/// long; where notes come so fast that rows after rows would run together, the view scrolls faster
/// through them, all voices alike, so each is written in full. A better reading of the tune while it
/// plays takes over below what is on screen, so nothing the listener can see changes. Clicking a
/// voice's rows selects it. A 2SID or 3SID tune shows every chip's voices, in narrower columns.
/// </summary>
internal sealed class ScoreView : Control, ICustomHitTest
{
    /// <summary>Under the pointer anywhere in its bounds: tested so, not against each of the
    /// lines and shapes it last drew - every move of the mouse tested them all.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>How much larger than the small size the text is - the rows, their spacing and the
    /// columns all grow with it.</summary>
    private double _scale = 1;

    private double RowHeight => 17 * _scale;
    private double FontSize => 13 * _scale;
    private double HeaderHeight => 26 * _scale;
    /// <summary>Characters a voice's column has: fewer, the more voices there are.</summary>
    private int VoiceChars => Voices switch { <= 3 => 24, <= 6 => 17, _ => 11 };
    private double Gap => 14 * _scale;

    /// <summary>Rows closer than this, in rows, cannot both be written.</summary>
    private const double TooClose = 0.75;

    private static readonly Typeface Mono = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"));
    private static readonly Typeface Bold = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"), FontStyle.Normal, FontWeight.Bold);

    private double _charWidth;

    /// <summary>Text laid out once and drawn again on every frame it shows: a row scrolling past is
    /// the same text for as long as it is on screen. Emptied for a new tune or a new size. A
    /// TextLayout, not a FormattedText: drawing one of those lays it out again every time - 11 KB a
    /// draw, measured, against a TextLayout's 4 bytes.</summary>
    private readonly Dictionary<(string Text, Color Ink, bool Bold), TextLayout> _laidOut = [];

    /// <summary>A played row's text, by its pattern, row and transpose: made once, not every frame.</summary>
    private Dictionary<(int Pattern, int Row, int Transpose), string> _rowTexts = [];

    /// <summary>The score whose rows are still on screen above <see cref="_seam"/>, with its
    /// timeline and row texts, until they have scrolled away; null when there is none.</summary>
    private MagPlay.Score.Score? _before;
    private ScoreTimeline? _beforeTimeline;
    private Dictionary<(int Pattern, int Row, int Transpose), string> _beforeTexts = [];

    /// <summary>The call from which the rows are the score's own; before it, <see cref="_before"/>'s.</summary>
    private int _seam;

    /// <summary>Each voice's rows as drawn: <see cref="_firstOwn"/> of the score before, then its own.</summary>
    private IReadOnlyList<PlayedRow>[] _rows = [];
    private int[] _firstOwn = [];

    private MagPlay.Score.Score? _score;
    private ScoreTimeline? _timeline;
    private double _call;
    private int _selected;

    /// <summary>How far down the rows each call of the tune lies, counted in rows.</summary>
    private double[] _placed = [0];

    /// <summary>A digi's samples as played, each with its number - S01 the first heard - and the
    /// call it starts on in the timeline shown, worked out again when that changes.</summary>
    private IReadOnlyList<MagPlay.C64.DataRun> _samples = [];
    private int[] _sampleNumbers = [];
    private int[] _sampleCalls = [];
    private ScoreTimeline? _sampleCallsFor;

    /// <summary>Each sample's name, S01 on, and its line in the panel: made once, not every frame.</summary>
    private string[] _sampleNames = [];
    private string[] _sampleLines = [];

    /// <summary>The column of the digi's samples, after the voices': there when the tune plays one.</summary>
    private bool HasDigi => _samples.Count > 0;

    private double DigiWidth => 7 * _charWidth;

    /// <summary>The width the rows and the panel take.</summary>
    public double ContentWidth => TrackerWidth + Gap + PanelWidth;

    private double VoiceWidth => VoiceChars * _charWidth;

    private int Voices => _score?.Voices.Length ?? 3;

    private double TrackerWidth => Voices * VoiceWidth + (Voices - 1) * Gap + (HasDigi ? Gap + DigiWidth : 0);

    private double DigiLeft => Voices * (VoiceWidth + Gap);

    private double PanelWidth => 30 * _charWidth;

    public ScoreView() => _charWidth = Measure("0").WidthIncludingTrailingWhitespace;

    /// <summary>1 for the small text, more for bigger: the view is as wide again as it needs.</summary>
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

    public void SetScore(MagPlay.Score.Score? score, ScoreTimeline? timeline)
    {
        _score = score;
        _timeline = timeline;
        Forget();
        _rowTexts.Clear();
        _before = null;
        _beforeTimeline = null;
        _beforeTexts.Clear();
        _rows = timeline?.Rows ?? [];
        _firstOwn = new int[_rows.Length];
        _placed = score is not null && timeline is not null ? Placed(score, timeline) : [0];
        _call = 0;
        ShowSamples([]);
        _selected = Math.Min(_selected, Voices - 1);
        Width = ContentWidth;
        InvalidateVisual();
    }

    /// <summary>The samples a digi plays, shown in a column of their own beside the voices: the
    /// row each starts on named by its number.</summary>
    public void ShowSamples(IReadOnlyList<MagPlay.C64.DataRun> samples)
    {
        _samples = samples;
        var numbers = new Dictionary<int, int>();
        var starts = new List<int>();
        var bytes = new List<int>();
        _sampleNumbers = new int[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            if (!numbers.TryGetValue(samples[i].Start, out int number))
            {
                numbers[samples[i].Start] = number = numbers.Count + 1;
                starts.Add(samples[i].Start);
                bytes.Add(0);
            }
            _sampleNumbers[i] = number;
            bytes[number - 1] = Math.Max(bytes[number - 1], samples[i].Address - samples[i].Start + 1);
        }
        // Index 0 unused: a sample's number is its place.
        _sampleNames = ["", .. starts.Select((_, n) => $"S{n + 1:D2}")];
        _sampleLines = [.. starts.Select((start, n) => $"S{n + 1:D2}  ${start:X4}  {bytes[n],5} bytes")];
        _sampleCallsFor = null;
        if (!HasDigi && _selected >= Voices)
            _selected = Voices - 1;
        Width = ContentWidth;
        InvalidateVisual();
    }

    /// <summary>The call each sample starts on, by the timeline shown.</summary>
    private int[] SampleCalls(ScoreTimeline timeline)
    {
        if (_sampleCallsFor != timeline)
        {
            _sampleCalls = [.. _samples.Select(s => Math.Max(0, timeline.CallAt(s.Cycle)))];
            _sampleCallsFor = timeline;
        }
        return _sampleCalls;
    }

    /// <summary>
    /// A better reading of the same tune, taking over where it cannot be seen: what is on screen
    /// stays as it is and scrolls away, and the rows coming up from below are <paramref name="score"/>'s,
    /// from the first of its rows past the view's bottom.
    /// </summary>
    public void ContinueWith(MagPlay.Score.Score score, ScoreTimeline timeline)
    {
        if (_score is not { } was || _timeline is not { } wasTimeline || was.Voices.Length != score.Voices.Length)
        {
            double at = _call;
            SetScore(score, timeline);
            _call = at;
            return;
        }

        // The first call past the bottom of the view, and the new score's first row from there.
        double now = PlacedAt(_call);
        double last = _placed.Length - 1;
        int below = Math.Max(0, (int)Math.Floor(_call));
        double reach = (Bounds.Height - LineAt(Bounds.Height) + RowHeight) / RowHeight;
        while (below < last && _placed[below] - now < reach)
            below++;
        int seam = Math.Max(0, timeline.CallAt(wasTimeline.CycleOf(below)));
        if (timeline.Rows.FirstOrDefault(r => r.Count > 0) is { } grid)
        {
            int row = 0;
            while (row < grid.Count && grid[row].Call < seam)
                row++;
            seam = row < grid.Count ? grid[row].Call : timeline.Calls;
        }

        // Calls by the new timeline, should its calls fall otherwise: the old rows at their own times.
        int Same(int call) => Math.Max(0, timeline.CallAt(wasTimeline.CycleOf(call)));
        var rows = new IReadOnlyList<PlayedRow>[timeline.Rows.Length];
        var firstOwn = new int[rows.Length];
        for (int v = 0; v < rows.Length; v++)
        {
            var kept = _rows[v].Select(r => r with { Call = Same(r.Call) }).Where(r => r.Call < seam).ToList();
            firstOwn[v] = kept.Count;
            kept.AddRange(timeline.Rows[v].Where(r => r.Call >= seam));
            rows[v] = kept;
        }

        // Where the calls lie: as they did up to the seam, then as the new score has them.
        var own = Placed(score, timeline);
        var placed = new double[own.Length];
        int upTo = Math.Min(seam, own.Length - 1);
        for (int call = 0; call <= upTo; call++)
            placed[call] = _placed[Math.Clamp(wasTimeline.CallAt(timeline.CycleOf(call)), 0, _placed.Length - 1)];
        for (int call = upTo + 1; call < own.Length; call++)
            placed[call] = placed[upTo] + own[call] - own[upTo];

        double into = _call - Math.Floor(_call);
        int nowCall = Same((int)Math.Floor(_call));
        (_beforeTexts, _rowTexts) = (_rowTexts, []);
        (_before, _beforeTimeline, _seam) = (was, wasTimeline, seam);
        (_score, _timeline, _rows, _firstOwn, _placed) = (score, timeline, rows, firstOwn, placed);
        _call = nowCall + into;
        Width = ContentWidth;
        InvalidateVisual();
    }

    /// <summary>The score before let go, once its rows have left the view or the tune has gone back to them.</summary>
    private void LetGoOfBefore(double goingTo)
    {
        if (_before is null || _score is not { } score || _timeline is not { } timeline)
            return;
        bool back = goingTo < _call;
        double seamAt = (PlacedAt(_seam) - PlacedAt(goingTo)) * RowHeight + LineAt(Bounds.Height);
        if (!back && seamAt > HeaderHeight - RowHeight)
            return;

        _before = null;
        _beforeTimeline = null;
        _beforeTexts.Clear();
        _rows = timeline.Rows;
        _firstOwn = new int[_rows.Length];
        _placed = Placed(score, timeline);
    }

    /// <summary>The line where rows sound, in a view <paramref name="height"/> high.</summary>
    private double LineAt(double height) => HeaderHeight + Math.Round((height - HeaderHeight) / 3);

    /// <summary>The view at <paramref name="cycle"/> of the tune: between two calls, as far from
    /// the one as the cycle is, so the rows scroll smoothly.</summary>
    public void SetCycle(long cycle)
    {
        if (_timeline is not { } timeline)
            return;

        int call = timeline.CallAt(cycle);
        double into = 0;
        if (call >= 0 && call + 1 < timeline.Calls)
        {
            long from = timeline.CycleOf(call), to = timeline.CycleOf(call + 1);
            into = to > from ? Math.Clamp((cycle - from) / (double)(to - from), 0, 1) : 0;
        }
        // Drawn again only when the rows move: paused, they stand still.
        double now = call + into;
        if (now == _call)
            return;
        LetGoOfBefore(now);
        _call = now;
        InvalidateVisual();
    }

    /// <summary>Where each call lies, in rows: as far as its share of a row, and further where a
    /// voice has rows too close to write, one after another. One row split off its grid row is left
    /// as it is: stretched, it would jolt the scrolling for one note.</summary>
    private static double[] Placed(MagPlay.Score.Score score, ScoreTimeline timeline)
    {
        double plain = 1.0 / (score.FunkTempo is { } funk ? (score.Tempo + funk) / 2.0 : Math.Max(1, score.Tempo));
        var share = new double[timeline.Calls];
        Array.Fill(share, plain);
        foreach (var rows in timeline.Rows)
        {
            bool Close(int row) => row >= 0 && row + 1 < rows.Count && (rows[row + 1].Call - rows[row].Call) * plain < TooClose;
            for (int row = 0; row + 1 < rows.Count; row++)
            {
                if (!Close(row) || !(Close(row - 1) || Close(row + 1)))
                    continue;
                int from = rows[row].Call, to = Math.Min(rows[row + 1].Call, share.Length);
                for (int call = from; call < to; call++)
                    share[call] = Math.Max(share[call], 1.0 / (to - from));
            }
        }

        var placed = new double[share.Length + 1];
        for (int call = 0; call < share.Length; call++)
            placed[call + 1] = placed[call] + share[call];
        return placed;
    }

    /// <summary>Where <paramref name="call"/> lies, in rows, between two calls too; before the
    /// first and past the last at the rate next to them.</summary>
    private double PlacedAt(double call)
    {
        int last = _placed.Length - 1;
        if (last < 1)
            return 0;
        int whole = Math.Clamp((int)Math.Floor(call), 0, last - 1);
        return _placed[whole] + (call - whole) * (_placed[whole + 1] - _placed[whole]);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        double x = e.GetPosition(this).X;
        for (int v = 0; v < Voices; v++)
        {
            double left = v * (VoiceWidth + Gap);
            if (x >= left && x < left + VoiceWidth)
            {
                _selected = v;
                InvalidateVisual();
            }
        }
        if (HasDigi && x >= DigiLeft && x < DigiLeft + DigiWidth)
        {
            _selected = Voices;
            InvalidateVisual();
        }
        base.OnPointerPressed(e);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.PanelBackground), bounds);
        if (_score is not { } score || _timeline is not { } timeline)
            return;

        double line = LineAt(bounds.Height);

        using (context.PushClip(new Rect(0, HeaderHeight, TrackerWidth, bounds.Height - HeaderHeight)))
        {
            context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 18)),
                                  new Rect(0, line, TrackerWidth, RowHeight));
            for (int v = 0; v < Voices; v++)
                DrawVoice(context, score, timeline, v, v * (VoiceWidth + Gap), line, bounds.Height);
            if (HasDigi)
                DrawDigi(context, timeline, line, bounds.Height);
        }

        for (int v = 0; v < Voices; v++)
        {
            double left = v * (VoiceWidth + Gap);
            var colour = SidPlayerPalette.VoiceColour(v % 3);
            Text(context, Name(v), left + 4, 5 * _scale, v == _selected ? colour : SidPlayerPalette.WithAlpha(colour, 140), bold: true);
            if (v == _selected)
                context.FillRectangle(SidPlayerPalette.Brush(colour), new Rect(left, HeaderHeight - 3, VoiceWidth, 2));
        }
        if (HasDigi)
        {
            bool chosen = _selected == Voices;
            Text(context, "DIGI", DigiLeft + 4, 5 * _scale, chosen ? SidPlayerPalette.Digi : SidPlayerPalette.WithAlpha(SidPlayerPalette.Digi, 140), bold: true);
            if (chosen)
                context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.Digi), new Rect(DigiLeft, HeaderHeight - 3, DigiWidth, 2));
        }

        if (_selected >= Voices)
            DrawSamplePanel(context, timeline, TrackerWidth + Gap);
        else
            DrawPanel(context, score, timeline, TrackerWidth + Gap, bounds.Height);
    }

    // ------------------------------------------------------------------ the digi

    /// <summary>The digi's column: on the rows of the voice with the most, each sample by its number
    /// on the row it starts in, and the rows between dotted as a tracker's empty rows are.</summary>
    private void DrawDigi(DrawingContext context, ScoreTimeline timeline, double line, double height)
    {
        var rows = _rows.MaxBy(r => r.Count);
        if (rows is not { Count: > 0 })
            return;

        var calls = SampleCalls(timeline);
        int now = Math.Max(0, RowAt(rows, (int)Math.Floor(_call)));
        double at = PlacedAt(_call);
        double Y(int i) => line + (PlacedAt(rows[i].Call) - at) * RowHeight;
        int first = now;
        while (first > 0 && Y(first - 1) > HeaderHeight - RowHeight)
            first--;

        // The first sample starting on or after the first row drawn, and on from there.
        int sample = 0;
        while (sample < calls.Length && calls[sample] < rows[first].Call)
            sample++;
        double last = double.NegativeInfinity;
        for (int i = first; i < rows.Count && Y(i) < height; i++)
        {
            int end = i + 1 < rows.Count ? rows[i + 1].Call : int.MaxValue;
            int starts = -1;
            while (sample < calls.Length && calls[sample] < end)
            {
                if (starts < 0)
                    starts = sample;
                sample++;
            }

            double y = Y(i);
            if (y - last < RowHeight * TooClose)
                continue;
            last = y;
            bool sounding = i == now;
            if (starts >= 0)
                Text(context, _sampleNames[_sampleNumbers[starts]], DigiLeft + 4, y + 1, sounding ? SidPlayerPalette.Title : SidPlayerPalette.Digi, bold: sounding);
            else
                Text(context, "...", DigiLeft + 4, y + 1, sounding ? SidPlayerPalette.Title : SidPlayerPalette.WithAlpha(SidPlayerPalette.Subtitle, 110), bold: sounding);
        }
    }

    /// <summary>The digi's samples listed, the one playing lit: each where it is in the tune's
    /// memory and how many bytes it is, at the longest it was played.</summary>
    private void DrawSamplePanel(DrawingContext context, ScoreTimeline timeline, double left)
    {
        double y = 5 * _scale;
        Text(context, "DIGI", left, y, SidPlayerPalette.Digi, bold: true);
        y += RowHeight;
        Text(context, "Samples through the volume", left, y, SidPlayerPalette.Subtitle);
        y += RowHeight * 1.5;

        // The last sample started by now, if it is still being read.
        long cycle = timeline.CycleOf((int)Math.Floor(_call));
        int low = 0, high = _samples.Count - 1, latest = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (_samples[middle].Cycle <= cycle)
            {
                latest = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        int playing = latest >= 0 && cycle <= _samples[latest].LastCycle ? _sampleNumbers[latest] : -1;

        for (int n = 0; n < _sampleLines.Length && y < Bounds.Height - RowHeight; n++)
        {
            bool lit = playing == n + 1;
            if (lit)
                context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.Digi, 60)), new Rect(left - 4, y, PanelWidth, RowHeight));
            Text(context, _sampleLines[n], left, y + 1, lit ? SidPlayerPalette.Title : SidPlayerPalette.Control, bold: lit);
            y += RowHeight;
        }
    }

    // ------------------------------------------------------------------ the rows

    private void DrawVoice(DrawingContext context, MagPlay.Score.Score score, ScoreTimeline timeline, int v,
                           double left, double line, double height)
    {
        var rows = _rows[v];
        if (rows.Count == 0)
            return;

        var colour = SidPlayerPalette.VoiceColour(v % 3);
        int now = Math.Max(0, RowAt(rows, (int)Math.Floor(_call)));
        using var clip = context.PushClip(new Rect(left - 6, HeaderHeight, VoiceWidth + 6, height - HeaderHeight));
        double at = PlacedAt(_call);
        double Y(int i) => line + (PlacedAt(rows[i].Call) - at) * RowHeight;

        // From the row sounding up and down, as far as the view reaches; a row too close to the one
        // drawn before it - split off its grid row - is marked, not written over it.
        int first = now;
        while (first > 0 && Y(first - 1) > HeaderHeight - RowHeight)
            first--;
        double last = double.NegativeInfinity;
        for (int i = first; i < rows.Count && Y(i) < height; i++)
        {
            double y = Y(i);
            var played = rows[i];
            // Too close to the row before to be written, it is marked - and gets no pattern's line,
            // which would cross that row's text: Pirates Blade's first P01.
            if (y - last < RowHeight * TooClose)
            {
                context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(colour, 120)), new Rect(left - 5, y, 3, 2));
                continue;
            }
            last = y;
            if (played.Row == 0 && y > HeaderHeight)
            {
                var pen = SidPlayerPalette.Pen(SidPlayerPalette.WithAlpha(colour, 70));
                context.DrawLine(pen, new Point(left, y - 1), new Point(left + VoiceWidth, y - 1));
            }

            bool before = i < _firstOwn[v];
            var row = (before ? _before! : score).PatternNumber(played.Pattern)?.Rows.ElementAtOrDefault(played.Row) ?? Row.Hold;
            var texts = before ? _beforeTexts : _rowTexts;
            bool sounding = i == now;
            bool empty = row.Note.Kind == RowNoteKind.Hold && row.Instrument == 0 && row.Effects.Count == 0;
            var ink = sounding ? SidPlayerPalette.Title
                : empty ? SidPlayerPalette.WithAlpha(SidPlayerPalette.Subtitle, 110)
                : SidPlayerPalette.WithAlpha(colour, 220);
            if (!texts.TryGetValue((played.Pattern, played.Row, played.Transpose), out var text))
                texts[(played.Pattern, played.Row, played.Transpose)] = text = RowText(row, played);
            Text(context, text, left + 4, y + 1, ink, bold: sounding);
            if (played.Row == 0)
            {
                // At the column's end, and only where the row's own text leaves it room.
                string entry = EntryText(played);
                if (text.Length + entry.Length < VoiceChars)
                    Text(context, entry, left + VoiceWidth - entry.Length * _charWidth, y + 1, SidPlayerPalette.WithAlpha(colour, 150));
            }
        }
    }

    /// <summary>The row of <paramref name="rows"/> playing on <paramref name="call"/>; -1 before the first.</summary>
    private static int RowAt(IReadOnlyList<PlayedRow> rows, int call)
    {
        int low = 0, high = rows.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (rows[middle].Call <= call)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found;
    }

    /// <summary>A row as a tracker writes it: its number in the pattern, the note, the instrument,
    /// the effects.</summary>
    private static string RowText(Row row, PlayedRow played)
    {
        string note = row.Note.Kind switch
        {
            RowNoteKind.Note => NoteTables.Name(row.Note.Note + played.Transpose).PadRight(3),
            RowNoteKind.GateOff => "===",
            RowNoteKind.GateOn => "+++",
            _ => "...",
        };
        string instrument = row.Instrument > 0 ? row.Instrument.ToString("X2") : "..";
        string effects = string.Join(" ", row.Effects.Select(e => e.Label));
        return $"{played.Row:X2} {note} {instrument} {effects}";
    }

    /// <summary>A voice by its place: VOICE 1-3 on one chip; with more, which SID it is on too.</summary>
    private string Name(int voice) => Voices <= 3 ? $"VOICE {voice + 1}" : $"SID{voice / 3 + 1} V{voice % 3 + 1}";

    private static string EntryText(PlayedRow played) =>
        played.Transpose == 0 ? $"P{played.Pattern:X2}" : $"P{played.Pattern:X2}{(played.Transpose > 0 ? "+" : "-")}{Math.Abs(played.Transpose)}";

    // ------------------------------------------------------------------ the instrument and programs

    private void DrawPanel(DrawingContext context, MagPlay.Score.Score score, ScoreTimeline timeline, double left, double height)
    {
        int call = Math.Max(0, (int)Math.Floor(_call));
        // The instruments the rows sounding name: the score before's until its rows have played.
        if (_before is { } before && _beforeTimeline is { } beforeTimeline && call < _seam)
            (score, timeline, call) = (before, beforeTimeline, Math.Max(0, beforeTimeline.CallAt(timeline.CycleOf(call))));
        var position = timeline.PositionOf(_selected, call);
        var colour = SidPlayerPalette.VoiceColour(_selected % 3);
        var dim = SidPlayerPalette.Subtitle;
        double y = 5 * _scale;

        void Line(string text, Color ink, bool bold = false)
        {
            Text(context, text, left, y, ink, bold);
            y += RowHeight;
        }

        if (score.InstrumentNumber(position.Instrument) is not { } instrument)
        {
            Line(Name(_selected), colour, bold: true);
            Line("No instrument yet", dim);
        }
        else
        {
            Line($"INSTRUMENT {instrument.Number:X2}", colour, bold: true);
            y += 4;
            Line($"AD {instrument.AttackDecay:X2}  SR {instrument.SustainRelease:X2}" +
                 (instrument.FirstControl is { } first ? $"  first {first:X2}" : ""), SidPlayerPalette.Control);
            Line(instrument.HardRestart switch
            {
                HardRestartKind.Envelope when instrument.GateTimer > 0 => $"hard restart {instrument.HardRestartEnvelope:X4}, {instrument.GateTimer} before",
                HardRestartKind.GateOff when instrument.GateTimer > 0 => $"gate off {instrument.GateTimer} before",
                _ => "no hard restart",
            }, dim);

            double programs = height - y - 7 * RowHeight;
            int lines = Math.Max(3, (int)(programs / RowHeight / 2) - 1);
            y += 6;
            Line("WAVE", colour, bold: true);
            Program(context, score.WaveTable, instrument.WaveProgram, position.WaveStep, left, ref y, lines);
            y += 6;
            Line("PULSE", colour, bold: true);
            int pulse = instrument.PulseProgram >= 0 ? instrument.PulseProgram : Start(score.PulseTable, position.PulseStep);
            Program(context, score.PulseTable, pulse, position.PulseStep, left, ref y, lines);
        }

        y = Math.Max(y + 6, height - 6 * RowHeight);
        int chip = Math.Min(_selected / 3, score.Chips - 1);
        Line(score.Chips > 1 ? $"FILTER, SID{chip + 1}" : "FILTER", SidPlayerPalette.Title, bold: true);
        int filterStep = timeline.FilterStepAt(call, chip);
        int filterProgram = score.FilterPrograms[chip];
        Program(context, score.FilterTable, filterProgram >= 0 ? filterProgram : Start(score.FilterTable, filterStep),
                filterStep, left, ref y, 4);
    }

    /// <summary>Where the program a step is in starts: the step after the end before it.</summary>
    private static int Start(List<TableStep> table, int step)
    {
        if (step < 0)
            return -1;
        int start = step;
        while (start > 0 && table[start - 1] is not JumpStep)
            start--;
        return start;
    }

    /// <summary>A program's steps, from <paramref name="start"/> to its end - a window of them
    /// around the one sounding when there are more than <paramref name="lines"/>.</summary>
    private void Program(DrawingContext context, List<TableStep> table, int start, int sounding, double left, ref double y, int lines)
    {
        if (start < 0 || start >= table.Count)
        {
            Text(context, "  none", left, y, SidPlayerPalette.Subtitle);
            y += RowHeight;
            return;
        }

        int end = start;
        while (end < table.Count - 1 && table[end] is not JumpStep)
            end++;
        int from = start;
        if (end - start + 1 > lines && sounding >= start && sounding <= end)
            from = Math.Clamp(sounding - lines / 3, start, end - lines + 1);

        for (int step = from; step <= end && step < from + lines; step++)
        {
            bool lit = step == sounding;
            if (lit)
                context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.VoiceColour(_selected % 3), 60)),
                                      new Rect(left - 3, y - 1, PanelWidth - 4, RowHeight));
            Text(context, $"{step - start:X2} {Describe(table[step])}", left, y,
                 lit ? SidPlayerPalette.Title : SidPlayerPalette.Control, bold: lit);
            y += RowHeight;
        }
    }

    private static string Describe(TableStep step) => step switch
    {
        WaveStep w => $"{(w.Control is { } c ? c.ToString("X2") : "..")} {Pitch(w.Pitch)}{Calls(w.Calls)}",
        PulseStep p => $"{(p.Width is { } width ? $"={width:X3}" : p.Add == 0 ? "hold" : "    ")} {Add(p.Add)}{Calls(p.Calls)}",
        FilterStep f => $"{(f.Cutoff is { } cut ? $"={cut:X3}" : "    ")} {Add(f.Add)}" +
                        $"{(f.Control is { } fc ? $" res {fc:X2}" : "")}{(f.Mode is { } m ? $" mode {m >> 4:X}" : "")}{Calls(f.Calls)}",
        EffectStep e => e.Effect.Label,
        JumpStep { To: < 0 } => "end",
        JumpStep j => $"loop to {j.To:X2}",
        RawStep r => $"raw, {r.Writes.Count} writes{Calls(r.Calls)}",
        _ => "",
    };

    private static string Pitch(Pitch? pitch) => pitch switch
    {
        null => "   ",
        { Kind: PitchKind.Relative, Value: var semitones } => semitones >= 0 ? $"+{semitones}" : $"{semitones}",
        { Kind: PitchKind.Absolute, Value: var note } => NoteTables.Name(note),
        { Kind: PitchKind.Offset, Value: var offset } => offset >= 0 ? $"f+{offset:X}" : $"f-{-offset:X}",
        { Value: var frequency } => $"f={frequency:X4}",
    };

    private static string Add(int add) => add == 0 ? "" : add > 0 ? $"+{add:X}" : $"-{-add:X}";

    private static string Calls(int calls) => calls switch
    {
        1 => "",
        >= int.MaxValue / 4 => "  on",
        _ => $"  x{calls}",
    };

    // ------------------------------------------------------------------ text

    private TextLayout Measure(string text, Color? ink = null, bool bold = false)
    {
        var key = (text, ink ?? SidPlayerPalette.Control, bold);
        if (_laidOut.TryGetValue(key, out var laidOut))
            return laidOut;
        // A bound on what is kept: a long tune's rows, not every row of every tune heard.
        if (_laidOut.Count >= 4096)
            Forget();
        laidOut = new TextLayout(text, bold ? Bold : Mono, FontSize, SidPlayerPalette.Brush(key.Item2));
        _laidOut[key] = laidOut;
        return laidOut;
    }

    /// <summary>The text laid out so far let go: a new tune's rows, or a new size.</summary>
    private void Forget()
    {
        foreach (var laidOut in _laidOut.Values)
            laidOut.Dispose();
        _laidOut.Clear();
    }

    private void Text(DrawingContext context, string text, double x, double y, Color ink, bool bold = false) =>
        Measure(text, ink, bold).Draw(context, new Point(x, y));
}
