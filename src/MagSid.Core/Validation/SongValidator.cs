using MagSid.Core.Model;

namespace MagSid.Core.Validation;

/// <summary>
/// Data-level validity checks that hold regardless of where a <see cref="Song"/> came from
/// (loaded from a file, or built through the editor UI). Mirrors the assert() discipline in
/// the sibling assembly project's Startup\Tables.s exactly, so a Song that passes here is one
/// the existing assembler pipeline will also accept. Used both after parsing (Reader) and
/// before serializing (Writer), so both directions fail loudly instead of silently producing
/// something the assembler would reject.
/// </summary>
public static class SongValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(Song song)
    {
        var issues = new List<ValidationIssue>();

        ValidatePatternNumbering(song, issues);
        ValidateInstrumentNumbering(song, issues);
        ValidateInstrumentPrograms(song, issues);
        ValidateInstrumentReferences(song, issues);
        ValidatePatternReferences(song, issues);
        ValidateOrderNotEmpty(song, issues);
        ValidateRestartPosition(song, issues);

        return issues;
    }

    private static void ValidatePatternNumbering(Song song, List<ValidationIssue> issues)
    {
        if (song.Patterns.Count == 0)
        {
            issues.Add(new ValidationIssue("No patterns defined (expected at least one PAT01: ... END:)."));
            return;
        }

        var numbers = new HashSet<int>(song.Patterns.Select(p => p.Number));
        int max = numbers.Max();
        for (int n = 1; n <= max; n++)
        {
            if (!numbers.Contains(n))
            {
                issues.Add(new ValidationIssue(
                    $"Pattern numbers must be contiguous starting at 1 - missing PAT{n:D2}."));
                break;
            }
        }
    }

    private static void ValidateInstrumentNumbering(Song song, List<ValidationIssue> issues)
    {
        if (song.Instruments.Count == 0)
            return;

        var numbers = new HashSet<int>(song.Instruments.Select(i => i.Number));
        int max = numbers.Max();
        for (int n = 1; n <= max; n++)
        {
            if (!numbers.Contains(n))
            {
                issues.Add(new ValidationIssue(
                    $"Instrument numbers must be contiguous starting at 1 - missing instrument {n:D2}."));
                break;
            }
        }

        // A pattern row selects its instrument with a $C0..$CF command byte, so 16 is the most
        // the player can ever address (Startup\Tables.s asserts the same thing). Caught here
        // because an instrument past that one is not a broken instrument - it assembles fine and
        // simply never plays, while still taking a slot in all seven per-instrument tables and
        // pulling its programs into the pools. Silent dead weight is exactly what validation is
        // for; without this the first sign of trouble is the assembler failing at .sid export.
        if (max > MaxInstrumentNumber)
        {
            issues.Add(new ValidationIssue(
                $"Instrument {max:D2} is past the {MaxInstrumentNumber} a pattern can select " +
                $"($C0..$CF) - it can never be played, and only costs bytes."));
        }
    }

    /// <summary>Highest instrument number a pattern's $C0..$CF command can name.</summary>
    public const int MaxInstrumentNumber = 16;

    /// <summary>
    /// The ARP / FLT / PW ranges the assembler asserts in Startup\Tables.s. Checked here as
    /// well as at parse time because the editor can build these values in memory without ever
    /// going near the file parser, and the assembler's failure message arrives far too late to
    /// be useful.
    /// </summary>
    private static void ValidateInstrumentPrograms(Song song, List<ValidationIssue> issues)
    {
        foreach (var instrument in song.Instruments)
            ValidateProgram(instrument, $"Instrument {instrument.Number:D2}", issues);
    }

    /// <summary>The same program checks a song gets, for one instrument on its own - an instrument
    /// file is checked with these before it is let into a song.</summary>
    public static IReadOnlyList<ValidationIssue> ValidateInstrument(Instrument instrument, string who)
    {
        var issues = new List<ValidationIssue>();
        ValidateProgram(instrument, who, issues);
        return issues;
    }

    private static void ValidateProgram(Instrument instrument, string who, List<ValidationIssue> issues)
    {
        if (instrument.Wavetable.Contains((byte)0))
        {
            issues.Add(new ValidationIssue(
                $"{who}: a WAVE step of $00 ends a program, so it cannot also be a step."));
        }

        if (instrument.ArpeggioLoopPoint < 0 ||
            instrument.ArpeggioLoopPoint >= Math.Max(1, instrument.Arpeggio.Count))
        {
            issues.Add(new ValidationIssue(
                $"{who}: ARP LOOP must have at least one note after it."));
        }

        if (instrument.ArpeggioEnding == ArpeggioEnding.Hold && instrument.Arpeggio.Count == 0)
        {
            issues.Add(new ValidationIssue(
                $"{who}: ARP HOLD stays on the last step, so it needs at least one."));
        }

        foreach (var step in instrument.Filter)
        {
            if (step.SweepRate is < -128 or > 127)
            {
                issues.Add(new ValidationIssue(
                    $"{who}: FLT sweep rate must fit a signed byte, -128..127, got {step.SweepRate}."));
                break;
            }

            if (step.Cutoff is < FilterStep.MinCutoff or > FilterStep.MaxCutoff)
            {
                issues.Add(new ValidationIssue(
                    $"{who}: FLT cutoff must be ${FilterStep.MinCutoff:x2}..${FilterStep.MaxCutoff:x2} " +
                    $"($00 ends a program and $ff marks a setup step), got ${step.Cutoff:x2}."));
                break;
            }
        }

        if (instrument.PulseWidth is < 0 or > 15)
        {
            issues.Add(new ValidationIssue(
                $"{who}: PW width is in 1/16ths, so 0..15, got {instrument.PulseWidth}."));
        }

        if (instrument.PulseSweep is < -128 or > 127)
        {
            issues.Add(new ValidationIssue(
                $"{who}: PW delta must fit a signed byte, -128..127, got {instrument.PulseSweep}."));
        }
    }

    private static void ValidateInstrumentReferences(Song song, List<ValidationIssue> issues)
    {
        var known = new HashSet<int>(song.Instruments.Select(i => i.Number));

        foreach (var pattern in song.Patterns)
        {
            foreach (var row in pattern.Rows)
            {
                if (row is NoteRow note && !known.Contains(note.InstrumentNumber))
                {
                    issues.Add(new ValidationIssue(
                        $"PAT{pattern.Number:D2}: instrument {note.InstrumentNumber} is not defined in INSTRUMENTS."));
                }
            }
        }
    }

    private static void ValidatePatternReferences(Song song, List<ValidationIssue> issues)
    {
        var known = new HashSet<int>(song.Patterns.Select(p => p.Number));

        foreach (var order in song.ChannelOrders)
        {
            foreach (var token in order.Tokens)
            {
                if (token is PatternRefToken(var patternNumber) && !known.Contains(patternNumber))
                {
                    issues.Add(new ValidationIssue(
                        $"ORDER_CH{order.ChannelNumber}: references undefined pattern {patternNumber}."));
                }
            }
        }
    }

    private static void ValidateOrderNotEmpty(Song song, List<ValidationIssue> issues)
    {
        foreach (var order in song.ChannelOrders)
        {
            if (order.Tokens.Count == 0)
                issues.Add(new ValidationIssue($"ORDER_CH{order.ChannelNumber}: ORDER list must contain at least one pattern."));
        }
    }

    private static void ValidateRestartPosition(Song song, List<ValidationIssue> issues)
    {
        foreach (var order in song.ChannelOrders)
        {
            if (order.Tokens.Count > 0 && (order.RestartPosition < 1 || order.RestartPosition > order.Tokens.Count))
            {
                issues.Add(new ValidationIssue(
                    $"ORDER_CH{order.ChannelNumber}: 'restart' target {order.RestartPosition} is out of range (1..{order.Tokens.Count})."));
            }
        }
    }
}
