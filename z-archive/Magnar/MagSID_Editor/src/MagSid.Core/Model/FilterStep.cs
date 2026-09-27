namespace MagSid.Core.Model;

/// <summary>
/// One frame of an instrument's filter program, mirroring the FLT section in the
/// INSTRUMENTS: block and the flt_data byte stream the assembler builds from it.
///
/// Two shapes, distinguished by <see cref="IsSetup"/>:
/// a bare sweep step, which only moves the cutoff high byte ($D416), and a setup step,
/// which also writes the filter mode plus master volume ($D418) and the resonance plus
/// voice routing ($D417). A program normally opens with a setup step and continues with
/// sweep steps.
///
/// <see cref="Cutoff"/> is restricted to $01..$FE because the assembled stream spends
/// $FF on the setup marker and $00 on the end marker. That is not a loss worth working
/// around: both excluded values are degenerate cutoff settings.
/// </summary>
public readonly record struct FilterStep
{
    public const byte MinCutoff = 0x01;
    public const byte MaxCutoff = 0xFE;

    /// <summary>$D418 - filter mode in the high nibble, master volume in the low. Null on a
    /// sweep step.
    ///
    /// Bit 7 (3OFF, disconnect voice 3 from the output) is always 0 here - see
    /// <see cref="Setup"/>. This player never uses 3OFF, and GoatTracker users habitually write
    /// this byte as 9x/bx/dx/fx anyway (there that bit means something else, a file-format
    /// marker, not the SID's own 3OFF), so a byte arriving with it set means the same mode and
    /// volume as the same byte without it.</summary>
    public byte? ModeVolume { get; private init; }

    /// <summary>$D417 - resonance in the high nibble, which voices pass through the filter in
    /// the low. Null on a sweep step.</summary>
    public byte? ResonanceRouting { get; private init; }

    /// <summary>
    /// How fast the cutoff sweeps from here on, signed, 0 for no sweep. Only a setup step
    /// carries one.
    ///
    /// The sweep is a free-running triangle that keeps going long after the program has
    /// finished, so it is a property of the filter's configuration rather than a step of its
    /// own - which is also why it rides along in the setup record instead of having a marker
    /// in the byte stream.
    /// </summary>
    public int SweepRate { get; private init; }

    /// <summary>$D416 - the cutoff frequency's high byte, written by every step.</summary>
    public byte Cutoff { get; private init; }

    public bool IsSetup => ModeVolume.HasValue;

    public static FilterStep Sweep(byte cutoff) => new() { Cutoff = cutoff };

    /// <summary>Masks off bit 7 of <paramref name="modeVolume"/> (3OFF) unconditionally, so
    /// every step this factory produces already carries the invariant <see cref="ModeVolume"/>
    /// documents - regardless of whether the byte came from a hand-typed GoatTracker-habit
    /// value, an existing file authored the same way, or anywhere else. One place, so nothing
    /// downstream (playback, export, the editor's grid) has to re-derive or repeat it.</summary>
    public static FilterStep Setup(byte modeVolume, byte resonanceRouting, byte cutoff,
                                  int sweepRate = 0) => new()
    {
        ModeVolume = (byte)(modeVolume & 0x7f),
        ResonanceRouting = resonanceRouting,
        Cutoff = cutoff,
        SweepRate = sweepRate,
    };
}
