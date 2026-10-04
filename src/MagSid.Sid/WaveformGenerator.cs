namespace MagSid.Sid;

/// <summary>
/// Direct port of reSID's wave.h/wave.cc. A 24-bit accumulator is the basis for
/// waveform generation; FREQ is added to it every cycle. The noise waveform is
/// taken from intermediate bits of a 23-bit shift register, clocked by
/// accumulator bit 19. Combined waveforms are approximated via real OSC3 sample
/// tables (see WaveformTables) rather than derived, exactly as in the original -
/// see wave.h's lengthy comment on why combined waveforms can't be modeled as a
/// simple bitwise AND.
/// </summary>
public sealed class WaveformGenerator
{
    /// <summary>
    /// The floating waveform DAC. Switch to <c>false</c> to go back to reSID 0.16's behaviour,
    /// where selecting no waveform drops the output to 0 at once - this one line is the whole of
    /// reversing it; everything it governs lives in this class, marked "floating DAC".
    ///
    /// On a real SID, selecting no waveform does not ground the waveform DAC: its input floats and
    /// holds the last value, which then fades away. Newer reSID versions model that, and so does
    /// this. Without it a waveform switched off mid-note - which every hard restart does, writing
    /// $08 to the control register while the previous note is still releasing - drops to 0 in one
    /// sample, and the step is heard as a click on every such note.
    /// </summary>
    public static bool FloatingWaveformOutput { get; set; } = true;

    // floating DAC: how long the held value lasts, then how often it loses bits while fading, in
    // cycles. The figures are libsidplayfp's reSIDfp ones, measured on warm 6581R3/R4 and 8580R5
    // chips; the 8580 holds far longer.
    private const int FloatingTtl6581 = 54_000;
    private const int FloatingFade6581 = 1_400;
    private const int FloatingTtl8580 = 800_000;
    private const int FloatingFade8580 = 50_000;

    private bool _is6581 = true;
    private int _floatingOutput;
    private int _floatingCycles;

    private WaveformGenerator? _syncSource;
    private WaveformGenerator? _syncDest;

    private bool _msbRising;

    private int _accumulator;
    private int _shiftRegister;

    private int _freq;
    private int _pw;

    private int _waveform;

    private int _test;
    private int _ringMod;
    private int _sync;

    private byte[] _waveST = WaveformTables.Wave6581_ST;
    private byte[] _wavePT = WaveformTables.Wave6581_PT;
    private byte[] _wavePS = WaveformTables.Wave6581_PS;
    private byte[] _wavePST = WaveformTables.Wave6581_PST;

    public WaveformGenerator()
    {
        _syncSource = this;
        SetChipModel(ChipModel.Mos6581);
        Reset();
    }

    public void SetSyncSource(WaveformGenerator source)
    {
        _syncSource = source;
        source._syncDest = this;
    }

    public void SetChipModel(ChipModel model)
    {
        _is6581 = model == ChipModel.Mos6581;

        if (model == ChipModel.Mos6581)
        {
            _waveST = WaveformTables.Wave6581_ST;
            _wavePT = WaveformTables.Wave6581_PT;
            _wavePS = WaveformTables.Wave6581_PS;
            _wavePST = WaveformTables.Wave6581_PST;
        }
        else
        {
            _waveST = WaveformTables.Wave8580_ST;
            _wavePT = WaveformTables.Wave8580_PT;
            _wavePS = WaveformTables.Wave8580_PS;
            _wavePST = WaveformTables.Wave8580_PST;
        }
    }

    /// <summary>SID clocking - 1 cycle.</summary>
    public void Clock()
    {
        AgeFloatingOutput(1);

        if (_test != 0)
        {
            return;
        }

        int accumulatorPrev = _accumulator;

        _accumulator = (_accumulator + _freq) & 0xffffff;

        _msbRising = (accumulatorPrev & 0x800000) == 0 && (_accumulator & 0x800000) != 0;

        // Shift noise register once for each time accumulator bit 19 is set high.
        if ((accumulatorPrev & 0x080000) == 0 && (_accumulator & 0x080000) != 0)
        {
            int bit0 = ((_shiftRegister >> 22) ^ (_shiftRegister >> 17)) & 0x1;
            _shiftRegister = ((_shiftRegister << 1) & 0x7fffff) | bit0;
        }
    }

    /// <summary>SID clocking - delta_t cycles.</summary>
    public void Clock(int deltaT)
    {
        AgeFloatingOutput(deltaT);

        if (_test != 0)
        {
            return;
        }

        int accumulatorPrev = _accumulator;

        int deltaAccumulator = deltaT * _freq;
        _accumulator = (_accumulator + deltaAccumulator) & 0xffffff;

        _msbRising = (accumulatorPrev & 0x800000) == 0 && (_accumulator & 0x800000) != 0;

        int shiftPeriod = 0x100000;

        while (deltaAccumulator != 0)
        {
            if (deltaAccumulator < shiftPeriod)
            {
                shiftPeriod = deltaAccumulator;

                if (shiftPeriod <= 0x080000)
                {
                    if (((_accumulator - shiftPeriod) & 0x080000) != 0 || (_accumulator & 0x080000) == 0)
                    {
                        break;
                    }
                }
                else
                {
                    if (((_accumulator - shiftPeriod) & 0x080000) != 0 && (_accumulator & 0x080000) == 0)
                    {
                        break;
                    }
                }
            }

            int bit0 = ((_shiftRegister >> 22) ^ (_shiftRegister >> 17)) & 0x1;
            _shiftRegister = ((_shiftRegister << 1) & 0x7fffff) | bit0;

            deltaAccumulator -= shiftPeriod;
        }
    }

    /// <summary>Synchronize oscillators - must be called after all oscillators have been
    /// clocked, since the oscillators operate in parallel.</summary>
    public void Synchronize()
    {
        if (_syncDest is null)
        {
            return;
        }

        // A special case occurs when a sync source is synced itself on the same
        // cycle as when its MSB is set high. In this case the destination will
        // not be synced.
        var source = _syncSource ?? this;
        if (_msbRising && _syncDest._sync != 0 && !(_sync != 0 && source._msbRising))
        {
            _syncDest._accumulator = 0;
        }
    }

    public void Reset()
    {
        _accumulator = 0;
        _shiftRegister = 0x7ffff8;
        _freq = 0;
        _pw = 0;

        _test = 0;
        _ringMod = 0;
        _sync = 0;

        _msbRising = false;

        _floatingOutput = 0;
        _floatingCycles = 0;
    }

    /// <summary>
    /// floating DAC: holds the value the waveform had when it was switched off, then fades it the
    /// way reSIDfp does - each step clears every bit that has no set bit above it, so the value
    /// sinks to 0 in a dozen steps rather than vanishing at once. Runs whatever the test bit says:
    /// the DAC fades on its own, and a hard restart holds TEST the whole time it is fading.
    /// </summary>
    private void AgeFloatingOutput(int cycles)
    {
        if (!FloatingWaveformOutput || _waveform != 0 || _floatingCycles == 0)
        {
            return;
        }

        _floatingCycles -= cycles;
        while (_floatingCycles <= 0 && _floatingOutput != 0)
        {
            _floatingOutput &= _floatingOutput >> 1;
            _floatingCycles += _is6581 ? FloatingFade6581 : FloatingFade8580;
        }

        if (_floatingOutput == 0)
        {
            _floatingCycles = 0;
        }
    }

    public void WriteFreqLo(int freqLo) => _freq = (_freq & 0xff00) | (freqLo & 0x00ff);

    public void WriteFreqHi(int freqHi) => _freq = ((freqHi << 8) & 0xff00) | (_freq & 0x00ff);

    public void WritePwLo(int pwLo) => _pw = (_pw & 0xf00) | (pwLo & 0x0ff);

    public void WritePwHi(int pwHi) => _pw = ((pwHi << 8) & 0xf00) | (_pw & 0x0ff);

    public void WriteControlReg(int control)
    {
        int waveformNext = (control >> 4) & 0x0f;

        // floating DAC: switching the waveform off keeps what it was putting out at this moment,
        // measured before the test bit below can clear the accumulator it comes from.
        if (FloatingWaveformOutput && waveformNext == 0 && _waveform != 0)
        {
            _floatingOutput = Output();
            _floatingCycles = _is6581 ? FloatingTtl6581 : FloatingTtl8580;
        }

        _waveform = waveformNext;
        _ringMod = control & 0x04;
        _sync = control & 0x02;

        int testNext = control & 0x08;

        if (testNext != 0)
        {
            // Test bit set: the accumulator and the shift register are both cleared.
            _accumulator = 0;
            _shiftRegister = 0;
        }
        else if (_test != 0)
        {
            // Test bit cleared: the accumulator starts counting, and the shift
            // register is reset to the value 0x7ffff8.
            _shiftRegister = 0x7ffff8;
        }

        _test = testNext;

        // The gate bit is handled by the EnvelopeGenerator.
    }

    public int ReadOsc() => Output() >> 4;

    /// <summary>12-bit waveform output. Selects one of 16 possible waveform combinations.</summary>
    public int Output() => _waveform switch
    {
        0x0 => OutputNone(),
        0x1 => OutputTriangle(),
        0x2 => OutputSawtooth(),
        0x3 => OutputSawtoothTriangle(),
        0x4 => OutputPulse(),
        0x5 => OutputPulseTriangle(),
        0x6 => OutputPulseSawtooth(),
        0x7 => OutputPulseSawtoothTriangle(),
        0x8 => OutputNoise(),
        // Waveform combinations including noise output zero after a few cycles - the
        // effects are not fully explored, and there is very little audible output
        // from these combinations on real hardware. Matches the source exactly.
        _ => 0,
    };

    // floating DAC: the held, fading value - or reSID 0.16's hard 0 with the switch off.
    private int OutputNone() => FloatingWaveformOutput ? _floatingOutput : 0x000;

    // Triangle: the upper 12 bits of the accumulator are used. The MSB creates the
    // falling edge by inverting the lower 11 bits, then is thrown away (left-shifted,
    // half the resolution, full amplitude). Ring modulation substitutes the MSB with
    // MSB EOR sync_source MSB.
    private int OutputTriangle()
    {
        var source = _syncSource ?? this;
        int msb = (_ringMod != 0 ? _accumulator ^ source._accumulator : _accumulator) & 0x800000;
        return ((msb != 0 ? ~_accumulator : _accumulator) >> 11) & 0xfff;
    }

    // Sawtooth: identical to the upper 12 bits of the accumulator.
    private int OutputSawtooth() => (_accumulator >> 12) & 0xfff;

    // Pulse: the upper 12 bits of the accumulator compared against the pulse width
    // register by a 12-bit digital comparator. The test bit holds the output at
    // 0xfff regardless of pulse width.
    private int OutputPulse() => (_test != 0 || (_accumulator >> 12) >= _pw) ? 0xfff : 0x000;

    // Noise: taken from intermediate bits of the 23-bit shift register, left-shifted
    // 4 times since waveform output is 12 bits.
    private int OutputNoise() =>
        ((_shiftRegister & 0x400000) >> 11) |
        ((_shiftRegister & 0x100000) >> 10) |
        ((_shiftRegister & 0x010000) >> 7) |
        ((_shiftRegister & 0x002000) >> 5) |
        ((_shiftRegister & 0x000800) >> 4) |
        ((_shiftRegister & 0x000080) >> 1) |
        ((_shiftRegister & 0x000010) << 1) |
        ((_shiftRegister & 0x000004) << 2);

    // Combined waveforms are approximated via real OSC3 sample tables, per wave.h's
    // detailed explanation of why the "AND'ed" mental model does not hold exactly.
    private int OutputSawtoothTriangle() => _waveST[OutputSawtooth()] << 4;

    private int OutputPulseTriangle() => (_wavePT[OutputTriangle() >> 1] << 4) & OutputPulse();

    private int OutputPulseSawtooth() => (_wavePS[OutputSawtooth()] << 4) & OutputPulse();

    private int OutputPulseSawtoothTriangle() => (_wavePST[OutputSawtooth()] << 4) & OutputPulse();
}
