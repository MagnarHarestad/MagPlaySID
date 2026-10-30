using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MagPlay.Score;
using MagPlaySid.Player;

namespace MagPlaySid;

/// <summary>
/// The Retro view of a score: each voice's rows - pattern row, note, instrument, effects -
/// scrolling with the music past a line where they sound, and beside them the selected voice's
/// instrument with its wave and pulse programs, and the filter program, the step sounding lit.
/// Rows are placed by when they play, so a row a note splits off its grid row is as tall as it is
/// long. Clicking a voice's rows selects it. A 2SID or 3SID tune shows every chip's voices, in
/// narrower columns.
/// </summary>
internal sealed class ScoreView : Control
{
    /// <summary>How much larger than the small size the text is - the rows, their spacing and the
    /// columns all grow with it.</summary>
    private double _scale = 1;

    private double RowHeight => 17 * _scale;
    private double FontSize => 13 * _scale;
    private double HeaderHeight => 26 * _scale;
    /// <summary>Characters a voice's column has: fewer, the more voices there are.</summary>
    private int VoiceChars => Voices switch { <= 3 => 24, <= 6 => 17, _ => 11 };
    private double Gap => 14 * _scale;

    private static readonly Typeface Mono = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"));
    private static readonly Typeface Bold = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"), FontStyle.Normal, FontWeight.Bold);

    private double _charWidth;

    /// <summary>Text laid out once and drawn again on every frame it shows: a row scrolling past is
    /// the same text for as long as it is on screen. Emptied for a new tune or a new size. A
    /// TextLayout, not a FormattedText: drawing one of those lays it out again every time - 11 KB a
    /// draw, measured, against a TextLayout's 4 bytes.</summary>
    private readonly Dictionary<(string Text, Color Ink, bool Bold), TextLayout> _laidOut = [];

    /// <summary>A played row's text, by its pattern, row and transpose: made once, not every frame.</summary>
    private readonly Dictionary<(int Pattern, int Row, int Transpose), string> _rowTexts = [];

    private MagPlay.Score.Score? _score;
    private ScoreTimeline? _timeline;
    private double _call;
    private int _selected;

    /// <summary>The width the rows and the panel take.</summary>
    public double ContentWidth => TrackerWidth + Gap + PanelWidth;

    private double VoiceWidth => VoiceChars * _charWidth;

    private int Voices => _score?.Voices.Length ?? 3;

    private double TrackerWidth => Voices * VoiceWidth + (Voices - 1) * Gap;

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
        _call = 0;
        _selected = Math.Min(_selected, Voices - 1);
        Width = ContentWidth;
        InvalidateVisual();
    }

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
        _call = now;
        InvalidateVisual();
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
        base.OnPointerPressed(e);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.PanelBackground), bounds);
        if (_score is not { } score || _timeline is not { } timeline)
            return;

        double line = HeaderHeight + Math.Round((bounds.Height - HeaderHeight) / 3);
        double perCall = RowHeight / (score.FunkTempo is { } funk ? (score.Tempo + funk) / 2.0 : Math.Max(1, score.Tempo));

        using (context.PushClip(new Rect(0, HeaderHeight, TrackerWidth, bounds.Height - HeaderHeight)))
        {
            context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 18)),
                                  new Rect(0, line, TrackerWidth, RowHeight));
            for (int v = 0; v < Voices; v++)
                DrawVoice(context, score, timeline, v, v * (VoiceWidth + Gap), line, perCall, bounds.Height);
        }

        for (int v = 0; v < Voices; v++)
        {
            double left = v * (VoiceWidth + Gap);
            var colour = SidPlayerPalette.VoiceColour(v % 3);
            Text(context, Name(v), left + 4, 5 * _scale, v == _selected ? colour : SidPlayerPalette.WithAlpha(colour, 140), bold: true);
            if (v == _selected)
                context.FillRectangle(SidPlayerPalette.Brush(colour), new Rect(left, HeaderHeight - 3, VoiceWidth, 2));
        }

        DrawPanel(context, score, timeline, TrackerWidth + Gap, bounds.Height);
    }

    // ------------------------------------------------------------------ the rows

    private void DrawVoice(DrawingContext context, MagPlay.Score.Score score, ScoreTimeline timeline, int v,
                           double left, double line, double perCall, double height)
    {
        var rows = timeline.Rows[v];
        if (rows.Count == 0)
            return;

        var colour = SidPlayerPalette.VoiceColour(v % 3);
        int now = Math.Max(0, timeline.RowAt(v, (int)Math.Floor(_call)));
        using var clip = context.PushClip(new Rect(left - 6, HeaderHeight, VoiceWidth + 6, height - HeaderHeight));
        double Y(int i) => line + (rows[i].Call - _call) * perCall;

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
            if (played.Row == 0 && y > HeaderHeight)
            {
                var pen = SidPlayerPalette.Pen(SidPlayerPalette.WithAlpha(colour, 70));
                context.DrawLine(pen, new Point(left, y - 1), new Point(left + VoiceWidth, y - 1));
            }

            if (y - last < RowHeight * 0.75)
            {
                context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(colour, 120)), new Rect(left - 5, y, 3, 2));
                continue;
            }
            last = y;

            var row = score.PatternNumber(played.Pattern)?.Rows.ElementAtOrDefault(played.Row) ?? Row.Hold;
            bool sounding = i == now;
            bool empty = row.Note.Kind == RowNoteKind.Hold && row.Instrument == 0 && row.Effects.Count == 0;
            var ink = sounding ? SidPlayerPalette.Title
                : empty ? SidPlayerPalette.WithAlpha(SidPlayerPalette.Subtitle, 110)
                : SidPlayerPalette.WithAlpha(colour, 220);
            if (!_rowTexts.TryGetValue((played.Pattern, played.Row, played.Transpose), out var text))
                _rowTexts[(played.Pattern, played.Row, played.Transpose)] = text = RowText(row, played);
            Text(context, text, left + 4, y + 1, ink, bold: sounding);
            if (played.Row == 0)
                Text(context, EntryText(played), left + VoiceWidth - 7 * _charWidth, y + 1, SidPlayerPalette.WithAlpha(colour, 150));
        }
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
