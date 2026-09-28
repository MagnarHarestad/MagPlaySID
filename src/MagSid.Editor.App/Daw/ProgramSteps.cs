using MagSid.Core.Model;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// Turns an instrument's three programs into rows of glyphs.
///
/// This is the whole difference between the two views. The retro editor shows <c>$09 $41 $40 $41</c>
/// and is right to - it is what the bytes are. This says noise, pulse, hold, pulse, which is what
/// they do. The hex rides along as each step's label, so nothing is lost.
/// </summary>
internal static class ProgramSteps
{
    /// <summary>One SID control byte per frame. $00 holds whatever was last written, which is how a
    /// program sustains without a loop.</summary>
    public static IReadOnlyList<ProgramStep> Wavetable(Instrument instrument)
    {
        if (instrument.Wavetable.Count == 0)
        {
            // An instrument with no WAVE section is the one-step program {waveform | gate}: the
            // emitter writes exactly that, so showing it is honest rather than an empty lane.
            byte implied = (byte)(instrument.Waveform | 0x01);
            return [new ProgramStep($"${implied:X2}", GlyphFor(implied), DawPalette.Envelope)];
        }

        return [.. instrument.Wavetable.Select(b =>
            new ProgramStep($"${b:X2}", GlyphFor(b), b == 0 ? DawPalette.TextDim : DawPalette.Envelope))];
    }

    /// <summary>Absolute note ids, then whatever the program does when it runs out: back to its own
    /// start, back to a loop point, stay on the last step, or return to the row's own note.</summary>
    public static IReadOnlyList<ProgramStep> NoteProgram(Instrument instrument)
    {
        var steps = new List<ProgramStep>();
        int? previous = null;

        for (int i = 0; i < instrument.Arpeggio.Count; i++)
        {
            var note = instrument.Arpeggio[i];
            int index = note.TableIndex;

            var glyph = previous is null || index == previous
                ? StepGlyph.Flat
                : index > previous ? StepGlyph.Rise : StepGlyph.Fall;

            bool isLoopTarget = instrument.ArpeggioEnding == ArpeggioEnding.Loop
                                && instrument.ArpeggioLoopPoint == i
                                && i > 0;

            steps.Add(new ProgramStep(
                note.ToString(),
                isLoopTarget ? StepGlyph.Root : glyph,
                isLoopTarget ? DawPalette.Filter : DawPalette.Oscillator));

            previous = index;
        }

        // The ending is its own cell: it is a real byte pair in the pool and it is what separates an
        // arpeggio from a drum, so it should not be a property hidden somewhere off to the side.
        steps.Add(instrument.ArpeggioEnding switch
        {
            ArpeggioEnding.Root => new ProgramStep("ROOT", StepGlyph.Root, DawPalette.Filter, Marker: true),
            ArpeggioEnding.Hold => new ProgramStep("HOLD", StepGlyph.Hold, DawPalette.Filter, Marker: true),
            _ => new ProgramStep(
                instrument.ArpeggioLoopPoint > 0 ? $"LOOP {instrument.ArpeggioLoopPoint}" : "LOOP",
                StepGlyph.Hold, DawPalette.Filter, Marker: true),
        });

        return steps;
    }

    /// <summary>A plain step moves the cutoff; a setup record carries mode+volume, resonance and
    /// routing, the sweep rate and a cutoff all at once.</summary>
    public static IReadOnlyList<ProgramStep> Filter(Instrument instrument)
    {
        var steps = new List<ProgramStep>();
        int? previous = null;

        foreach (var step in instrument.Filter)
        {
            if (step.IsSetup)
            {
                steps.Add(new ProgramStep($"${step.Cutoff:X2}", StepGlyph.Setup, DawPalette.Filter, Marker: true));
            }
            else
            {
                var glyph = previous is null || step.Cutoff == previous
                    ? StepGlyph.Flat
                    : step.Cutoff > previous ? StepGlyph.Rise : StepGlyph.Fall;
                steps.Add(new ProgramStep($"${step.Cutoff:X2}", glyph, DawPalette.Filter));
            }

            previous = step.Cutoff;
        }

        return steps;
    }

    /// <summary>Which waveform bits a control byte has set. Noise wins the label when several are
    /// on, because it is the one that changes the character most.</summary>
    private static StepGlyph GlyphFor(byte control) => (control & 0xF0) switch
    {
        0 => StepGlyph.Flat,
        var w when (w & 0x80) != 0 => StepGlyph.Noise,
        var w when (w & 0x40) != 0 => StepGlyph.Pulse,
        var w when (w & 0x20) != 0 => StepGlyph.Saw,
        _ => StepGlyph.Triangle,
    };
}
