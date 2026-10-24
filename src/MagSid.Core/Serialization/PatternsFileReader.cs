using System.Text.RegularExpressions;
using MagSid.Core.Model;
using MagSid.Core.Validation;

namespace MagSid.Core.Serialization;

/// <summary>
/// Reads the Patterns.s grammar defined in the sibling assembly project's
/// Startup\Tables.s: a "var v_Song_Speed = N" line, an INSTRUMENTS: block, one or more
/// global PATnn: blocks, and exactly three ORDER_CH1/2/3: blocks. Mirrors that Lua parser's
/// semantics exactly (case-insensitive keywords, whitespace-stripped pattern rows, no
/// uniqueness constraint on in-pattern LS/LE) so a file this reads is one the existing
/// assembler pipeline will also accept - see SongValidator for the shared, symmetric
/// validation used by both this reader and PatternsFileWriter.
/// </summary>
public static partial class PatternsFileReader
{
    private enum Mode { None, Instruments, Pattern, Order }

    public static Song Read(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static Song Parse(string text, string sourceName = "Patterns.s")
    {
        var song = new Song();
        var lines = SplitLines(text);

        Mode mode = Mode.None;
        Pattern? currentPattern = null;
        int currentOrderChannel = 0;
        var seenPatternNumbers = new HashSet<int>();
        var seenOrderChannels = new HashSet<int>();
        var orderRawLines = new Dictionary<int, List<(int LineNumber, string Text)>>();
        var instrumentNames = new Dictionary<int, string>();
        bool sawSongSpeed = false;

        for (int i = 0; i < lines.Count; i++)
        {
            int lineNumber = i + 1;
            string raw = lines[i];

            // "; Instrument NN : Name" is a whole-line comment giving instrument NN a label -
            // purely for the editor's own benefit, never touching a SID register or the
            // assembled song, which is exactly why it lives in a comment rather than a section
            // on the instrument's own line: nothing here needs to understand it, so nothing has
            // to. Checked against the raw line, before the generic comment strip below turns a
            // whole-line comment into nothing and skips it.
            var nameMatch = InstrumentNameCommentRegex().Match(raw.Trim());
            if (nameMatch.Success)
            {
                instrumentNames[int.Parse(nameMatch.Groups[1].Value)] = nameMatch.Groups[2].Value.Trim();
                continue;
            }

            // A ';' starts a comment that runs to end of line, wherever it appears. No token in
            // this grammar contains one, so cutting here is safe - and it has to happen, because
            // the assembler's own reader tolerates a trailing comment on the speed line. A file
            // that assembles must open in the editor.
            int comment = raw.IndexOf(';');
            string trimmed = (comment >= 0 ? raw[..comment] : raw).Trim();

            if (trimmed.Length == 0)
                continue;

            if (trimmed.Equals("INSTRUMENTS:", StringComparison.OrdinalIgnoreCase))
            {
                mode = Mode.Instruments;
                continue;
            }

            var patMatch = PatternHeaderRegex().Match(trimmed);
            if (patMatch.Success)
            {
                int number = int.Parse(patMatch.Groups[1].Value);
                if (!seenPatternNumbers.Add(number))
                    throw Error(sourceName, lineNumber, $"Duplicate PAT{number:D2}.");

                currentPattern = new Pattern { Number = number };
                song.Patterns.Add(currentPattern);
                mode = Mode.Pattern;
                continue;
            }

            var orderMatch = OrderHeaderRegex().Match(trimmed);
            if (orderMatch.Success)
            {
                int channel = int.Parse(orderMatch.Groups[1].Value);
                if (!seenOrderChannels.Add(channel))
                    throw Error(sourceName, lineNumber, $"Duplicate ORDER_CH{channel}.");

                currentOrderChannel = channel;
                orderRawLines[channel] = [];
                mode = Mode.Order;
                continue;
            }

            if (trimmed.Equals("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == Mode.Order)
                {
                    ParseOrderTokens(
                        sourceName, song.ChannelOrders[currentOrderChannel - 1], orderRawLines[currentOrderChannel]);
                }

                mode = Mode.None;
                currentPattern = null;
                currentOrderChannel = 0;
                continue;
            }

            switch (mode)
            {
                case Mode.Instruments:
                    var instrument = ParseInstrumentLine(sourceName, lineNumber, trimmed);
                    if (instrumentNames.TryGetValue(instrument.Number, out string? name))
                        instrument.Name = name;
                    song.Instruments.Add(instrument);
                    break;

                case Mode.Pattern:
                    currentPattern!.Rows.Add(ParsePatternRow(sourceName, lineNumber, raw));
                    break;

                case Mode.Order:
                    orderRawLines[currentOrderChannel].Add((lineNumber, trimmed));
                    break;

                case Mode.None:
                    if (SongFunkRegex().Match(trimmed) is { Success: true } funkMatch)
                    {
                        song.FunkSpeed = int.Parse(funkMatch.Groups[1].Value);
                        break;
                    }

                    var speedMatch = SongSpeedRegex().Match(trimmed);
                    if (!speedMatch.Success)
                        throw Error(sourceName, lineNumber, $"Unexpected line outside any block: '{trimmed}'.");
                    song.Speed = int.Parse(speedMatch.Groups[1].Value);
                    sawSongSpeed = true;
                    break;
            }
        }

        if (!sawSongSpeed)
            throw Error(sourceName, 0, "Missing 'var v_Song_Speed = N' line.");

        for (int ch = 1; ch <= Song.ChannelCount; ch++)
        {
            if (!seenOrderChannels.Contains(ch))
                throw Error(sourceName, 0, $"Missing ORDER_CH{ch}: block.");
        }

        var issues = SongValidator.Validate(song);
        if (issues.Count > 0)
            throw Error(sourceName, 0, issues[0].Message);

        return song;
    }

    private static Instrument ParseInstrumentLine(string sourceName, int lineNumber, string line)
    {
        var m = InstrumentLineRegex().Match(line);
        if (!m.Success)
            throw Error(sourceName, lineNumber, $"INSTRUMENTS: cannot parse line: {line}");

        var instrument = new Instrument
        {
            Number = int.Parse(m.Groups[1].Value),
            Waveform = Convert.ToByte(m.Groups[2].Value, 16),
            AttackDecay = Convert.ToByte(m.Groups[3].Value, 16),
            SustainRelease = Convert.ToByte(m.Groups[4].Value, 16),
        };

        ParseInstrumentSections(sourceName, lineNumber, line, m.Groups[5].Value, instrument);
        return instrument;
    }

    /// <summary>
    /// Everything past the SR value: a comma-separated list of optional WAVE / ARP / FLT / PW
    /// sections plus the bare NOHR flag, in any order. Neither section body contains a comma,
    /// which is what makes splitting on commas enough to find them - the same approach
    /// Startup\Tables.s takes, kept deliberately identical so the two parsers cannot drift into
    /// accepting different files.
    /// </summary>
    private static void ParseInstrumentSections(
        string sourceName, int lineNumber, string line, string tail, Instrument instrument)
    {
        bool sawWave = false, sawArp = false, sawFilter = false, sawPulse = false,
             sawNoHardRestart = false, sawSoftHardRestart = false, sawVibrato = false;

        foreach (string rawSegment in tail.Split(','))
        {
            string segment = rawSegment.Trim();
            if (segment.Length == 0)
                continue;

            // Bare keyword, no body - does not fit SectionRegex's "keyword <space> body" shape.
            if (segment.Equals("NOHR", StringComparison.OrdinalIgnoreCase))
            {
                if (sawNoHardRestart)
                    throw Error(sourceName, lineNumber, $"INSTRUMENTS: duplicate NOHR: {line}");
                sawNoHardRestart = true;
                instrument.NoHardRestart = true;
                continue;
            }

            if (segment.Equals("SOFTHR", StringComparison.OrdinalIgnoreCase))
            {
                if (sawSoftHardRestart)
                    throw Error(sourceName, lineNumber, $"INSTRUMENTS: duplicate SOFTHR: {line}");
                sawSoftHardRestart = true;
                instrument.SoftHardRestart = true;
                continue;
            }

            var m = SectionRegex().Match(segment);
            string keyword = m.Success ? m.Groups[1].Value.ToUpperInvariant() : string.Empty;
            string body = m.Success ? m.Groups[2].Value.Trim() : string.Empty;

            switch (keyword)
            {
                case "WAVE":
                    if (sawWave)
                        throw Error(sourceName, lineNumber, $"INSTRUMENTS: duplicate WAVE: {line}");
                    sawWave = true;
                    ParseWavetable(sourceName, lineNumber, line, body, instrument);
                    break;

                case "ARP":
                    if (sawArp)
                        throw Error(sourceName, lineNumber, $"INSTRUMENTS: duplicate ARP: {line}");
                    sawArp = true;
                    ParseArpeggio(sourceName, lineNumber, line, body, instrument);
                    break;

                case "FLT":
                    if (sawFilter)
                        throw Error(sourceName, lineNumber, $"INSTRUMENTS: duplicate FLT: {line}");
                    sawFilter = true;
                    ParseFilter(sourceName, lineNumber, line, body, instrument);
                    break;

                case "PW":
                    if (sawPulse)
                        throw Error(sourceName, lineNumber, $"INSTRUMENTS: duplicate PW: {line}");
                    sawPulse = true;
                    ParsePulse(sourceName, lineNumber, line, body, instrument);
                    break;

                // Slide moved from the instrument to the patterns. Refusing it rather than
                // dropping it keeps a file from quietly losing its glides.
                case "SLIDE":
                    throw Error(sourceName, lineNumber,
                        "INSTRUMENTS: SLIDE is no longer an instrument setting - write slide rows in " +
                        $"the pattern instead, \"1xx\" up or \"2xx\" down in hex: {line}");

                case "VIB":
                    if (sawVibrato)
                        throw Error(sourceName, lineNumber, $"INSTRUMENTS: duplicate VIB: {line}");
                    sawVibrato = true;
                    ParseVibrato(sourceName, lineNumber, line, body, instrument);
                    break;

                default:
                    throw Error(sourceName, lineNumber,
                        $"INSTRUMENTS: unknown section '{segment}' in: {line}");
            }
        }
    }

    /// <summary>VIB $ds $depth - delay/speed nibbles, then a depth of $01..$ff ($00 would mean
    /// "no vibrato", which is spelt by leaving the section out).</summary>
    private static void ParseVibrato(
        string sourceName, int lineNumber, string line, string body, Instrument instrument)
    {
        var values = HexByteRegex().Matches(body);
        if (values.Count != 2 || HexByteRegex().Replace(body, "").Trim().Length != 0)
        {
            throw Error(sourceName, lineNumber,
                $"INSTRUMENTS: VIB takes a delay/speed byte and a depth byte, e.g. VIB $32 $10: {line}");
        }

        int delaySpeed = Convert.ToInt32(values[0].Groups[1].Value, 16);
        int depth = Convert.ToInt32(values[1].Groups[1].Value, 16);
        if (delaySpeed > 0xff || depth < 0x01 || depth > 0xff)
        {
            throw Error(sourceName, lineNumber,
                $"INSTRUMENTS: VIB delay/speed must be $00..$ff and depth $01..$ff: {line}");
        }

        instrument.VibratoDelaySpeed = (byte)delaySpeed;
        instrument.VibratoDepth = (byte)depth;
    }

    private static void ParseWavetable(
        string sourceName, int lineNumber, string line, string body, Instrument instrument)
    {
        foreach (Match step in HexByteRegex().Matches(body))
        {
            int value = Convert.ToInt32(step.Groups[1].Value, 16);
            if (value == 0)
            {
                throw Error(sourceName, lineNumber,
                    $"INSTRUMENTS: WAVE step $00 ends a program, so it cannot also be a step: {line}");
            }

            instrument.Wavetable.Add((byte)value);
        }

        if (instrument.Wavetable.Count == 0)
            throw Error(sourceName, lineNumber, $"INSTRUMENTS: WAVE needs at least one step: {line}");
    }

    /// <summary>
    /// Tokenised in order rather than pattern-matched, because ROOT and HOLD are positional:
    /// they say how the program ends, so a step after either of them would never be reached.
    /// </summary>
    private static void ParseArpeggio(
        string sourceName, int lineNumber, string line, string body, Instrument instrument)
    {
        bool ended = false;

        foreach (string token in body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ended)
            {
                throw Error(sourceName, lineNumber,
                    $"INSTRUMENTS: ARP's ROOT/HOLD ends the program, so nothing may follow it: {line}");
            }

            switch (token.ToUpperInvariant())
            {
                case "ROOT":
                    instrument.ArpeggioEnding = ArpeggioEnding.Root;
                    ended = true;
                    continue;

                case "HOLD":
                    if (instrument.Arpeggio.Count == 0)
                    {
                        throw Error(sourceName, lineNumber,
                            $"INSTRUMENTS: ARP HOLD needs a note before it: {line}");
                    }

                    instrument.ArpeggioEnding = ArpeggioEnding.Hold;
                    ended = true;
                    continue;

                case "LOOP":
                    if (instrument.ArpeggioLoopPoint != 0)
                        throw Error(sourceName, lineNumber, $"INSTRUMENTS: ARP has two LOOP markers: {line}");

                    // Marks where the program goes back to, so it is a position rather than a
                    // step, and at least one note has to follow it.
                    instrument.ArpeggioLoopPoint = instrument.Arpeggio.Count;
                    continue;
            }

            var quoted = QuotedNoteRegex().Match(token);
            if (!quoted.Success || quoted.Length != token.Length)
                throw Error(sourceName, lineNumber, $"INSTRUMENTS: cannot parse ARP token '{token}': {line}");

            string text = quoted.Groups[1].Value;
            if (!NoteName.TryParse(text, out var parsed))
            {
                throw Error(sourceName, lineNumber,
                    $"INSTRUMENTS: ARP step '{text}' is not a note (expected e.g. \"C-1\"): {line}");
            }

            instrument.Arpeggio.Add(parsed);
        }

        if (!instrument.HasArpeggio)
            throw Error(sourceName, lineNumber, $"INSTRUMENTS: ARP needs at least one step: {line}");
    }

    private static void ParseFilter(
        string sourceName, int lineNumber, string line, string body, Instrument instrument)
    {
        int i = 0;
        while (i < body.Length)
        {
            if (char.IsWhiteSpace(body[i]))
            {
                i++;
                continue;
            }

            if (body[i] == '(')
            {
                int close = body.IndexOf(')', i);
                if (close < 0)
                    throw Error(sourceName, lineNumber, $"INSTRUMENTS: unclosed FLT group: {line}");

                var values = new List<int>();
                int sweepRate = 0;

                foreach (string token in body[(i + 1)..close]
                             .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    var hex = HexByteRegex().Match(token);
                    if (hex.Success && hex.Length == token.Length)
                    {
                        values.Add(Convert.ToInt32(hex.Groups[1].Value, 16));
                    }
                    else if (SignedRegex().IsMatch(token))
                    {
                        // A signed value in the group is the sweep rate. It rides here rather
                        // than being a step of its own because the sweep outlives the program.
                        sweepRate = int.Parse(token);
                        if (sweepRate is < -128 or > 127)
                        {
                            throw Error(sourceName, lineNumber,
                                $"INSTRUMENTS: FLT sweep rate must fit a signed byte: {line}");
                        }
                    }
                    else
                    {
                        throw Error(sourceName, lineNumber,
                            $"INSTRUMENTS: cannot parse FLT group token '{token}': {line}");
                    }
                }

                if (values.Count != 3)
                {
                    throw Error(sourceName, lineNumber,
                        "INSTRUMENTS: a FLT group is (mode+volume, resonance+routing, cutoff) " +
                        $"with an optional signed sweep rate: {line}");
                }

                instrument.Filter.Add(FilterStep.Setup(
                    (byte)values[0],
                    (byte)values[1],
                    CheckCutoff(sourceName, lineNumber, line, values[2]),
                    sweepRate));
                i = close + 1;
                continue;
            }

            var step = HexByteRegex().Match(body, i);
            if (!step.Success || step.Index != i)
                throw Error(sourceName, lineNumber, $"INSTRUMENTS: cannot parse FLT at: {body[i..]}");

            instrument.Filter.Add(FilterStep.Sweep(
                CheckCutoff(sourceName, lineNumber, line, Convert.ToInt32(step.Groups[1].Value, 16))));
            i = step.Index + step.Length;
        }

        if (instrument.Filter.Count == 0)
            throw Error(sourceName, lineNumber, $"INSTRUMENTS: FLT needs at least one step: {line}");
    }

    private static byte CheckCutoff(string sourceName, int lineNumber, string line, int value)
    {
        if (value is < FilterStep.MinCutoff or > FilterStep.MaxCutoff)
        {
            throw Error(sourceName, lineNumber,
                $"INSTRUMENTS: FLT cutoff must be ${FilterStep.MinCutoff:x2}..${FilterStep.MaxCutoff:x2}, " +
                $"got ${value:x2}: {line}");
        }

        return (byte)value;
    }

    private static void ParsePulse(
        string sourceName, int lineNumber, string line, string body, Instrument instrument)
    {
        var m = PulseBodyRegex().Match(body);
        if (!m.Success)
        {
            throw Error(sourceName, lineNumber,
                $"INSTRUMENTS: PW takes a width 0..15 and a signed delta, e.g. PW 8 +2: {line}");
        }

        int width = int.Parse(m.Groups[1].Value);
        int sweep = int.Parse(m.Groups[2].Value);

        bool continuous = false;
        foreach (string option in m.Groups[3].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (option.Equals("CONT", StringComparison.OrdinalIgnoreCase))
                continuous = true;
            else
                throw Error(sourceName, lineNumber, $"INSTRUMENTS: unknown PW option '{option}': {line}");
        }

        if (width is < 0 or > 15)
            throw Error(sourceName, lineNumber, $"INSTRUMENTS: PW width is in 1/16ths, so 0..15: {line}");
        if (sweep is < -128 or > 127)
            throw Error(sourceName, lineNumber, $"INSTRUMENTS: PW delta must fit a signed byte, -128..127: {line}");

        instrument.PulseWidth = width;
        instrument.PulseSweep = sweep;
        instrument.PulseContinuous = continuous;
    }

    private static PatternRow ParsePatternRow(string sourceName, int lineNumber, string rawLine)
    {
        string trimmedUpper = rawLine.Trim().ToUpperInvariant();
        if (trimmedUpper == "LS")
            return new LoopStartRow();
        if (trimmedUpper == "LE")
            return new LoopEndRow();
        if (trimmedUpper.StartsWith("LE", StringComparison.Ordinal) && trimmedUpper.Length > 2 &&
            trimmedUpper[2..].All(char.IsDigit))
        {
            int repeatCount = int.Parse(trimmedUpper[2..]);
            if (repeatCount < 1)
                throw Error(sourceName, lineNumber, $"LE repeat count must be at least 1: '{rawLine.Trim()}'.");
            return new LoopEndRow(repeatCount);
        }

        string stripped = rawLine.Replace(" ", "");
        var m = QuotedRowRegex().Match(stripped);
        if (!m.Success)
            throw Error(sourceName, lineNumber, $"cannot parse pattern row: {rawLine.Trim()}");

        string noteText = m.Groups[1].Value.ToUpperInvariant();
        int instrumentNumber = int.Parse(m.Groups[2].Value);

        return noteText switch
        {
            "..." => new HoldRow(),
            "---" => new NoteOffRow(),
            _ when SlideRow.TryParse(noteText, out var slide) => slide,
            _ when noteText.Length == 3 && noteText[0] is '1' or '2' && char.IsAsciiHexDigit(noteText[1]) =>
                throw Error(sourceName, lineNumber,
                    $"'{noteText}' is not a slide: 1xx goes up to 17F, 2xx down to 280."),
            _ => new NoteRow(ParseNoteOrThrow(sourceName, lineNumber, noteText), instrumentNumber),
        };
    }

    private static NoteName ParseNoteOrThrow(string sourceName, int lineNumber, string noteText)
    {
        if (!NoteName.TryParse(noteText, out var note))
            throw Error(sourceName, lineNumber, $"'{noteText}' is not a valid note (flats are not supported).");
        return note;
    }

    private static void ParseOrderTokens(
        string sourceName, ChannelOrder order, List<(int LineNumber, string Text)> rawLines)
    {
        var tokens = new List<(int LineNumber, string Text)>();
        foreach (var (lineNumber, text) in rawLines)
        {
            foreach (string rawToken in text.Split(','))
            {
                string token = rawToken.Trim();
                if (token.Length == 0)
                    continue;

                if (token.Length >= 2 && token[0] == '"' && token[^1] == '"')
                    token = token[1..^1];

                tokens.Add((lineNumber, token));
            }
        }

        int restartIdx = tokens.FindIndex(t => t.Text.Equals("restart", StringComparison.OrdinalIgnoreCase));
        int patternTokenCount = restartIdx >= 0 ? restartIdx : tokens.Count;

        for (int i = 0; i < patternTokenCount; i++)
        {
            var (lineNumber, token) = tokens[i];
            if (!int.TryParse(token, out int patternNumber))
                throw Error(sourceName, lineNumber, $"ORDER_CH{order.ChannelNumber}: invalid token '{token}'.");
            order.Tokens.Add(new PatternRefToken(patternNumber));
        }

        if (restartIdx >= 0)
        {
            if (restartIdx != tokens.Count - 2)
            {
                throw Error(sourceName, tokens[restartIdx].LineNumber,
                    $"ORDER_CH{order.ChannelNumber}: 'restart' must be followed by exactly one target position, as the last two tokens.");
            }

            var (targetLineNumber, targetText) = tokens[restartIdx + 1];
            if (!int.TryParse(targetText, out int restartPosition))
                throw Error(sourceName, targetLineNumber, $"ORDER_CH{order.ChannelNumber}: 'restart' target must be a number.");

            order.RestartPosition = restartPosition;
        }
    }

    private static List<string> SplitLines(string text) =>
        [.. text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')];

    private static PatternsFormatException Error(string sourceName, int lineNumber, string message) =>
        new(sourceName, lineNumber, message);

    [GeneratedRegex(@"^PAT(\d+)\s*:$", RegexOptions.IgnoreCase)]
    private static partial Regex PatternHeaderRegex();

    [GeneratedRegex(@"^ORDER_CH([1-3])\s*:$", RegexOptions.IgnoreCase)]
    private static partial Regex OrderHeaderRegex();

    [GeneratedRegex(@"^var\s+v_Song_Speed\s*=\s*(\d+)$")]
    private static partial Regex SongSpeedRegex();

    /// <summary>Funk speed, the second speed the ticks alternate with - see Song.FunkSpeed.</summary>
    [GeneratedRegex(@"^var\s+v_Song_Funk\s*=\s*(\d+)$")]
    private static partial Regex SongFunkRegex();

    [GeneratedRegex(@"^;\s*Instrument\s+(\d+)\s*:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InstrumentNameCommentRegex();

    [GeneratedRegex(@"^(\d+)\s*,\s*\$([0-9A-Fa-f]{1,2})\s*,\s*\$([0-9A-Fa-f]{1,2})\s*,\s*\$([0-9A-Fa-f]{1,2})\s*(.*)$")]
    private static partial Regex InstrumentLineRegex();

    [GeneratedRegex(@"^([A-Za-z]+)\s+(.+)$")]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"\$([0-9A-Fa-f]{1,2})")]
    private static partial Regex HexByteRegex();

    [GeneratedRegex(@"^(\d+)\s+([-+]?\d+)\s*(.*)$")]
    private static partial Regex PulseBodyRegex();

    [GeneratedRegex(@"^[-+]?\d+$")]
    private static partial Regex SignedRegex();

    [GeneratedRegex(@"""([^""]+)""")]
    private static partial Regex QuotedNoteRegex();

    [GeneratedRegex("""^"([^"]+)",(\d+)$""")]
    private static partial Regex QuotedRowRegex();
}
