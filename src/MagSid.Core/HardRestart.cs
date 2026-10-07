namespace MagSid.Core;

/// <summary>
/// How the hard restart on a note's trigger frame leaves the control register - the one switch
/// for it. The C# playback engine, the 6502 player the .sid export builds and the size figures all
/// read this, and the golden images for each setting sit side by side, so flipping it is the whole
/// of going back.
///
/// <list type="bullet">
/// <item><c>true</c>: gate off with the waveform kept, the GoatTracker way, for every
/// instrument. The old note fades through the frame rather than being cut, and the oscillator is
/// not reset. Two bytes more player.</item>
/// <item><c>false</c>: $08 - gate off, TEST on, no waveform: the waveform stops at once and the
/// oscillator is held at zero. Except after an instrument with
/// <see cref="Model.Instrument.SoftHardRestart"/>, which gets the gentle way on its own.</item>
/// </list>
///
/// AD $00 and SR $07 are written either way.
/// </summary>
public static class HardRestart
{
    public static readonly bool KeepsWaveform = false;
}
