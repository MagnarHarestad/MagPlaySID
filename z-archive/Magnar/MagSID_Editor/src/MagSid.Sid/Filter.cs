namespace MagSid.Sid;

/// <summary>
/// Direct port of reSID's filter.h/filter.cc - a two-integrator-loop biquadratic filter,
/// confirmed by Bob Yannes to be the actual circuit used in the SID chip. See the
/// original source for the full circuit explanation; the comments here focus on what
/// each step computes.
/// </summary>
public sealed class Filter
{
    private bool _enabled;

    private int _fc;
    private int _res;
    private int _filt;
    private int _voice3Off;
    private int _hpBpLp;
    private int _vol;

    private int _mixerDC;

    private int _vhp;
    private int _vbp;
    private int _vlp;
    private int _vnf;

    private int _w0;
    private int _w0Ceil1;
    private int _w0CeilDt;
    private int _1024DivQ;

    private readonly int[] _f0_6581 = new int[2048];
    private readonly int[] _f0_8580 = new int[2048];
    private int[] _f0;

    public Filter()
    {
        _fc = 0;
        _res = 0;
        _filt = 0;
        _voice3Off = 0;
        _hpBpLp = 0;
        _vol = 0;

        _vhp = 0;
        _vbp = 0;
        _vlp = 0;
        _vnf = 0;

        EnableFilter(true);

        BuildCutoffTable(FilterCutoffPoints.Mos6581, _f0_6581);
        BuildCutoffTable(FilterCutoffPoints.Mos8580, _f0_8580);

        _f0 = _f0_6581;
        SetChipModel(ChipModel.Mos6581);
    }

    private static void BuildCutoffTable((double X, double Y)[] points, int[] table)
    {
        CubicSpline.Interpolate(points, (x, y) =>
        {
            if (y < 0)
            {
                y = 0;
            }
            table[(int)x] = (int)y;
        }, 1.0);
    }

    public void EnableFilter(bool enable) => _enabled = enable;

    public void SetChipModel(ChipModel model)
    {
        if (model == ChipModel.Mos6581)
        {
            // The mixer has a small input DC offset - see filter.cc for the full
            // derivation from measured voltages.
            _mixerDC = (-0xfff * 0xff / 18) >> 7;
            _f0 = _f0_6581;
        }
        else
        {
            // No DC offsets in the MOS8580.
            _mixerDC = 0;
            _f0 = _f0_8580;
        }

        SetW0();
        SetQ();
    }

    public void Reset()
    {
        _fc = 0;
        _res = 0;
        _filt = 0;
        _voice3Off = 0;
        _hpBpLp = 0;
        _vol = 0;

        _vhp = 0;
        _vbp = 0;
        _vlp = 0;
        _vnf = 0;

        SetW0();
        SetQ();
    }

    public void WriteFcLo(int fcLo)
    {
        _fc = (_fc & 0x7f8) | (fcLo & 0x007);
        SetW0();
    }

    public void WriteFcHi(int fcHi)
    {
        _fc = ((fcHi << 3) & 0x7f8) | (_fc & 0x007);
        SetW0();
    }

    public void WriteResFilt(int resFilt)
    {
        _res = (resFilt >> 4) & 0x0f;
        SetQ();

        _filt = resFilt & 0x0f;
    }

    public void WriteModeVol(int modeVol)
    {
        _voice3Off = modeVol & 0x80;
        _hpBpLp = (modeVol >> 4) & 0x07;
        _vol = modeVol & 0x0f;
    }

    private void SetW0()
    {
        const double pi = 3.1415926535897932385;

        // Multiply with 1.048576 to facilitate division by 1 000 000 by right-
        // shifting 20 times (2 ^ 20 = 1048576).
        _w0 = (int)(2 * pi * _f0[_fc] * 1.048576);

        // Limit f0 to 16kHz to keep 1 cycle filter stable.
        const int w0Max1 = (int)(2 * pi * 16000 * 1.048576);
        _w0Ceil1 = _w0 <= w0Max1 ? _w0 : w0Max1;

        // Limit f0 to 4kHz to keep delta_t cycle filter stable.
        const int w0MaxDt = (int)(2 * pi * 4000 * 1.048576);
        _w0CeilDt = _w0 <= w0MaxDt ? _w0 : w0MaxDt;
    }

    private void SetQ()
    {
        // Q is controlled linearly by res. Q has approximate range [0.707, 1.7].
        // The coefficient 1024 is dispensed of later by right-shifting 10 times.
        _1024DivQ = (int)(1024.0 / (0.707 + 1.0 * _res / 0x0f));
    }

    /// <summary>SID clocking - 1 cycle.</summary>
    public void Clock(int voice1, int voice2, int voice3, int extIn)
    {
        // Scale each voice down from 20 to 13 bits.
        voice1 >>= 7;
        voice2 >>= 7;

        // NB! Voice 3 is not silenced by voice3off if it is routed through the filter.
        if (_voice3Off != 0 && (_filt & 0x04) == 0)
        {
            voice3 = 0;
        }
        else
        {
            voice3 >>= 7;
        }

        extIn >>= 7;

        if (!_enabled)
        {
            _vnf = voice1 + voice2 + voice3 + extIn;
            _vhp = _vbp = _vlp = 0;
            return;
        }

        int vi = RouteVoices(voice1, voice2, voice3, extIn, _filt, out _vnf);

        int dVbp = (_w0Ceil1 * _vhp) >> 20;
        int dVlp = (_w0Ceil1 * _vbp) >> 20;
        _vbp -= dVbp;
        _vlp -= dVlp;
        _vhp = ((_vbp * _1024DivQ) >> 10) - _vlp - vi;
    }

    /// <summary>SID clocking - delta_t cycles.</summary>
    public void Clock(int deltaT, int voice1, int voice2, int voice3, int extIn)
    {
        voice1 >>= 7;
        voice2 >>= 7;

        if (_voice3Off != 0 && (_filt & 0x04) == 0)
        {
            voice3 = 0;
        }
        else
        {
            voice3 >>= 7;
        }

        extIn >>= 7;

        if (!_enabled)
        {
            _vnf = voice1 + voice2 + voice3 + extIn;
            _vhp = _vbp = _vlp = 0;
            return;
        }

        int vi = RouteVoices(voice1, voice2, voice3, extIn, _filt, out _vnf);

        // Maximum delta cycles for the filter to work satisfactorily under current
        // cutoff frequency and resonance constraints is approximately 8.
        int deltaTFlt = 8;

        while (deltaT != 0)
        {
            if (deltaT < deltaTFlt)
            {
                deltaTFlt = deltaT;
            }

            int w0DeltaT = (_w0CeilDt * deltaTFlt) >> 6;

            int dVbp = (w0DeltaT * _vhp) >> 14;
            int dVlp = (w0DeltaT * _vbp) >> 14;
            _vbp -= dVbp;
            _vlp -= dVlp;
            _vhp = ((_vbp * _1024DivQ) >> 10) - _vlp - vi;

            deltaT -= deltaTFlt;
        }
    }

    // Route voices into or around the filter, per the RES/FILT register's filt bits.
    private static int RouteVoices(int voice1, int voice2, int voice3, int extIn, int filt, out int vnf)
    {
        int vi;
        switch (filt)
        {
            default:
            case 0x0: vi = 0; vnf = voice1 + voice2 + voice3 + extIn; break;
            case 0x1: vi = voice1; vnf = voice2 + voice3 + extIn; break;
            case 0x2: vi = voice2; vnf = voice1 + voice3 + extIn; break;
            case 0x3: vi = voice1 + voice2; vnf = voice3 + extIn; break;
            case 0x4: vi = voice3; vnf = voice1 + voice2 + extIn; break;
            case 0x5: vi = voice1 + voice3; vnf = voice2 + extIn; break;
            case 0x6: vi = voice2 + voice3; vnf = voice1 + extIn; break;
            case 0x7: vi = voice1 + voice2 + voice3; vnf = extIn; break;
            case 0x8: vi = extIn; vnf = voice1 + voice2 + voice3; break;
            case 0x9: vi = voice1 + extIn; vnf = voice2 + voice3; break;
            case 0xa: vi = voice2 + extIn; vnf = voice1 + voice3; break;
            case 0xb: vi = voice1 + voice2 + extIn; vnf = voice3; break;
            case 0xc: vi = voice3 + extIn; vnf = voice1 + voice2; break;
            case 0xd: vi = voice1 + voice3 + extIn; vnf = voice2; break;
            case 0xe: vi = voice2 + voice3 + extIn; vnf = voice1; break;
            case 0xf: vi = voice1 + voice2 + voice3 + extIn; vnf = 0; break;
        }
        return vi;
    }

    /// <summary>SID audio output (20 bits).</summary>
    public int Output()
    {
        if (!_enabled)
        {
            return (_vnf + _mixerDC) * _vol;
        }

        // Mix highpass, bandpass, and lowpass outputs. The sum is not weighted.
        int vf = _hpBpLp switch
        {
            0x0 => 0,
            0x1 => _vlp,
            0x2 => _vbp,
            0x3 => _vlp + _vbp,
            0x4 => _vhp,
            0x5 => _vlp + _vhp,
            0x6 => _vbp + _vhp,
            _ => _vlp + _vbp + _vhp,
        };

        return (_vnf + vf + _mixerDC) * _vol;
    }
}
