using MagSid.Sid;

namespace MagPlay.C64;

/// <summary>
/// What a tune can read of the SID itself: voice 3's oscillator ($D41B) and envelope ($D41C) - for a
/// C64 that only records, with no chip of its own to ask. A whole SID clocked a cycle at a time
/// would do, but at some thirty times real time a tune reading OSC3 for its random numbers would
/// take forty seconds to be measured for twenty minutes. The reads need only the oscillators - all
/// three, as hard sync and ring modulation tie each to the one before - and voice 3's envelope, not
/// the filters; and with no hard sync these run any number of cycles at once, as reSID's own
/// delta clocking does, to the same values as a cycle at a time.
/// </summary>
internal sealed class VoiceThree
{
    /// <summary>The most cycles clocked at once: an oscillator's frequency times them must fit in an int.</summary>
    private const int LongestStep = 32768;

    private readonly WaveformGenerator _wave1 = new(), _wave2 = new(), _wave3 = new();
    private readonly EnvelopeGenerator _envelope = new();

    public VoiceThree()
    {
        // As on the chip: each oscillator synced and ring modulated by the one before, voice 1 by voice 3.
        _wave1.SetSyncSource(_wave3);
        _wave2.SetSyncSource(_wave1);
        _wave3.SetSyncSource(_wave2);
    }

    public void Write(int register, int value)
    {
        if (register >= 0x15)
            return;
        var wave = (register / 7) switch { 0 => _wave1, 1 => _wave2, _ => _wave3 };
        bool three = register / 7 == 2;
        switch (register % 7)
        {
            case 0: wave.WriteFreqLo(value); break;
            case 1: wave.WriteFreqHi(value); break;
            case 2: wave.WritePwLo(value); break;
            case 3: wave.WritePwHi(value); break;
            case 4:
                wave.WriteControlReg(value);
                if (three)
                    _envelope.WriteControlReg(value);
                break;
            case 5:
                if (three)
                    _envelope.WriteAttackDecay(value);
                break;
            case 6:
                if (three)
                    _envelope.WriteSustainRelease(value);
                break;
        }
    }

    /// <summary>The oscillators and the envelope run on <paramref name="cycles"/> cycles: at once
    /// without hard sync, a cycle at a time with it - the envelopes first, then the oscillators, then
    /// their sync, as the chip clocks them.</summary>
    public void Clock(long cycles)
    {
        while (cycles > 0)
        {
            // All but the last cycle at once, when no oscillator is synced: the last a cycle on its
            // own, so each oscillator's "MSB rose" is the last cycle's, as a cycle at a time leaves it -
            // an oscillator held by its test bit keeps it, and hard sync switched on later reads it.
            if (!_wave1.Syncs && !_wave2.Syncs && !_wave3.Syncs && cycles > 1)
            {
                int step = (int)Math.Min(cycles - 1, LongestStep);
                _envelope.Clock(step);
                _wave1.Clock(step);
                _wave2.Clock(step);
                _wave3.Clock(step);
                cycles -= step;
            }

            _envelope.Clock();
            _wave1.Clock();
            _wave2.Clock();
            _wave3.Clock();
            _wave1.Synchronize();
            _wave2.Synchronize();
            _wave3.Synchronize();
            cycles--;
        }
    }

    /// <summary>OSC3 at $1B, ENV3 at $1C.</summary>
    public int Read(int register) => register == 0x1B ? _wave3.ReadOsc() : _envelope.ReadEnv();
}
