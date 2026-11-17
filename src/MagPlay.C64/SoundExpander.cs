namespace MagPlay.C64;

/// <summary>
/// The SFX Sound Expander, or the FM-YAM, in the expansion port: an FM chip - Yamaha's OPL, YM3526 or
/// YM3812 - in I/O 2, its register chosen at $DF40, written at $DF50 and its status read at $DF60, as
/// the v5 SID format's FM OPL flag asks for. The C64 keeps its two timers, which a tune may wait on;
/// the sound is the engine's (<see cref="FmSound"/>).
/// </summary>
public sealed class SoundExpander(double c64ClockHz)
{
    public const int AddressPort = 0xDF40;
    public const int DataPort = 0xDF50;
    public const int StatusPort = 0xDF60;

    /// <summary>The chip's clock: the cartridge's 3.58 MHz crystal.</summary>
    private const double ChipClockHz = 3579545;

    /// <summary>C64 cycles to a step of timer 1 - 80 µs, 288 of the chip's clocks - and of timer 2, four times that.</summary>
    private readonly double _step1 = c64ClockHz * 288 / ChipClockHz;
    private readonly double _step2 = c64ClockHz * 288 * 4 / ChipClockHz;

    /// <summary>The register the next write to the data port goes to.</summary>
    public int Latch { get; private set; }

    private int _preset1, _preset2;

    /// <summary>The channels keyed on - B0-B8's bit 5 - and the rhythm's drums - BD's - with the cycle
    /// the last of them went off.</summary>
    private int _keyed, _drums;
    private long _lastOff;

    /// <summary>Seconds an FM note may ring on after its key goes off: its release.</summary>
    private const double ReleaseSeconds = 2;

    /// <summary>The cycle from which the FM chip has been silent, a release after its last note went
    /// off; null while a note is keyed.</summary>
    public long? QuietSince() => _keyed != 0 || _drums != 0 || _digi ? null : _lastOff + (long)(ReleaseSeconds * c64ClockHz);

    /// <summary>Whether a digi is armed (<see cref="FmSound"/>): sounding, as a keyed note is.</summary>
    private bool _digi;
    private bool _run1, _run2, _mask1, _mask2, _flag1, _flag2;
    private double _due1, _due2;

    public void WriteAddress(int value) => Latch = value & 0xFF;

    /// <summary>A write to the register chosen: the timers' own taken here.</summary>
    public void WriteData(long cycle, int value)
    {
        Update(cycle);
        if (Latch == 0x01)
        {
            bool digi = (value & 0xFF) == FmSound.DigiArm;
            if (_digi && !digi)
                _lastOff = cycle;
            _digi = digi;
        }
        if (Latch is >= 0xB0 and <= 0xB8)
            Key(cycle, ref _keyed, 1 << (Latch - 0xB0), (value & 0x20) != 0);
        else if (Latch == 0xBD)
        {
            bool rhythm = (value & 0x20) != 0;
            int drums = rhythm ? value & 0x1F : 0;
            if (drums != _drums && drums == 0)
                _lastOff = cycle;
            _drums = drums;
        }
        switch (Latch)
        {
            case 0x02:
                _preset1 = value & 0xFF;
                break;
            case 0x03:
                _preset2 = value & 0xFF;
                break;
            case 0x04:
                if ((value & 0x80) != 0)
                {
                    // The flags reset; the rest of the byte is let be.
                    _flag1 = _flag2 = false;
                    break;
                }
                _mask1 = (value & 0x40) != 0;
                _mask2 = (value & 0x20) != 0;
                bool start1 = (value & 0x01) != 0, start2 = (value & 0x02) != 0;
                if (start1 && !_run1)
                    _due1 = cycle + (_step1 * (256 - _preset1));
                if (start2 && !_run2)
                    _due2 = cycle + (_step2 * (256 - _preset2));
                (_run1, _run2) = (start1, start2);
                break;
        }
    }

    private void Key(long cycle, ref int keyed, int bit, bool on)
    {
        int was = keyed;
        keyed = on ? keyed | bit : keyed & ~bit;
        if (was != 0 && keyed == 0)
            _lastOff = cycle;
    }

    /// <summary>The status: bit 7 when either flag is up, bit 6 timer 1's, bit 5 timer 2's - and the
    /// 0x06 an OPL2 reads with them.</summary>
    public byte ReadStatus(long cycle)
    {
        Update(cycle);
        int status = 0x06;
        if (_flag1)
            status |= 0x40;
        if (_flag2)
            status |= 0x20;
        if (_flag1 || _flag2)
            status |= 0x80;
        return (byte)status;
    }

    /// <summary>The timers run on to <paramref name="cycle"/>: one that overflows raises its flag - unless
    /// masked - and counts again from its preset.</summary>
    private void Update(long cycle)
    {
        if (_run1 && cycle >= _due1)
        {
            double period = _step1 * (256 - _preset1);
            _due1 += period * (Math.Floor((cycle - _due1) / period) + 1);
            if (!_mask1)
                _flag1 = true;
        }
        if (_run2 && cycle >= _due2)
        {
            double period = _step2 * (256 - _preset2);
            _due2 += period * (Math.Floor((cycle - _due2) / period) + 1);
            if (!_mask2)
                _flag2 = true;
        }
    }
}

/// <summary>
/// The FM chip's sound, sample by sample at the engine's rate: an OPL2 as Nuked OPL3 plays it in its
/// compatible mode. Its waveforms only once the tune enables them, as an OPL2's are - a YM3526 has
/// none, and a tune for it writes none.
/// </summary>
public sealed class FmSound
{
    private readonly NukedOpl3 _chip;
    private readonly byte[] _waveforms = new byte[0x100];
    private bool _waveformsOn;
    private readonly short[] _pair = new short[2];

    /// <summary>The test register's value that arms the digi: 0x04 written to $01.</summary>
    public const int DigiArm = 0x04;

    /// <summary>
    /// A digi played through the FM chip. A real OPL2 sounds the bytes a tune writes to channel 1's and
    /// 2's frequency ($A0, $A1) once 0x04 is in its test register; Nuked OPL3 does not. While armed - as
    /// SIDKick-Pico, and Wilfred Bos's SID-Device after it, do - those bytes are 8-bit samples, held
    /// until the next, and they are the FM chip's sound: its own is not made. Any other value in the test
    /// register lets the chip sound again, from where it stopped.
    /// </summary>
    private bool _digi;
    private readonly int[] _digiSample = new int[2];

    /// <summary>A digi byte, 0-255 around 128, made a sample: as loud as SID-Device makes it.</summary>
    private const int DigiScale = 64;

    public FmSound(int sampleRate) => _chip = new NukedOpl3(sampleRate);

    public void Write(int register, int value)
    {
        register &= 0xFF;
        byte data = (byte)value;
        if (register == 0x01)
            _digi = data == DigiArm;
        if (_digi && register is 0xA0 or 0xA1)
        {
            _digiSample[register - 0xA0] = (data - 128) * DigiScale;
            return;
        }
        if (register == 0x01)
        {
            bool on = (data & 0x20) != 0;
            if (on != _waveformsOn)
            {
                _waveformsOn = on;
                for (int r = 0xE0; r <= 0xF5; r++)
                    _chip.WriteReg(r, on ? _waveforms[r] : (byte)0);
            }
            return;
        }
        if (register is >= 0xE0 and <= 0xF5)
        {
            _waveforms[register] = data;
            if (!_waveformsOn)
                return;
        }
        _chip.WriteReg(register, data);
    }

    /// <summary>The next sample, mono - an OPL2 has one output.</summary>
    public short Next()
    {
        if (_digi)
            return (short)Math.Clamp(_digiSample[0] + _digiSample[1], short.MinValue, short.MaxValue);
        _chip.GenerateResampled(_pair);
        return _pair[0];
    }
}
