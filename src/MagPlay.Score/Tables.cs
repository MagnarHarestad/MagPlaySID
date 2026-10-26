namespace MagPlay.Score;

/// <summary>A step of a wave, pulse or filter program. A program runs a step at a time - each
/// lasting the calls it says - until a jump sends it elsewhere or ends it.</summary>
public abstract record TableStep;

/// <summary>The program goes on at <paramref name="To"/> - a loop - or, at -1, stops where it is.</summary>
public sealed record JumpStep(int To) : TableStep;

public enum PitchKind
{
    /// <summary>Semitones from the row's note: an arpeggio, a chord.</summary>
    Relative,

    /// <summary>A note of its own, whatever the row says: a drum's fixed pitch.</summary>
    Absolute,

    /// <summary>A frequency, all sixteen bits: a pitch no note has.</summary>
    Frequency,

    /// <summary>A frequency added to the row's note's: a step of a vibrato or a slide, in the
    /// units a player adds them in.</summary>
    Offset,
}

public readonly record struct Pitch(PitchKind Kind, int Value)
{
    public static Pitch Relative(int semitones) => new(PitchKind.Relative, semitones);

    public static Pitch Absolute(int note) => new(PitchKind.Absolute, note);

    public static Pitch Frequency(int value) => new(PitchKind.Frequency, value);

    public static Pitch Offset(int value) => new(PitchKind.Offset, value);
}

/// <summary>A wave program step: a control byte, a pitch, or both, held for <paramref name="Calls"/>.
/// The control byte's gate bit can take the gate off but not put it on - the gate is the note's.</summary>
public sealed record WaveStep(int? Control = null, Pitch? Pitch = null, int Calls = 1) : TableStep;

/// <summary>A pulse program step: the width set, then <paramref name="Add"/> added each call, for
/// <paramref name="Calls"/> calls.</summary>
public sealed record PulseStep(int? Width = null, int Add = 0, int Calls = 1) : TableStep;

/// <summary>A filter program step: any of cutoff, resonance and routing, and type set, then
/// <paramref name="Add"/> added to the cutoff each call, for <paramref name="Calls"/> calls.</summary>
public sealed record FilterStep(int? Cutoff = null, int? Control = null, int? Mode = null, int Add = 0, int Calls = 1) : TableStep;

/// <summary>A step that runs an effect - GoatTracker's wavetable commands.</summary>
public sealed record EffectStep(Effect Effect) : TableStep;

/// <summary>
/// Register writes, in order, for one call: what makes the score able to say anything. In a
/// voice's wave program registers 0-6 are that voice's own; 0x15-0x18, the filter and volume, are
/// the chip's. The player carries on from the values written, so effects after a raw step move
/// on from where it left the voice.
/// </summary>
public sealed record RawStep(IReadOnlyList<(int Register, int Value)> Writes, int Calls = 1) : TableStep;
