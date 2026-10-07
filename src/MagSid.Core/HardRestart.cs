namespace MagSid.Core;

/// <summary>
/// How the hard restart on a note's trigger frame leaves the control register - the one switch
/// for it. The C# playback engine, the 6502 player the .sid export builds and the size figures all
/// read this, and the golden images for each setting sit side by side, so flipping it is the whole
/// of going back.
///
/// <list type="bullet">
/// <item><c>true</c>: gate off with the waveform kept, the GoatTracker way. The old note fades
/// through the frame rather than being cut, and the oscillator is not reset - an instrument that
/// wants that puts $09 (Silence) first in its wavetable. Two bytes more player.</item>
/// <item><c>false</c>: $08 - gate off, TEST on, no waveform. The player as it was up to
/// 2026-10-07: the waveform stops at once and the oscillator is held at zero.</item>
/// </list>
///
/// AD $00 and SR $07 are written either way.
/// </summary>
public static class HardRestart
{
    public static readonly bool KeepsWaveform = true;
}
