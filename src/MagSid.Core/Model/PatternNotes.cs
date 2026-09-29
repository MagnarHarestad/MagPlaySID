namespace MagSid.Core.Model;

/// <summary>A note as a piano roll sees it: where it starts and how many rows it sounds for.
/// <paramref name="Length"/> counts the triggering row itself, so the shortest note is 1.</summary>
public sealed record NoteBlock(int Row, int Length, NoteName Note, int InstrumentNumber);

/// <summary>A "---" and the rows it holds for - the voice releasing rather than sounding.</summary>
public sealed record ReleaseBlock(int Row, int Length);

/// <summary>An LS or LE on a row of its own. <paramref name="RepeatCount"/> is null on an LS and on
/// a bare LE, which loops forever.</summary>
public sealed record LoopMarker(int Row, bool IsStart, int? RepeatCount);

/// <summary>
/// Reads a pattern as blocks and writes blocks back as rows.
///
/// The whole difference between the two editors lives here. A tracker row is a step: a note, then
/// "..." lines that each extend it. A piano roll has no such thing - a hold row <em>is</em> the
/// length of the note before it, which is why the roll shows no marker for one. Every edit therefore
/// has to be translated in both directions, and getting that wrong silently rewrites music.
///
/// Two rules the translation must keep, both borrowed from the emitter that turns these rows into
/// bytes:
///
/// - A note never extends across a loop marker. Holds on the far side of one only play on the pass
///   that reaches them, so swallowing them would change what the song does.
/// - A pattern's row count never changes. Rows are the pattern's own length; lengthening a note
///   consumes the rows after it rather than pushing anything along.
/// </summary>
public static class PatternNotes
{
    /// <summary>Every triggered note, in row order.</summary>
    public static IReadOnlyList<NoteBlock> Read(Pattern pattern)
    {
        var blocks = new List<NoteBlock>();
        var rows = pattern.Rows;

        for (int row = 0; row < rows.Count; row++)
        {
            if (rows[row] is not NoteRow note)
                continue;

            blocks.Add(new NoteBlock(row, HeldLength(rows, row), note.Note, note.InstrumentNumber));
        }

        return blocks;
    }

    /// <summary>Every note-off and the rows it holds for. Drawn hollow by the roll: the voice is
    /// releasing there, which is neither sounding nor silent.</summary>
    public static IReadOnlyList<ReleaseBlock> Releases(Pattern pattern)
    {
        var blocks = new List<ReleaseBlock>();
        var rows = pattern.Rows;

        for (int row = 0; row < rows.Count; row++)
        {
            if (rows[row] is NoteOffRow)
                blocks.Add(new ReleaseBlock(row, HeldLength(rows, row)));
        }

        return blocks;
    }

    /// <summary>How many rows an event at <paramref name="row"/> occupies - itself plus the holds
    /// after it, stopping at a loop marker or any other event.</summary>
    private static int HeldLength(IReadOnlyList<PatternRow> rows, int row)
    {
        int length = 1;
        for (int next = row + 1; next < rows.Count && rows[next] is HoldRow; next++)
            length++;

        return length;
    }

    /// <summary>Every LS and LE in the pattern, in row order.</summary>
    public static IReadOnlyList<LoopMarker> Markers(Pattern pattern)
    {
        var markers = new List<LoopMarker>();

        for (int row = 0; row < pattern.Rows.Count; row++)
        {
            markers.Add(pattern.Rows[row] switch
            {
                LoopStartRow => new LoopMarker(row, true, null),
                LoopEndRow end => new LoopMarker(row, false, end.RepeatCount),
                _ => null!,
            });
        }

        markers.RemoveAll(marker => marker is null);
        return markers;
    }

    /// <summary>
    /// Puts an LS on a row, moving the loop anchor there.
    ///
    /// A marker takes a row of its own, so whatever was there is gone - and a note before it can no
    /// longer reach past, because holds on the far side only play on the pass that gets to them.
    /// Both are what putting a marker in the middle of a phrase means.
    /// </summary>
    public static void SetLoopStart(Pattern pattern, int row)
    {
        if (row < 0 || row >= pattern.Rows.Count)
            return;

        pattern.Rows[row] = new LoopStartRow();
    }

    /// <summary>
    /// Puts an LE on a row.
    ///
    /// <paramref name="repeatCount"/> null is a bare LE, which loops forever; a number plays the
    /// marked section that many times in total and then falls through. Loops do not nest, so an LE
    /// always pairs with the nearest LS before it - or with the start of the pattern when there is
    /// none, which is a whole-pattern loop and perfectly ordinary.
    /// </summary>
    public static void SetLoopEnd(Pattern pattern, int row, int? repeatCount)
    {
        if (row < 0 || row >= pattern.Rows.Count)
            return;

        // The count is one byte in the stream and 0 there already means "forever", which null
        // spells here - so a caller asking for 0 gets the bare LE it must have meant.
        int? clamped = repeatCount is null or <= 0 ? null : Math.Min(repeatCount.Value, 255);
        pattern.Rows[row] = new LoopEndRow(clamped);
    }

    /// <summary>Takes a marker away, leaving a hold - the same nothing a removed note leaves, since
    /// this format has no blank row. Returns false when that row held no marker.</summary>
    public static bool RemoveMarker(Pattern pattern, int row)
    {
        if (row < 0 || row >= pattern.Rows.Count || pattern.Rows[row] is not (LoopStartRow or LoopEndRow))
            return false;

        pattern.Rows[row] = new HoldRow();
        return true;
    }

    /// <summary>The furthest a note starting at <paramref name="row"/> may reach: up to the next
    /// loop marker, or the end of the pattern.</summary>
    public static int MaxLengthAt(Pattern pattern, int row)
    {
        var rows = pattern.Rows;
        if (row < 0 || row >= rows.Count)
            return 0;

        int length = 1;
        for (int next = row + 1; next < rows.Count; next++)
        {
            if (rows[next] is LoopStartRow or LoopEndRow)
                break;

            length++;
        }

        return length;
    }

    /// <summary>
    /// Puts a note at <paramref name="row"/>, running for <paramref name="length"/> rows.
    ///
    /// The rows it covers become holds, which is what a length is. Anything that started inside
    /// them is gone, and a note that started before and reached into them simply becomes shorter -
    /// both of which are what dropping a note on top of another means.
    /// </summary>
    public static void SetNote(Pattern pattern, int row, NoteName note, int instrumentNumber, int length = 1)
    {
        var rows = pattern.Rows;
        if (row < 0 || row >= rows.Count)
            return;

        if (rows[row] is LoopStartRow or LoopEndRow)
            return;

        int allowed = Math.Clamp(length, 1, MaxLengthAt(pattern, row));

        rows[row] = new NoteRow(note, instrumentNumber);
        for (int i = 1; i < allowed; i++)
            rows[row + i] = new HoldRow();
    }

    /// <summary>
    /// Changes how long the note at <paramref name="row"/> sounds.
    ///
    /// Growing consumes the rows after it as holds. Shortening cannot simply hand them back,
    /// because this format has no empty row: every row is a note, a hold, a release or a marker,
    /// and a hold always extends whatever came before it. So a note's length IS the count of holds
    /// after it, and the only way to stop one early is to put a "---" where it should stop - which
    /// is exactly what a tracker user does by hand, and audible, so it is not done silently.
    ///
    /// Returns true when the length changed.
    /// </summary>
    public static bool SetLength(Pattern pattern, int row, int length)
    {
        var rows = pattern.Rows;
        if (row < 0 || row >= rows.Count || rows[row] is not NoteRow)
            return false;

        int current = HeldLength(rows, row);
        int allowed = Math.Clamp(length, 1, MaxLengthAt(pattern, row));
        if (allowed == current)
            return false;

        if (allowed > current)
        {
            for (int i = current; i < allowed; i++)
                rows[row + i] = new HoldRow();

            return true;
        }

        // Shortening: the note stops, which means a release at the first row it no longer covers.
        // The rows after that stay holds and now extend the release instead, which is the same
        // shape a hand-written pattern has.
        rows[row + allowed] = new NoteOffRow();
        return true;
    }

    /// <summary>
    /// Takes the note at <paramref name="row"/> away.
    ///
    /// Its row becomes a hold, not a blank - a tracker has no blank. The rows that were its length
    /// stay holds and now extend whatever sounded before it, which is exactly what clearing a note
    /// in the tracker does, and why deleting a note in the roll can make an earlier one longer.
    /// </summary>
    public static void Remove(Pattern pattern, int row)
    {
        var rows = pattern.Rows;
        if (row < 0 || row >= rows.Count || rows[row] is not (NoteRow or NoteOffRow))
            return;

        rows[row] = new HoldRow();
    }

    /// <summary>Puts a "---" at <paramref name="row"/>, releasing whatever is sounding.</summary>
    public static void SetRelease(Pattern pattern, int row, int length = 1)
    {
        var rows = pattern.Rows;
        if (row < 0 || row >= rows.Count || rows[row] is LoopStartRow or LoopEndRow)
            return;

        int allowed = Math.Clamp(length, 1, MaxLengthAt(pattern, row));

        rows[row] = new NoteOffRow();
        for (int i = 1; i < allowed; i++)
            rows[row + i] = new HoldRow();
    }

    /// <summary>Moves a note to another row and pitch, keeping its length where the new place
    /// allows it. Used by dragging a block around the roll.</summary>
    public static void Move(Pattern pattern, int fromRow, int toRow, NoteName note)
    {
        var rows = pattern.Rows;
        if (fromRow < 0 || fromRow >= rows.Count || rows[fromRow] is not NoteRow existing)
            return;

        if (toRow < 0 || toRow >= rows.Count || rows[toRow] is LoopStartRow or LoopEndRow)
            return;

        int length = HeldLength(rows, fromRow);
        Remove(pattern, fromRow);
        SetNote(pattern, toRow, note, existing.InstrumentNumber, length);
    }
}
