namespace MagPlay.Score;

/// <summary>
/// What a row - or a table step - does besides its note. The first fifteen are GoatTracker's
/// commands, and keep its numbers in <see cref="Label"/> so the Retro view reads as a tracker
/// would; the rest are what other players do that GoatTracker has no command for.
///
/// Pitch effects - portamento, tone portamento, vibrato, arpeggio - last the row they are on, as
/// in every tracker since ProTracker; the rest set something once.
/// </summary>
public abstract record Effect
{
    /// <summary>The effect in a tracker's column: a command and its value.</summary>
    public abstract string Label { get; }
}

/// <summary>1xx/2xx: the frequency moves by <paramref name="Speed"/> a call - up when positive.</summary>
public sealed record Portamento(int Speed) : Effect
{
    public override string Label => Speed >= 0 ? $"1{Hex(Speed)}" : $"2{Hex(-Speed)}";

    internal static string Hex(int value) => value <= 0xFF ? value.ToString("X2") : value.ToString("X4");
}

/// <summary>3xx: the pitch glides to the row's note by <paramref name="Speed"/> a call instead of
/// starting it; 0 ties the note on at once - legato.</summary>
public sealed record TonePortamento(int Speed) : Effect
{
    public override string Label => $"3{Portamento.Hex(Speed)}";
}

/// <summary>4xy: vibrato, <paramref name="Speed"/> calls a quarter period, <paramref name="Depth"/> a call.</summary>
public sealed record VibratoEffect(int Speed, int Depth) : Effect
{
    public override string Label => $"4{Speed:X}{Portamento.Hex(Depth)}";
}

/// <summary>5xx: attack and decay.</summary>
public sealed record SetAttackDecay(int Value) : Effect
{
    public override string Label => $"5{Value:X2}";
}

/// <summary>6xx: sustain and release.</summary>
public sealed record SetSustainRelease(int Value) : Effect
{
    public override string Label => $"6{Value:X2}";
}

/// <summary>7xx: the control byte - waveform, sync, ring, test; the gate stays the voice's.</summary>
public sealed record SetControl(int Value) : Effect
{
    public override string Label => $"7{Value:X2}";
}

/// <summary>8xx: the wave program carries on from this step.</summary>
public sealed record SetWaveProgram(int Step) : Effect
{
    public override string Label => $"8{Step:X2}";
}

/// <summary>9xx: the pulse program carries on from this step.</summary>
public sealed record SetPulseProgram(int Step) : Effect
{
    public override string Label => $"9{Step:X2}";
}

/// <summary>Axx: the filter program carries on from this step.</summary>
public sealed record SetFilterProgram(int Step) : Effect
{
    public override string Label => $"A{Step:X2}";
}

/// <summary>Bxx: resonance in the high nibble, the voices routed through the filter in the low.</summary>
public sealed record FilterControl(int Value) : Effect
{
    public override string Label => $"B{Value:X2}";
}

/// <summary>Cxx: the filter's cutoff, all eleven bits.</summary>
public sealed record FilterCutoff(int Value) : Effect
{
    public override string Label => $"C{Portamento.Hex(Value)}";
}

/// <summary>Dxx: the master volume, 0-15.</summary>
public sealed record MasterVolume(int Value) : Effect
{
    public override string Label => $"D{Value:X2}";
}

/// <summary>Exy: funk tempo - rows alternate <paramref name="First"/> and <paramref name="Second"/>
/// calls, on every voice.</summary>
public sealed record FunkTempo(int First, int Second) : Effect
{
    public override string Label => $"E{First:X}{Second:X}";
}

/// <summary>Fxx: calls a row - on every voice, or on this one alone.</summary>
public sealed record Tempo(int Calls, bool ThisVoiceOnly = false) : Effect
{
    public override string Label => $"F{(ThisVoiceOnly ? Calls | 0x80 : Calls):X2}";
}

/// <summary>The filter's type - low, band, high pass and voice 3 off - the high nibble of $D418.</summary>
public sealed record FilterMode(int Value) : Effect
{
    public override string Label => $"M{Value:X2}";
}

/// <summary>Arpeggio: the note, then <paramref name="First"/> and <paramref name="Second"/> semitones
/// above it, a call each, round again - as ProTracker's 0xy and many C64 players.</summary>
public sealed record Arpeggio(int First, int Second) : Effect
{
    public override string Label => $"0{First:X}{Second:X}";
}

/// <summary>The row's note starts this many calls into the row.</summary>
public sealed record NoteDelay(int Calls) : Effect
{
    public override string Label => $"ND{Calls:X}";
}

/// <summary>The gate goes off this many calls into the row.</summary>
public sealed record NoteCut(int Calls) : Effect
{
    public override string Label => $"NC{Calls:X}";
}

/// <summary>Added to the note's frequency: a voice tuned a little off the table.</summary>
public sealed record Detune(int Amount) : Effect
{
    public override string Label => $"DT{(Amount < 0 ? "-" : "+")}{Math.Abs(Amount):X}";
}

/// <summary>The frequency, all sixteen bits, outright.</summary>
public sealed record SetFrequency(int Value) : Effect
{
    public override string Label => $"F={Value:X4}";
}

/// <summary>The pulse width, all twelve bits, outright.</summary>
public sealed record SetPulse(int Value) : Effect
{
    public override string Label => $"P={Value:X3}";
}

/// <summary>Cycles from this call to the next, from now on: a CIA timer reprogrammed.</summary>
public sealed record CallRate(int Cycles) : Effect
{
    public override string Label => $"CR{Cycles:X4}";
}

/// <summary>A digi starts: the score's sample <paramref name="Sample"/> streams on
/// <paramref name="Stream"/> - streams play side by side, a new one on a stream replacing the old.
/// A negative sample stops the stream.</summary>
public sealed record PlaySample(int Sample, int Stream = 0) : Effect
{
    public override string Label => Sample < 0 ? $"S{Stream}--" : $"S{Stream}{Sample:X2}";
}
