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

            // Growing can run over the release that used to stop the note - its own, left by an
            // earlier shortening. The holds behind that release then carry the note on to the next
            // event, and the note ends up longer than asked. A release where it should stop keeps
            // the length exactly what was asked for, which is what a drag that grows a note one
            // beat at a time relies on.
            if (row + allowed < rows.Count && rows[row + allowed] is HoldRow)
                rows[row + allowed] = new NoteOffRow();

            return true;
        }

        // Shortening: the note stops, which means a release at the first row it no longer covers.
        // The rows after that stay holds and now extend the release instead, which is the same
        // shape a hand-written pattern has.
        rows[row + allowed] = new NoteOffRow();

        // The release that used to end the note now comes after silence, and says nothing - it
        // would only cost a byte in the export and draw a second release block in the roll.
        if (row + current < rows.Count && rows[row + current] is NoteOffRow)
            rows[row + current] = new HoldRow();

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

    /// <summary>
    /// Takes the note at <paramref name="row"/> away and leaves silence where it was.
    ///
    /// Not the same as <see cref="Remove"/>, which turns the note's row into a hold - the tracker's
    /// way, where a cleared row extends whatever sounded before it. In a piano roll that reads as
    /// the note before quietly growing into the gap, so the roll's erasing stops it there instead.
    /// Returns false when there is no note on that row.
    /// </summary>
    public static bool Erase(Pattern pattern, int row)
    {
        var rows = pattern.Rows;
        if (row < 0 || row >= rows.Count || rows[row] is not NoteRow)
            return false;

        ClearRange(pattern, row, row + HeldLength(rows, row) - 1);
        return true;
    }

    /// <summary>
    /// Moves several notes at once, each keeping its instrument and its length.
    ///
    /// All of them are lifted before any is put down - moving them one by one would let an early
    /// move land on a note still waiting to go, or measure a note's length after a neighbour had
    /// already been turned into holds beside it. Each leaves silence where it was, as
    /// <see cref="Erase"/> does, and each is put down at exactly its old length, with a release
    /// after it if holds would otherwise carry it on. A note put down over one that was not moving
    /// replaces it: this format plays one note at a time.
    /// </summary>
    public static void MoveNotes(Pattern pattern, IReadOnlyList<(int FromRow, int ToRow, NoteName Note)> moves)
    {
        var rows = pattern.Rows;
        var lifted = new List<(int ToRow, NoteName Note, int Instrument, int Length)>();

        foreach (var (from, to, note) in moves)
        {
            if (from < 0 || from >= rows.Count || rows[from] is not NoteRow existing)
                continue;

            lifted.Add((to, note, existing.InstrumentNumber, HeldLength(rows, from)));
        }

        foreach (var (from, _, _) in moves)
            Erase(pattern, from);

        foreach (var (to, note, instrument, length) in lifted.OrderBy(l => l.ToRow))
        {
            if (to < 0 || to >= rows.Count || rows[to] is LoopStartRow or LoopEndRow)
                continue;

            SetNote(pattern, to, note, instrument, length);
            SetLength(pattern, to, length);
        }
    }

    /// <summary>
    /// Cuts the note at <paramref name="row"/> in two at <paramref name="atRow"/>: the second half
    /// is a new trigger of the same note and instrument, and the first half ends where it starts.
    ///
    /// Only inside the note - the split row must be one of its holds. Splitting on its first row
    /// would be a note of nothing, and past its end there is nothing of it to split. Returns true
    /// when it split.
    /// </summary>
    public static bool Split(Pattern pattern, int row, int atRow)
    {
        var rows = pattern.Rows;
        if (row < 0 || row >= rows.Count || rows[row] is not NoteRow note)
            return false;

        if (atRow <= row || atRow >= row + HeldLength(rows, row) || rows[atRow] is not HoldRow)
            return false;

        rows[atRow] = new NoteRow(note.Note, note.InstrumentNumber);
        return true;
    }

    /// <summary>
    /// Joins the note at <paramref name="row"/> to the next note after it, so it sounds on through
    /// both: the next note's trigger and any release between them become holds.
    ///
    /// The first note's pitch and instrument carry on - this format plays one note at a time, so
    /// one of the two has to win, and the one clicked is the one meant. Stops at a loop marker,
    /// which a note cannot reach past, and returns false when there is no next note to join.
    /// </summary>
    public static bool Glue(Pattern pattern, int row)
    {
        var rows = pattern.Rows;
        if (row < 0 || row >= rows.Count || rows[row] is not NoteRow)
            return false;

        for (int next = row + 1; next < rows.Count; next++)
        {
            switch (rows[next])
            {
                case HoldRow or NoteOffRow:
                    continue;

                case NoteRow:
                    for (int i = row + 1; i <= next; i++)
                        rows[i] = new HoldRow();
                    return true;

                default:
                    return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Silences rows <paramref name="fromRow"/> to <paramref name="toRow"/>: every note and release
    /// in them goes, and a release at the start stops whatever was sounding into the range.
    ///
    /// The release is only written when something is sounding there - after a release or at the
    /// very start of the pattern the range is silent already, and a release would be a byte spent
    /// on saying so twice. Loop markers stay: they are structure, not content. Notes that started
    /// inside the range and held on past its end go with it, since their holds now extend the
    /// silence instead.
    /// </summary>
    public static void ClearRange(Pattern pattern, int fromRow, int toRow)
    {
        var rows = pattern.Rows;
        fromRow = Math.Max(0, fromRow);
        toRow = Math.Min(rows.Count - 1, toRow);
        if (fromRow > toRow)
            return;

        bool soundingBefore = false;
        for (int back = fromRow - 1; back >= 0; back--)
        {
            if (rows[back] is NoteRow) { soundingBefore = true; break; }
            if (rows[back] is NoteOffRow) break;
        }

        int firstPlayable = -1;
        for (int r = fromRow; r <= toRow; r++)
        {
            if (rows[r] is LoopStartRow or LoopEndRow)
                continue;

            if (firstPlayable < 0)
                firstPlayable = r;

            if (rows[r] is NoteRow or NoteOffRow)
                rows[r] = new HoldRow();
        }

        if (soundingBefore && firstPlayable >= 0)
            rows[firstPlayable] = new NoteOffRow();

        // The range ends silent either way, so a release that comes next - the one that used to
        // stop a note cleared from it - now releases nothing. Not across a loop marker, where what
        // sounds before it depends on which pass of the loop is playing.
        for (int next = toRow + 1; next < rows.Count; next++)
        {
            if (rows[next] is HoldRow)
                continue;

            if (rows[next] is NoteOffRow)
                rows[next] = new HoldRow();

            break;
        }
    }

    /// <summary>
    /// Draws a run of one-row notes from <paramref name="fromNote"/> at <paramref name="fromRow"/>
    /// to <paramref name="toNote"/> at <paramref name="toRow"/>, the pitch stepping evenly between
    /// them - a slide or an arpeggio drawn in one stroke rather than a note at a time.
    ///
    /// Loop markers are stepped over, not written on. Each note ends where the next begins, so
    /// none needs a release; the last gets one if holds follow it, or it would ring on to the next
    /// event.
    /// </summary>
    public static void DrawLine(Pattern pattern, int fromRow, NoteName fromNote, int toRow, NoteName toNote, int instrumentNumber)
    {
        var rows = pattern.Rows;
        if (toRow < fromRow)
        {
            (fromRow, toRow) = (toRow, fromRow);
            (fromNote, toNote) = (toNote, fromNote);
        }

        fromRow = Math.Max(0, fromRow);
        toRow = Math.Min(rows.Count - 1, toRow);

        var playable = new List<int>();
        for (int r = fromRow; r <= toRow; r++)
        {
            if (rows[r] is not (LoopStartRow or LoopEndRow))
                playable.Add(r);
        }

        if (playable.Count == 0)
            return;

        int from = fromNote.TableIndex;
        int to = toNote.TableIndex;
        var notes = NoteName.AllNotes;

        for (int i = 0; i < playable.Count; i++)
        {
            double t = playable.Count == 1 ? 0 : i / (double)(playable.Count - 1);
            int index = Math.Clamp((int)Math.Round(from + ((to - from) * t)), 0, notes.Count - 1);
            rows[playable[i]] = new NoteRow(notes[index], instrumentNumber);
        }

        SetLength(pattern, playable[^1], 1);
    }
}
