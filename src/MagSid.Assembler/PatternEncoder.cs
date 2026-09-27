using MagSid.Core.Model;

namespace MagSid.Assembler;

/// <summary>
/// One pattern's byte stream, terminator included.
///
/// Deliberately a second implementation of what SongSizeCalculator.EncodedPatternLength already
/// counts, rather than a refactor of it. That one is pinned against real c6510 output for all seven
/// songs and is what the editor's live Song Length reads; rewriting it to hand bytes back would put
/// the pinning and the export on the same code, so a shared mistake would look consistent. Instead
/// PatternEncoderTests asserts the two agree on every pattern of every song - two implementations
/// that must match, one of them pinned to the assembler.
/// </summary>
public static class PatternEncoder
{
    private const int CmdDuration = 0x80;
    private const int CmdInstrument = 0xC0;
    private const int CmdNoteOff = 0xD0;
    private const int CmdWait = 0xD1;
    private const int CmdLoopEnd = 0xD2;

    private const int MaxDuration = 64;
    private const int MaxInstrument = 16;

    /// <summary>A note costs one byte and carries whatever instrument and duration are already
    /// current, so only a change emits a command; a run of "..." rows folds into the preceding
    /// note's duration; and LE splices the anchor's state back in when the body changed it.</summary>
    public static byte[] Encode(Pattern pattern, NoteDictionary notes)
    {
        var rows = pattern.Rows;
        var stream = new List<byte>();

        // Both start unset so every pattern re-establishes what it uses. Patterns are shared
        // between channels, so carrying state in from whatever played before would make the same
        // pattern sound different on CH1 than on CH3.
        int? curInst = null, curDur = null;

        // The loop anchor, plus the state as it stood when the anchor was passed.
        int lsTarget = 0;
        int? lsInst = null, lsDur = null;

        int consumedUntil = -1;

        void SetState(int? inst, int? dur)
        {
            if (inst is not null && inst != curInst)
            {
                if (inst is < 1 or > MaxInstrument)
                {
                    throw new SongTooLargeException(
                        $"PAT{pattern.Number:00}: instrument {inst} is out of range (1..{MaxInstrument}).");
                }

                stream.Add((byte)(CmdInstrument + inst.Value - 1));
                curInst = inst;
            }

            if (dur is not null && dur != curDur)
            {
                if (dur is < 1 or > MaxDuration)
                {
                    throw new SongTooLargeException(
                        $"PAT{pattern.Number:00}: a row lasts {dur} ticks; the encoding tops out at {MaxDuration}.");
                }

                stream.Add((byte)(CmdDuration + dur.Value - 1));
                curDur = dur;
            }
        }

        // Stops at a loop marker: a note must not swallow holds from the far side of one, since
        // those only play on the pass that actually reaches them.
        int CountHolds(int from)
        {
            int n = 0;
            while (from < rows.Count && rows[from] is HoldRow)
            {
                n++;
                from++;
            }

            return n;
        }

        for (int idx = 0; idx < rows.Count; idx++)
        {
            switch (rows[idx])
            {
                case LoopStartRow:
                    lsTarget = stream.Count;             // byte offset of the next thing emitted
                    lsInst = curInst;
                    lsDur = curDur;
                    break;

                case LoopEndRow(var repeatCount):
                {
                    // If the body changed instrument or duration, the jump back would re-enter with
                    // the loop's end state rather than its start. Splice the needed commands in at
                    // the anchor so every pass re-establishes them.
                    var fixup = new List<byte>();
                    if (lsInst is not null && curInst != lsInst)
                        fixup.Add((byte)(CmdInstrument + lsInst.Value - 1));
                    if (lsDur is not null && curDur != lsDur)
                        fixup.Add((byte)(CmdDuration + lsDur.Value - 1));
                    for (int i = 0; i < fixup.Count; i++)
                        stream.Insert(lsTarget + i, fixup[i]);

                    // curInst/curDur deliberately keep the body's end state: the fixup runs at the
                    // top of the loop, so it only affects what each pass starts with.
                    stream.Add(CmdLoopEnd);
                    stream.Add((byte)(repeatCount ?? 0));    // 0 = bare LE, loops forever
                    stream.Add((byte)lsTarget);
                    break;
                }

                case HoldRow:
                    // A hold no preceding note could absorb still has to cost time - the editor
                    // shows it as a row of its own. A wait row spends the ticks and leaves whatever
                    // is sounding alone.
                    if (idx > consumedUntil)
                    {
                        int duration = 1 + CountHolds(idx + 1);
                        SetState(null, duration);
                        stream.Add(CmdWait);
                        consumedUntil = idx + duration - 1;
                    }

                    break;

                case NoteOffRow:
                {
                    int duration = 1 + CountHolds(idx + 1);
                    consumedUntil = idx + duration - 1;
                    SetState(null, duration);            // a note-off uses no instrument
                    stream.Add(CmdNoteOff);
                    break;
                }

                case NoteRow note:
                {
                    int duration = 1 + CountHolds(idx + 1);
                    consumedUntil = idx + duration - 1;
                    SetState(note.InstrumentNumber, duration);
                    stream.Add((byte)notes.IdOf(note.Note));
                    break;
                }
            }
        }

        // voice_event and the LE anchor are single bytes, so the terminator's own offset has to stay
        // addressable.
        if (stream.Count > 255)
        {
            throw new SongTooLargeException(
                $"PAT{pattern.Number:00} encodes to {stream.Count} bytes; the player addresses a " +
                "pattern with a single-byte offset, so 255 is the ceiling.");
        }

        stream.Add(0x00);                                // end of pattern
        return stream.ToArray();
    }
}
