using System.Text;
using MagSid.Core.Model;
using MagSid.Core.Validation;

namespace MagSid.Core.Serialization;

/// <summary>
/// Serializes a <see cref="Song"/> back to the exact Patterns.s grammar PatternsFileReader
/// reads, in a canonical form (LF line endings, uppercase keywords, lowercase hex, zero-padded
/// numbers) so the sibling assembly project's existing Startup\Tables.s pipeline keeps working
/// unchanged against files this editor produces. Refuses to write an invalid Song - see
/// SongValidator - rather than silently producing something the assembler would reject.
/// </summary>
public static class PatternsFileWriter
{
    public static void Write(Song song, string path)
    {
        string sourceName = Path.GetFileName(path);
        string text = Render(song, sourceName);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string Render(Song song, string sourceName = "Patterns.s")
    {
        var issues = SongValidator.Validate(song);
        if (issues.Count > 0)
            throw new PatternsFormatException(sourceName, 0, issues[0].Message);

        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');

        Line($"var v_Song_Speed = {song.Speed}");
        if (song.HasFunk)
            Line($"var v_Song_Funk = {song.FunkSpeed}");
        Line();

        Line("INSTRUMENTS:");
        Line("; nr, waveform, AD, SR [, WAVE $81 $41] [, ARP \"C-6\" ROOT] " +
             "[, FLT ($1f $f1 $08) $20] [, PW 8 +2] [, VIB $32 $10] [, NOHR]");
        foreach (var instrument in song.Instruments.OrderBy(i => i.Number))
        {
            // A whole-line comment rather than a section on the instrument's own line: the name
            // is purely for the editor's benefit, so it lives somewhere nothing but the editor
            // ever has to parse - see PatternsFileReader's matching InstrumentNameCommentRegex.
            if (instrument.Name.Length > 0)
                Line($"; Instrument {instrument.Number:D2} : {instrument.Name}");

            Line($"{instrument.Number:D2}, ${instrument.Waveform:x2}, " +
                 $"${instrument.AttackDecay:x2}, ${instrument.SustainRelease:x2}" +
                 RenderInstrumentSections(instrument));
        }
        Line("END:");
        Line();

        foreach (var pattern in song.Patterns.OrderBy(p => p.Number))
        {
            Line($"PAT{pattern.Number:D2}:");
            foreach (var row in pattern.Rows)
                Line(RenderRow(row));
            Line("END:");
            Line();
        }

        foreach (var order in song.ChannelOrders)
        {
            var parts = order.Tokens.Select(RenderToken).ToList();
            if (order.RestartPosition != 1)
            {
                parts.Add("\"restart\"");
                parts.Add(order.RestartPosition.ToString());
            }

            Line($"ORDER_CH{order.ChannelNumber}:");
            Line(string.Join(", ", parts));
            Line("END:");
            Line();
        }

        return sb.ToString();
    }

    /// <summary>
    /// The optional WAVE / ARP / FLT / PW / NOHR tail of an instrument line. Written in a fixed
    /// order even though the reader accepts any, so a file round-trips to itself byte for byte.
    /// A section is omitted entirely when the instrument does not use it - including PW at its
    /// default width 8 with no sweep, which is exactly what an absent PW means to both parsers.
    /// </summary>
    private static string RenderInstrumentSections(Instrument instrument)
    {
        var sb = new StringBuilder();

        if (instrument.Wavetable.Count > 0)
            sb.Append(", WAVE ").AppendJoin(' ', instrument.Wavetable.Select(b => $"${b:x2}"));

        if (instrument.HasArpeggio)
        {
            var steps = instrument.Arpeggio.Select(n => $"\"{n}\"").ToList();
            if (instrument.ArpeggioEnding == ArpeggioEnding.Loop && instrument.ArpeggioLoopPoint > 0)
                steps.Insert(instrument.ArpeggioLoopPoint, "LOOP");

            sb.Append(", ARP ").AppendJoin(' ', steps);

            // Loop is the ending you get by saying nothing, so it is the one that stays unwritten.
            if (instrument.ArpeggioEnding != ArpeggioEnding.Loop)
            {
                if (instrument.Arpeggio.Count > 0)
                    sb.Append(' ');
                sb.Append(instrument.ArpeggioEnding == ArpeggioEnding.Root ? "ROOT" : "HOLD");
            }
        }

        if (instrument.Filter.Count > 0)
            sb.Append(", FLT ").AppendJoin(' ', instrument.Filter.Select(RenderFilterStep));

        if (!instrument.HasDefaultPulse)
        {
            sb.Append($", PW {instrument.PulseWidth} {instrument.PulseSweep:+0;-0;+0}");
            if (instrument.PulseContinuous)
                sb.Append(" CONT");
        }

        if (instrument.HasVibrato)
            sb.Append($", VIB ${instrument.VibratoDelaySpeed:x2} ${instrument.VibratoDepth:x2}");

        if (instrument.NoHardRestart)
            sb.Append(", NOHR");

        if (instrument.SoftHardRestart)
            sb.Append(", SOFTHR");

        return sb.ToString();
    }

    private static string RenderFilterStep(FilterStep step)
    {
        if (!step.IsSetup)
            return $"${step.Cutoff:x2}";

        // A rate of 0 is what a group without one means, so it stays unwritten.
        string rate = step.SweepRate != 0 ? $" {step.SweepRate:+0;-0;+0}" : "";
        return $"(${step.ModeVolume!.Value:x2} ${step.ResonanceRouting!.Value:x2} " +
               $"${step.Cutoff:x2}{rate})";
    }

    private static string RenderRow(PatternRow row) => row switch
    {
        NoteRow(var note, var inst) => $"\"{note}\", {inst:D2}",
        HoldRow => "\"...\", 00",
        NoteOffRow => "\"---\", 00",
        SlideRow slide => $"\"{slide.Text}\", 00",
        LoopStartRow => "LS",
        LoopEndRow(null) => "LE",
        LoopEndRow(var count) => $"LE{count:D2}",
        _ => throw new NotSupportedException($"Unknown pattern row type: {row.GetType()}"),
    };

    private static string RenderToken(OrderToken token) => token switch
    {
        PatternRefToken(var number) => number.ToString(),
        _ => throw new NotSupportedException($"Unknown order token type: {token.GetType()}"),
    };
}
