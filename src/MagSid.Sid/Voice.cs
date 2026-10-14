// Based on voice.h and voice.cc from reSID, a MOS6581 SID emulator engine.
// Copyright (C) 2004 Dag Lem <resid@nimrod.no>
// Translated to C# and modified by Magnar Harestad, 2026.
//
// This file is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 2
// of the License, or (at your option) any later version. It is distributed WITHOUT ANY
// WARRANTY; see the LICENSE file for details.

namespace MagSid.Sid;

/// <summary>Direct port of reSID's voice.h/voice.cc - combines a WaveformGenerator and an
/// EnvelopeGenerator into one amplitude-modulated voice.</summary>
public sealed class Voice
{
    public WaveformGenerator Wave { get; } = new();
    public EnvelopeGenerator Envelope { get; } = new();

    private int _waveZero;
    private int _voiceDC;

    public Voice() => SetChipModel(ChipModel.Mos6581);

    public void SetChipModel(ChipModel model)
    {
        Wave.SetChipModel(model);

        if (model == ChipModel.Mos6581)
        {
            // The waveform D/A converter's measured "zero" level - see voice.cc for
            // the full measurement procedure.
            _waveZero = 0x380;

            // The envelope multiplying D/A converter's DC offset - see voice.cc.
            _voiceDC = 0x800 * 0xff;
        }
        else
        {
            // No DC offsets in the MOS8580.
            _waveZero = 0x800;
            _voiceDC = 0;
        }
    }

    public void SetSyncSource(Voice source) => Wave.SetSyncSource(source.Wave);

    public void WriteControlReg(int control)
    {
        Wave.WriteControlReg(control);
        Envelope.WriteControlReg(control);
    }

    public void Reset()
    {
        Wave.Reset();
        Envelope.Reset();
    }

    /// <summary>Amplitude modulated waveform output. Range [-2048*255, 2047*255].</summary>
    public int Output() => (Wave.Output() - _waveZero) * Envelope.Output() + _voiceDC;
}
