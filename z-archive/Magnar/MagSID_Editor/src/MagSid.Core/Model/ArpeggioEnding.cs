namespace MagSid.Core.Model;

/// <summary>
/// What an instrument's note program does once it runs past its last step. This is the whole
/// difference between an arpeggio and a drum, so it is a first-class part of the program
/// rather than a flag tacked on the side.
/// </summary>
public enum ArpeggioEnding
{
    /// <summary>Start over - the note program cycles for as long as the note is held. A plain
    /// arpeggio. Emitted as <c>$00</c> followed by the program's own start offset.</summary>
    Loop,

    /// <summary>Stay on the last step. A one-shot pitch sweep - a kick's pitch falls and stays
    /// down. Emitted as <c>$00</c> followed by the last step's offset.</summary>
    Hold,

    /// <summary>Settle onto the note the pattern row actually played, and stay there. This is
    /// how a bass or a drum returns to the song after its transient: a frame or two of fixed
    /// pitch on noise, then the row's own note. Emitted as <c>$80</c>, which the player reads
    /// as "the row's note" without advancing past it.</summary>
    Root,
}
