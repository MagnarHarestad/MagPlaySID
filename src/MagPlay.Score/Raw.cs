namespace MagPlay.Score;

/// <summary>
/// What a tune writes that the score's music does not say, given outright: the layer that makes
/// a transcription lossless before anything in it is understood. Writes made outside the calls -
/// a tune's init, a digi streamed from an NMI, a tune that plays from its main loop - are kept on
/// their own cycles. Within a call, a register the music gets wrong is written as the tune wrote
/// it, and the music's own writes to it that call are left out.
///
/// The music carries on from what the raw layer leaves on the chip, as it does after a
/// <see cref="RawStep"/>. How much of a tune is in here is the measure of how much of it the
/// score does not yet describe.
/// </summary>
public sealed class RawLayer
{
    /// <summary>Writes outside any call, each on its cycle.</summary>
    public List<TimedWrite> Between { get; } = [];

    /// <summary>The calls, by number from 0, in which some registers are written as given.</summary>
    public Dictionary<int, RawCall> Calls { get; } = [];

    public bool IsEmpty => Between.Count == 0 && Calls.Count == 0;
}

/// <summary>One call's raw writes. The registers in <paramref name="Registers"/> - a bit each,
/// $00-$18 - are written only as <paramref name="Writes"/> say; none of them, when it has none.</summary>
public sealed record RawCall(int Registers, IReadOnlyList<RawWrite> Writes)
{
    public bool Replaces(int register) => (Registers >> register & 1) != 0;
}

/// <summary>A write <paramref name="Offset"/> cycles after its call starts - where the tune made it.</summary>
public readonly record struct RawWrite(int Offset, int Register, int Value);
