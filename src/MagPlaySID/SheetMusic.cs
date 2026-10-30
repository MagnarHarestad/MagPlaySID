using System.Globalization;
using SkiaSharp;

namespace MagPlaySid;

/// <summary>
/// A <see cref="NoteSheet"/> engraved as sheet music - A4 pages, a staff for each voice, two bars
/// to a line - and written as a PDF. Each voice takes the clef its range sits best in, an octave
/// sign when it lies far above or below; notes are split at the bar lines and into the values a
/// beat allows, tied together; sharps and naturals hold for the bar. Drawn with Skia, which the
/// window already draws with: the noteheads, stems, flags, accidentals and rests are its own
/// shapes, the clefs a music font's when the system has one.
/// </summary>
internal static class SheetMusic
{
    // A4 in points, and the staff's measures: all else follows from the space between two lines.
    private const float PageWidth = 595, PageHeight = 842, Margin = 42;
    private const float Space = 6;
    private const float StaffHeight = Space * 4;
    private const float StaffGap = 32;
    private const float SystemGap = 22;
    private const float SystemTop = 16;
    private const float SystemBottom = 10;
    private const float TitleBlock = 74;
    private const float Footer = 18;
    private const float FirstLead = 58;
    private const float Lead = 40;
    private const int BarsPerSystem = 2;

    private static readonly SKColor Ink = new(0x1A, 0x1A, 0x1A);
    private static readonly SKColor Faint = new(0x70, 0x70, 0x78);

    /// <summary>Each value a note or rest can take, in steps, longest first; what it may start on.
    /// Loose for a tracker's music, full of notes off the beat: an eighth may start anywhere and a
    /// quarter on any eighth, so a tie is only where a note truly runs on.</summary>
    private static readonly (int Steps, Func<int, bool> StartsAt)[] Values =
    [
        (16, at => at == 0),
        (12, at => at is 0 or 4),
        (8, at => at % 4 == 0),
        (6, at => at % 2 == 0),
        (4, at => at % 2 == 0),
        (3, _ => true),
        (2, _ => true),
        (1, _ => true),
    ];

    private sealed record Clef(bool Treble, int Shift, string? Mark);

    private readonly record struct Piece(int At, int Steps, int? Pitch, bool TiedFromBefore, bool TiedOn);

    public static void WritePdf(NoteSheet sheet, Stream stream, string product)
    {
        var metadata = new SKDocumentPdfMetadata
        {
            Title = sheet.Title,
            Author = sheet.Author,
            Creator = product,
            Producer = product,
        };
        using var document = SKDocument.CreatePdf(stream, metadata);
        int pages = PageCount(sheet);
        for (int page = 0; page < pages; page++)
        {
            var canvas = document.BeginPage(PageWidth, PageHeight);
            DrawPage(sheet, page, canvas, product);
            document.EndPage();
        }
        document.Close();
    }

    public static int PageCount(NoteSheet sheet) => Paginate(sheet).Count;

    /// <summary>The systems each page holds: the first page has the title above them.</summary>
    private static List<(int First, int Count)> Paginate(NoteSheet sheet)
    {
        int systems = Math.Max(1, (sheet.Bars + BarsPerSystem - 1) / BarsPerSystem);
        float height = SystemHeight(sheet) + SystemGap;
        var pages = new List<(int, int)>();
        int at = 0;
        while (at < systems)
        {
            float room = PageHeight - 2 * Margin - Footer - (pages.Count == 0 ? TitleBlock : 0) + SystemGap;
            int count = Math.Clamp((int)(room / height), 1, systems - at);
            pages.Add((at, count));
            at += count;
        }
        return pages;
    }

    private static float SystemHeight(NoteSheet sheet) =>
        SystemTop + sheet.Voices * StaffHeight + (sheet.Voices - 1) * StaffGap + SystemBottom;

    public static void DrawPage(NoteSheet sheet, int page, SKCanvas canvas, string product)
    {
        canvas.Clear(SKColors.White);
        var (first, count) = Paginate(sheet)[page];
        var pieces = Enumerable.Range(0, sheet.Voices).Select(v => PiecesOf(sheet.Notes[v], sheet.Steps)).ToArray();
        using var text = new Text();

        float y = Margin;
        if (page == 0)
        {
            text.Centre(canvas, sheet.Title, PageWidth / 2, y + 18, 19, bold: true);
            var about = new[] { sheet.Author, sheet.Released }.Where(s => s.Length > 0).ToList();
            if (sheet.Songs > 1)
                about.Add($"song {sheet.Song} of {sheet.Songs}");
            text.Centre(canvas, string.Join("  ·  ", about), PageWidth / 2, y + 38, 10, colour: Faint);
            y += TitleBlock;
        }

        for (int system = first; system < first + count; system++)
        {
            DrawSystem(canvas, text, sheet, pieces, system, y);
            y += SystemHeight(sheet) + SystemGap;
        }

        text.Draw(canvas, $"Transcribed by {product} from the tune's own playback", Margin, PageHeight - Margin + 6, 7.5f, colour: Faint);
        string number = $"page {page + 1} of {PageCount(sheet)}";
        text.Draw(canvas, number, PageWidth - Margin - text.Width(number, 7.5f), PageHeight - Margin + 6, 7.5f, colour: Faint);
    }

    private static void DrawSystem(SKCanvas canvas, Text text, NoteSheet sheet, List<Piece>[] pieces, int system, float top)
    {
        bool opening = system == 0;
        float left = Margin, right = PageWidth - Margin;
        float content = left + (opening ? FirstLead : Lead);
        int firstStep = system * BarsPerSystem * NoteSheet.StepsPerBar;
        int bars = Math.Min(BarsPerSystem, sheet.Bars - system * BarsPerSystem);
        int lastStep = firstStep + bars * NoteSheet.StepsPerBar;
        float barWidth = (right - content) / BarsPerSystem;
        float StepX(int step) => content + (step - firstStep) / NoteSheet.StepsPerBar * barWidth + 10
                                 + (step - firstStep) % NoteSheet.StepsPerBar * (barWidth - 16) / NoteSheet.StepsPerBar;
        float systemRight = content + bars * barWidth;

        // Each voice's clef from the notes of this line - a voice that drops to its drums for a
        // while reads in the bass clef there - or the whole tune's, on a line of rests.
        var clefs = Enumerable.Range(0, sheet.Voices).Select(v =>
        {
            var here = sheet.Notes[v].Where(n => n.Start < lastStep && n.Start + n.Length > firstStep).ToList();
            return ClefOf(here.Count > 0 ? here : sheet.Notes[v]);
        }).ToArray();

        using var line = Stroke(0.6f);
        using var heavy = Stroke(2.2f);
        float staffTop = top + SystemTop;
        float systemBottom = staffTop + (sheet.Voices - 1) * (StaffHeight + StaffGap) + StaffHeight;

        // The bar's number over the first staff, the tempo over the opening.
        text.Draw(canvas, (system * BarsPerSystem + 1).ToString(CultureInfo.InvariantCulture), left + 1, staffTop - 6, 7.5f, colour: Faint);
        if (opening)
            DrawTempo(canvas, text, sheet, content + 6, staffTop - 9);

        canvas.DrawLine(left, staffTop, left, systemBottom, heavy);
        for (int voice = 0; voice < sheet.Voices; voice++)
        {
            float staff = staffTop + voice * (StaffHeight + StaffGap);
            for (int l = 0; l < 5; l++)
                canvas.DrawLine(left, staff + l * Space, systemRight, staff + l * Space, line);
            string name = opening ? sheet.VoiceName(voice) : $"V{voice + 1}";
            text.Draw(canvas, name, left - 4 - text.Width(name, 6.5f), staff + StaffHeight / 2 + 2.3f, 6.5f, colour: Faint);
            DrawClef(canvas, text, clefs[voice], left + 3, staff);
            if (opening)
                DrawTime(canvas, text, left + 34, staff);
            DrawStaff(canvas, pieces[voice], clefs[voice], staff, firstStep, lastStep, StepX, content, systemRight);
        }

        // Bar lines through every staff; the last bar of the tune ends with a double one.
        for (int bar = 1; bar <= bars; bar++)
        {
            float x = content + bar * barWidth;
            bool end = system * BarsPerSystem + bar == sheet.Bars;
            for (int voice = 0; voice < sheet.Voices; voice++)
            {
                float staff = staffTop + voice * (StaffHeight + StaffGap);
                if (end)
                {
                    canvas.DrawLine(x - 4.5f, staff, x - 4.5f, staff + StaffHeight, line);
                    canvas.DrawLine(x - 1.2f, staff, x - 1.2f, staff + StaffHeight, heavy);
                }
                else
                    canvas.DrawLine(x, staff, x, staff + StaffHeight, line);
            }
        }
    }

    // ------------------------------------------------------------------ the notes of a staff

    private static void DrawStaff(SKCanvas canvas, List<Piece> pieces, Clef clef, float staff, int firstStep, int lastStep,
                                  Func<int, float> stepX, float leftEdge, float rightEdge)
    {
        using var fill = Fill();
        using var stem = Stroke(0.9f);
        using var ledger = Stroke(0.8f);
        var accidentals = new Dictionary<int, bool>();
        int bar = -1;
        float bottom = staff + StaffHeight;
        float Y(int position) => bottom - position * Space / 2;

        foreach (var piece in pieces)
        {
            if (piece.At + piece.Steps <= firstStep || piece.At >= lastStep)
                continue;
            if (piece.At / NoteSheet.StepsPerBar != bar)
            {
                bar = piece.At / NoteSheet.StepsPerBar;
                accidentals.Clear();
            }

            int value = piece.Steps;
            bool dotted = value is 3 or 6 or 12;
            int plain = dotted ? value * 2 / 3 : value;
            float x = stepX(piece.At);

            if (piece.Pitch is not { } pitch)
            {
                // A whole bar's rest sits in the bar's middle.
                if (value == NoteSheet.StepsPerBar)
                    x = (stepX(piece.At) + stepX(piece.At + NoteSheet.StepsPerBar - 1)) / 2 + 3;
                DrawRest(canvas, fill, x, staff, plain, dotted);
                continue;
            }

            var (diatonic, sharp) = Diatonic(pitch - clef.Shift);
            int position = diatonic - (clef.Treble ? 30 : 18);
            float y = Y(position);
            float rx = Space * 0.62f;

            for (int p = -2; p >= position; p -= 2)
                canvas.DrawLine(x - rx * 1.7f, Y(p), x + rx * 1.7f, Y(p), ledger);
            for (int p = 10; p <= position; p += 2)
                canvas.DrawLine(x - rx * 1.7f, Y(p), x + rx * 1.7f, Y(p), ledger);

            bool had = accidentals.TryGetValue(diatonic, out bool wasSharp) && wasSharp;
            if (!piece.TiedFromBefore || piece.At % NoteSheet.StepsPerBar != 0)
            {
                if (sharp && !had)
                    DrawSharp(canvas, x - rx - Space * 1.05f, y);
                else if (!sharp && had)
                    DrawNatural(canvas, x - rx - Space * 1.0f, y);
            }
            accidentals[diatonic] = sharp;

            DrawHead(canvas, x, y, plain);
            if (plain < 16)
            {
                bool up = position < 4;
                float sx = up ? x + rx * 0.9f : x - rx * 0.9f;
                float end = up ? Math.Min(y - 3.5f * Space, Y(4)) : Math.Max(y + 3.5f * Space, Y(4));
                canvas.DrawLine(sx, y, sx, end, stem);
                int flags = plain switch { 2 => 1, 1 => 2, _ => 0 };
                for (int f = 0; f < flags; f++)
                    DrawFlag(canvas, fill, sx, end + (up ? f * Space * 0.95f : -f * Space * 0.95f), up);
            }
            if (dotted)
                canvas.DrawCircle(x + rx + Space * 0.55f, position % 2 == 0 ? y - Space / 2 : y, Space * 0.19f, fill);

            // Ties: to the next piece - or to the line's end, and on the next line from its start.
            bool tieUp = position >= 4;
            if (piece.TiedOn)
            {
                float to = piece.At + piece.Steps >= lastStep ? rightEdge - 2 : stepX(piece.At + piece.Steps) - rx;
                DrawTie(canvas, x + rx, to, y, tieUp);
            }
            if (piece.TiedFromBefore && piece.At == firstStep)
                DrawTie(canvas, leftEdge + 2, x - rx, y, tieUp);
        }
    }

    /// <summary>A voice's notes and the rests between them, split at bar lines and into the values
    /// a beat allows; a note's pieces tied.</summary>
    private static List<Piece> PiecesOf(IReadOnlyList<SheetNote> notes, int steps)
    {
        var pieces = new List<Piece>();
        void Add(int from, int length, int? pitch)
        {
            bool first = true;
            int at = from, end = from + length;
            while (at < end)
            {
                int inBar = at % NoteSheet.StepsPerBar;
                int room = Math.Min(end - at, NoteSheet.StepsPerBar - inBar);
                // Rests take a whole bar's rest for a bar of silence; notes the largest value that fits.
                int value = Values.First(v => v.Steps <= room && v.StartsAt(inBar)).Steps;
                bool more = at + value < end;
                pieces.Add(new Piece(at, value, pitch, pitch is not null && !first, pitch is not null && more));
                first = false;
                at += value;
            }
        }

        int cursor = 0;
        foreach (var note in notes)
        {
            if (note.Start >= steps)
                break;
            if (note.Start > cursor)
                Add(cursor, note.Start - cursor, null);
            int length = Math.Min(note.Length, steps - note.Start);
            Add(note.Start, length, note.Pitch);
            cursor = note.Start + length;
        }
        if (cursor < steps)
            Add(cursor, steps - cursor, null);
        return pieces;
    }

    /// <summary>The clef a voice reads best in: treble from G below middle C up, with an octave
    /// sign for a voice that lies far above it, bass below with one for far below.</summary>
    private static Clef ClefOf(IReadOnlyList<SheetNote> notes)
    {
        if (notes.Count == 0)
            return new Clef(true, 0, null);
        var sorted = notes.Select(n => n.Pitch).Order().ToList();
        int median = sorted[sorted.Count / 2];
        if (median >= 43)
            return median > 79 ? new Clef(true, 24, "15ma") : median > 67 ? new Clef(true, 12, "8va") : new Clef(true, 0, null);
        return median < 24 ? new Clef(false, -12, "8vb") : new Clef(false, 0, null);
    }

    /// <summary>A pitch's line or space, counting C D E F G A B up from C-0, and whether it is sharp.</summary>
    private static (int Diatonic, bool Sharp) Diatonic(int pitch)
    {
        pitch = Math.Clamp(pitch, 0, 119);
        int pc = pitch % 12;
        int letter = pc switch { 0 or 1 => 0, 2 or 3 => 1, 4 => 2, 5 or 6 => 3, 7 or 8 => 4, 9 or 10 => 5, _ => 6 };
        return (pitch / 12 * 7 + letter, pc is 1 or 3 or 6 or 8 or 10);
    }

    // ------------------------------------------------------------------ the shapes

    private static void DrawHead(SKCanvas canvas, float x, float y, int value)
    {
        float rx = Space * 0.62f, ry = Space * 0.44f;
        canvas.Save();
        canvas.Translate(x, y);
        if (value == 16)
        {
            using var ring = Stroke(1.5f);
            canvas.DrawOval(0, 0, rx * 1.12f, ry * 1.05f, ring);
        }
        else
        {
            canvas.RotateDegrees(-20);
            using var paint = value >= 8 ? Stroke(1.2f) : Fill();
            canvas.DrawOval(0, 0, value >= 8 ? rx * 0.9f : rx, value >= 8 ? ry * 0.85f : ry, paint);
        }
        canvas.Restore();
    }

    private static void DrawFlag(SKCanvas canvas, SKPaint fill, float x, float y, bool up)
    {
        float d = up ? 1 : -1, s = Space;
        using var path = new SKPath();
        path.MoveTo(x, y);
        path.CubicTo(x + 0.1f * s, y + d * 1.1f * s, x + 1.45f * s, y + d * 1.4f * s, x + 0.95f * s, y + d * 3.0f * s);
        path.CubicTo(x + 1.15f * s, y + d * 1.9f * s, x + 0.45f * s, y + d * 1.55f * s, x, y + d * 1.25f * s);
        path.Close();
        canvas.DrawPath(path, fill);
    }

    private static void DrawSharp(SKCanvas canvas, float x, float y)
    {
        using var thin = Stroke(0.8f);
        using var thick = Stroke(1.6f);
        float s = Space;
        canvas.DrawLine(x - 0.28f * s, y - 1.25f * s, x - 0.28f * s, y + 1.4f * s, thin);
        canvas.DrawLine(x + 0.28f * s, y - 1.4f * s, x + 0.28f * s, y + 1.25f * s, thin);
        canvas.DrawLine(x - 0.6f * s, y - 0.3f * s, x + 0.6f * s, y - 0.6f * s, thick);
        canvas.DrawLine(x - 0.6f * s, y + 0.6f * s, x + 0.6f * s, y + 0.3f * s, thick);
    }

    private static void DrawNatural(SKCanvas canvas, float x, float y)
    {
        using var thin = Stroke(0.8f);
        using var thick = Stroke(1.5f);
        float s = Space;
        canvas.DrawLine(x - 0.3f * s, y - 1.4f * s, x - 0.3f * s, y + 0.55f * s, thin);
        canvas.DrawLine(x + 0.3f * s, y - 0.55f * s, x + 0.3f * s, y + 1.4f * s, thin);
        canvas.DrawLine(x - 0.3f * s, y - 0.25f * s, x + 0.3f * s, y - 0.45f * s, thick);
        canvas.DrawLine(x - 0.3f * s, y + 0.45f * s, x + 0.3f * s, y + 0.25f * s, thick);
    }

    private static void DrawRest(SKCanvas canvas, SKPaint fill, float x, float staff, int value, bool dotted)
    {
        float s = Space;
        float middle = staff + 2 * s;
        switch (value)
        {
            case 16:
                canvas.DrawRect(x - 0.6f * s, staff + s, 1.2f * s, 0.5f * s, fill);
                break;
            case 8:
                canvas.DrawRect(x - 0.6f * s, middle - 0.5f * s, 1.2f * s, 0.5f * s, fill);
                break;
            case 4:
            {
                using var zigzag = Stroke(1.3f);
                using var path = new SKPath();
                path.MoveTo(x - 0.25f * s, middle - 1.5f * s);
                path.LineTo(x + 0.4f * s, middle - 0.7f * s);
                path.LineTo(x - 0.35f * s, middle + 0.1f * s);
                path.LineTo(x + 0.35f * s, middle + 0.75f * s);
                path.QuadTo(x - 0.6f * s, middle + 0.5f * s, x - 0.05f * s, middle + 1.45f * s);
                canvas.DrawPath(path, zigzag);
                break;
            }
            default:
            {
                // An eighth's rest, a sixteenth's with a second flag below.
                using var stroke = Stroke(1.0f);
                int flags = value == 1 ? 2 : 1;
                float topY = middle - 0.9f * s;
                canvas.DrawLine(x + 0.45f * s, topY, x - 0.2f * s, middle + (flags == 2 ? 1.6f : 1.1f) * s, stroke);
                for (int f = 0; f < flags; f++)
                {
                    float fy = topY + f * s;
                    canvas.DrawCircle(x - 0.35f * s, fy + 0.15f * s, 0.32f * s, fill);
                    canvas.DrawLine(x - 0.35f * s, fy + 0.35f * s, x + 0.45f * s - f * 0.25f * s, fy, stroke);
                }
                break;
            }
        }
        if (dotted)
            canvas.DrawCircle(x + 1.0f * s, middle - 0.5f * s, 0.19f * s, fill);
    }

    private static void DrawTie(SKCanvas canvas, float from, float to, float y, bool over)
    {
        if (to - from < 2)
            return;
        float d = over ? -1 : 1;
        float y0 = y + d * Space * 0.75f;
        float bend = Math.Min(Space * 1.3f, (to - from) * 0.18f);
        using var path = new SKPath();
        path.MoveTo(from, y0);
        path.CubicTo(from + (to - from) * 0.25f, y0 + d * bend, from + (to - from) * 0.75f, y0 + d * bend, to, y0);
        path.CubicTo(from + (to - from) * 0.75f, y0 + d * (bend + 0.9f), from + (to - from) * 0.25f, y0 + d * (bend + 0.9f), from, y0);
        path.Close();
        using var fill = Fill();
        canvas.DrawPath(path, fill);
    }

    private static void DrawClef(SKCanvas canvas, Text text, Clef clef, float x, float staff)
    {
        // A treble clef reaches from below the staff to above it, curling round the G line; a bass
        // clef hangs from the top line, its dots either side of the F line.
        float top = clef.Treble ? staff - 1.6f * Space : staff - 0.05f * Space;
        float height = clef.Treble ? 7.2f * Space : 3.25f * Space;
        if (!text.Symbol(canvas, clef.Treble ? "\U0001D11E" : "\U0001D122", x, top, height))
        {
            // No music font: the clef's letter, on its line.
            float line = clef.Treble ? staff + 3 * Space : staff + Space;
            text.Draw(canvas, clef.Treble ? "G" : "F", x + 2, line + 5, 15, bold: true);
        }
        if (clef.Mark is { } mark)
            text.Draw(canvas, mark, x + 2, clef.Shift > 0 ? staff - 2.0f * Space : staff + StaffHeight + 1.4f * Space, 6.5f, italic: true);
    }

    private static void DrawTime(SKCanvas canvas, Text text, float x, float staff)
    {
        text.Centre(canvas, "4", x + 5, staff + 2 * Space - 1.2f, 15, bold: true);
        text.Centre(canvas, "4", x + 5, staff + 4 * Space - 1.2f, 15, bold: true);
    }

    private static void DrawTempo(SKCanvas canvas, Text text, NoteSheet sheet, float x, float baseline)
    {
        using var fill = Fill();
        using var stem = Stroke(0.8f);
        canvas.Save();
        canvas.Translate(x, baseline - 1.5f);
        canvas.RotateDegrees(-20);
        canvas.DrawOval(0, 0, 3.0f, 2.1f, fill);
        canvas.Restore();
        canvas.DrawLine(x + 2.7f, baseline - 1.5f, x + 2.7f, baseline - 13, stem);
        text.Draw(canvas, string.Create(CultureInfo.InvariantCulture, $"= {sheet.BeatsPerMinute:0}"), x + 7, baseline, 9.5f, bold: true);
    }

    private static SKPaint Stroke(float width) => new()
    {
        Color = Ink,
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width,
        StrokeCap = SKStrokeCap.Butt,
    };

    private static SKPaint Fill() => new() { Color = Ink, IsAntialias = true, Style = SKPaintStyle.Fill };

    /// <summary>Text in the system's own typeface - and its music symbols in a font that has them.</summary>
    private sealed class Text : IDisposable
    {
        private readonly SKTypeface _regular = SKTypeface.Default;
        private readonly SKTypeface _bold = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
        private readonly SKTypeface _italic = SKTypeface.FromFamilyName(null, SKFontStyle.Italic) ?? SKTypeface.Default;
        private readonly SKTypeface? _music = SKFontManager.Default.MatchCharacter(0x1D11E);

        /// <summary>Text as its letters' outlines: no font goes into the PDF - whole fonts did, three
        /// megabytes of them - and it looks the same wherever it is opened.</summary>
        public void Draw(SKCanvas canvas, string text, float x, float y, float size, bool bold = false, bool italic = false, SKColor? colour = null)
        {
            using var paint = new SKPaint { Typeface = bold ? _bold : italic ? _italic : _regular, TextSize = size };
            using var outline = paint.GetTextPath(text, x, y);
            using var ink = new SKPaint { Color = colour ?? Ink, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawPath(outline, ink);
        }

        public void Centre(SKCanvas canvas, string text, float x, float y, float size, bool bold = false, SKColor? colour = null) =>
            Draw(canvas, text, x - Width(text, size, bold) / 2, y, size, bold, colour: colour);

        public float Width(string text, float size, bool bold = false)
        {
            using var paint = new SKPaint { Typeface = bold ? _bold : _regular, TextSize = size };
            return paint.MeasureText(text);
        }

        /// <summary>A music symbol scaled to fill <paramref name="height"/> from <paramref name="top"/>
        /// - false when no font on the system has it.</summary>
        public bool Symbol(SKCanvas canvas, string symbol, float x, float top, float height)
        {
            if (_music is null || !_music.ContainsGlyphs(symbol))
                return false;
            var bounds = default(SKRect);
            using (var probe = new SKPaint { Typeface = _music, TextSize = 100 })
                probe.MeasureText(symbol, ref bounds);
            if (bounds.Height <= 0)
                return false;
            float size = 100 * height / bounds.Height;
            var fitted = default(SKRect);
            using (var measure = new SKPaint { Typeface = _music, TextSize = size })
                measure.MeasureText(symbol, ref fitted);
            using var paint = new SKPaint { Typeface = _music, TextSize = size };
            using var outline = paint.GetTextPath(symbol, x - fitted.Left, top - fitted.Top);
            using var ink = new SKPaint { Color = Ink, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawPath(outline, ink);
            return true;
        }

        public void Dispose()
        {
            _bold.Dispose();
            _italic.Dispose();
            _music?.Dispose();
        }
    }
}
