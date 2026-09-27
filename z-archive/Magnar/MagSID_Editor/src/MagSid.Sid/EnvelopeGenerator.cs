namespace MagSid.Sid;

/// <summary>
/// Direct port of reSID's envelope.h/envelope.cc (Dag Lem, simonowen/resid fork).
/// A 15-bit counter implements the envelope rates by dividing the clock to the
/// envelope counter by the currently selected rate period; a second counter
/// implements the exponential envelope decay by further dividing that clock.
/// All comments below are carried over from the original source - they document
/// real, verified SID chip quirks (the ADSR delay bug, the zero-freeze lock),
/// not implementation notes about this port.
/// </summary>
public sealed class EnvelopeGenerator
{
    public enum EnvelopeState { Attack, DecaySustain, Release }

    // Rate counter periods, from the Envelope Rates table in the Programmer's
    // Reference Guide, verified by sampling ENV3 (see envelope.cc for the full
    // derivation notes).
    private static readonly int[] RateCounterPeriod =
    [
        9, 32, 63, 95, 149, 220, 267, 313,
        392, 977, 1954, 3126, 3907, 11720, 19532, 31251,
    ];

    // The 16 selectable sustain levels - both the low and high 4 bits of the
    // envelope counter are compared to the 4-bit sustain value.
    private static readonly int[] SustainLevelTable =
    [
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
    ];

    private int _rateCounter;
    private int _ratePeriod;
    private int _exponentialCounter;
    private int _exponentialCounterPeriod;
    private int _envelopeCounter;
    private bool _holdZero;

    private int _attack;
    private int _decay;
    private int _sustain;
    private int _release;

    private int _gate;

    private EnvelopeState _state;

    public EnvelopeGenerator() => Reset();

    public void Reset()
    {
        _envelopeCounter = 0;

        _attack = 0;
        _decay = 0;
        _sustain = 0;
        _release = 0;

        _gate = 0;

        _rateCounter = 0;
        _exponentialCounter = 0;
        _exponentialCounterPeriod = 1;

        _state = EnvelopeState.Release;
        _ratePeriod = RateCounterPeriod[_release];
        _holdZero = true;
    }

    /// <summary>SID clocking - 1 cycle.</summary>
    public void Clock()
    {
        // Check for ADSR delay bug.
        // If the rate counter comparison value is set below the current value of the
        // rate counter, the counter will continue counting up until it wraps around
        // to zero at 2^15 = 0x8000, and then count rate_period - 1 before the
        // envelope can finally be stepped.
        // This has been verified by sampling ENV3.
        if ((++_rateCounter & 0x8000) != 0)
        {
            _rateCounter = (_rateCounter + 1) & 0x7fff;
        }

        if (_rateCounter != _ratePeriod)
        {
            return;
        }

        _rateCounter = 0;

        // The first envelope step in the attack state also resets the exponential
        // counter. This has been verified by sampling ENV3.
        if (_state == EnvelopeState.Attack || ++_exponentialCounter == _exponentialCounterPeriod)
        {
            _exponentialCounter = 0;

            // Check whether the envelope counter is frozen at zero.
            if (_holdZero)
            {
                return;
            }

            switch (_state)
            {
                case EnvelopeState.Attack:
                    // The envelope counter can flip from 0xff to 0x00 by changing state to
                    // release, then to attack. The envelope counter is then frozen at
                    // zero; to unlock this situation the state must be changed to release,
                    // then to attack. This has been verified by sampling ENV3.
                    _envelopeCounter = (_envelopeCounter + 1) & 0xff;
                    if (_envelopeCounter == 0xff)
                    {
                        _state = EnvelopeState.DecaySustain;
                        _ratePeriod = RateCounterPeriod[_decay];
                    }
                    break;

                case EnvelopeState.DecaySustain:
                    if (_envelopeCounter != SustainLevelTable[_sustain])
                    {
                        _envelopeCounter = (_envelopeCounter - 1) & 0xff;
                    }
                    break;

                case EnvelopeState.Release:
                    // The envelope counter can flip from 0x00 to 0xff by changing state to
                    // attack, then to release. The envelope counter will then continue
                    // counting down in the release state.
                    _envelopeCounter = (_envelopeCounter - 1) & 0xff;
                    break;
            }

            // Check for change of exponential counter period.
            _exponentialCounterPeriod = _envelopeCounter switch
            {
                0xff => 1,
                0x5d => 2,
                0x36 => 4,
                0x1a => 8,
                0x0e => 16,
                0x06 => 30,
                0x00 => 1,
                _ => _exponentialCounterPeriod,
            };

            if (_envelopeCounter == 0x00)
            {
                // When the envelope counter is changed to zero, it is frozen at zero.
                _holdZero = true;
            }
        }
    }

    /// <summary>SID clocking - delta_t cycles.</summary>
    public void Clock(int deltaT)
    {
        // NB! This requires two's complement integer (matches C# int).
        int rateStep = _ratePeriod - _rateCounter;
        if (rateStep <= 0)
        {
            rateStep += 0x7fff;
        }

        while (deltaT != 0)
        {
            if (deltaT < rateStep)
            {
                _rateCounter += deltaT;
                if ((_rateCounter & 0x8000) != 0)
                {
                    _rateCounter = (_rateCounter + 1) & 0x7fff;
                }
                return;
            }

            _rateCounter = 0;
            deltaT -= rateStep;

            if (_state == EnvelopeState.Attack || ++_exponentialCounter == _exponentialCounterPeriod)
            {
                _exponentialCounter = 0;

                if (_holdZero)
                {
                    rateStep = _ratePeriod;
                    continue;
                }

                switch (_state)
                {
                    case EnvelopeState.Attack:
                        _envelopeCounter = (_envelopeCounter + 1) & 0xff;
                        if (_envelopeCounter == 0xff)
                        {
                            _state = EnvelopeState.DecaySustain;
                            _ratePeriod = RateCounterPeriod[_decay];
                        }
                        break;

                    case EnvelopeState.DecaySustain:
                        if (_envelopeCounter != SustainLevelTable[_sustain])
                        {
                            _envelopeCounter = (_envelopeCounter - 1) & 0xff;
                        }
                        break;

                    case EnvelopeState.Release:
                        _envelopeCounter = (_envelopeCounter - 1) & 0xff;
                        break;
                }

                _exponentialCounterPeriod = _envelopeCounter switch
                {
                    0xff => 1,
                    0x5d => 2,
                    0x36 => 4,
                    0x1a => 8,
                    0x0e => 16,
                    0x06 => 30,
                    0x00 => 1,
                    _ => _exponentialCounterPeriod,
                };

                if (_envelopeCounter == 0x00)
                {
                    _holdZero = true;
                }
            }

            rateStep = _ratePeriod;
        }
    }

    public void WriteControlReg(int control)
    {
        int gateNext = control & 0x01;

        // The rate counter is never reset, thus there will be a delay before the
        // envelope counter starts counting up (attack) or down (release).
        if (_gate == 0 && gateNext != 0)
        {
            // Gate bit on: Start attack, decay, sustain.
            _state = EnvelopeState.Attack;
            _ratePeriod = RateCounterPeriod[_attack];

            // Switching to attack state unlocks the zero freeze.
            _holdZero = false;
        }
        else if (_gate != 0 && gateNext == 0)
        {
            // Gate bit off: Start release.
            _state = EnvelopeState.Release;
            _ratePeriod = RateCounterPeriod[_release];
        }

        _gate = gateNext;
    }

    public void WriteAttackDecay(int attackDecay)
    {
        _attack = (attackDecay >> 4) & 0x0f;
        _decay = attackDecay & 0x0f;

        if (_state == EnvelopeState.Attack)
        {
            _ratePeriod = RateCounterPeriod[_attack];
        }
        else if (_state == EnvelopeState.DecaySustain)
        {
            _ratePeriod = RateCounterPeriod[_decay];
        }
    }

    public void WriteSustainRelease(int sustainRelease)
    {
        _sustain = (sustainRelease >> 4) & 0x0f;
        _release = sustainRelease & 0x0f;

        if (_state == EnvelopeState.Release)
        {
            _ratePeriod = RateCounterPeriod[_release];
        }
    }

    public int ReadEnv() => Output();

    /// <summary>8-bit envelope output.</summary>
    public int Output() => _envelopeCounter;

    /// <summary>Which of Attack/Decay-Sustain/Release the envelope is currently in - exposed
    /// read-only for debug/trace tooling (the Signal Path view), not used by the chip itself.</summary>
    public EnvelopeState State => _state;
}
