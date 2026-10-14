// Based on extfilt.h and extfilt.cc from reSID, a MOS6581 SID emulator engine.
// Copyright (C) 2004 Dag Lem <resid@nimrod.no>
// Translated to C# and modified by Magnar Harestad, 2026.
//
// This file is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 2
// of the License, or (at your option) any later version. It is distributed WITHOUT ANY
// WARRANTY; see the LICENSE file for details.

namespace MagSid.Sid;

/// <summary>
/// Direct port of reSID's extfilt.h/extfilt.cc - the C64's audio output stage: a
/// low-pass filter (3dB at 16kHz) followed by a high-pass filter (3dB at 16Hz).
/// </summary>
public sealed class ExternalFilter
{
    private bool _enabled;
    private int _mixerDC;

    private int _vlp;
    private int _vhp;
    private int _vo;

    private int _w0Lp;
    private int _w0Hp;

    public ExternalFilter()
    {
        Reset();
        EnableFilter(true);
        SetChipModel(ChipModel.Mos6581);

        // Low-pass:  R = 10kOhm, C = 1000pF; w0l = 1/RC = 1/(1e4*1e-9) = 100000
        // High-pass: R =  1kOhm, C =   10uF; w0h = 1/RC = 1/(1e3*1e-5) =    100
        // Multiply with 1.048576 to facilitate division by 1 000 000 by right-
        // shifting 20 times.
        _w0Lp = 104858;
        _w0Hp = 105;
    }

    public void EnableFilter(bool enable) => _enabled = enable;

    public void SetChipModel(ChipModel model)
    {
        if (model == ChipModel.Mos6581)
        {
            // Maximum mixer DC output level; to be removed if the external filter is
            // turned off: ((wave DC + voice DC)*voices + mixer DC)*volume.
            _mixerDC = ((((0x800 - 0x380) + 0x800) * 0xff * 3 - 0xfff * 0xff / 18) >> 7) * 0x0f;
        }
        else
        {
            _mixerDC = 0;
        }
    }

    public void Reset()
    {
        _vlp = 0;
        _vhp = 0;
        _vo = 0;
    }

    /// <summary>SID clocking - 1 cycle.</summary>
    public void Clock(int vi)
    {
        if (!_enabled)
        {
            _vlp = _vhp = 0;
            _vo = vi - _mixerDC;
            return;
        }

        int dVlp = ((_w0Lp >> 8) * (vi - _vlp)) >> 12;
        int dVhp = (_w0Hp * (_vlp - _vhp)) >> 20;
        _vo = _vlp - _vhp;
        _vlp += dVlp;
        _vhp += dVhp;
    }

    /// <summary>SID clocking - delta_t cycles.</summary>
    public void Clock(int deltaT, int vi)
    {
        if (!_enabled)
        {
            _vlp = _vhp = 0;
            _vo = vi - _mixerDC;
            return;
        }

        int deltaTFlt = 8;

        while (deltaT != 0)
        {
            if (deltaT < deltaTFlt)
            {
                deltaTFlt = deltaT;
            }

            int dVlp = ((_w0Lp * deltaTFlt >> 8) * (vi - _vlp)) >> 12;
            int dVhp = (_w0Hp * deltaTFlt * (_vlp - _vhp)) >> 20;
            _vo = _vlp - _vhp;
            _vlp += dVlp;
            _vhp += dVhp;

            deltaT -= deltaTFlt;
        }
    }

    /// <summary>Audio output (19.5 bits).</summary>
    public int Output() => _vo;
}
