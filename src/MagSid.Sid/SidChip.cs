// Based on sid.h and sid.cc from reSID, a MOS6581 SID emulator engine.
// Copyright (C) 2004 Dag Lem <resid@nimrod.no>
// Translated to C# and modified by Magnar Harestad, 2026.
//
// This file is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 2
// of the License, or (at your option) any later version. It is distributed WITHOUT ANY
// WARRANTY; see the LICENSE file for details.

namespace MagSid.Sid;

/// <summary>
/// Direct port of reSID's sid.h/sid.cc top-level SID class: three voices, the filter, and
/// the external filter, wired together and exposed as register read/write plus sample
/// generation.
///
/// One deliberate simplification versus the source: only single-cycle Clock() is ported
/// (not the delta_t bulk-clocking path with its cycle-precise hard-sync lookahead) - since
/// audio-rate sample generation only needs to advance ~22 SID cycles between samples at
/// 44.1kHz/985kHz, stepping Clock() in a loop is simple, unambiguously correct (it is
/// literally "one hardware cycle at a time"), and fast enough for real-time playback on
/// any modern machine. clock_resample_interpolate/clock_resample_fast (the windowed-sinc
/// FIR resampling paths) are likewise not ported - see the plan's "Utanför scope" section.
/// </summary>
public sealed class SidChip
{
    private const int FixpShift = 16;
    private const int FixpMask = 0xffff;

    private readonly Voice[] _voice = [new Voice(), new Voice(), new Voice()];
    private readonly Filter _filter = new();
    private readonly ExternalFilter _extFilter = new();

    private int _busValue;
    private int _busValueTtl;

    private int _extIn;

    private SamplingMethod _sampling;
    private int _cyclesPerSample;
    private int _sampleOffset;
    private short _samplePrev;

    public SidChip()
    {
        _voice[0].SetSyncSource(_voice[2]);
        _voice[1].SetSyncSource(_voice[0]);
        _voice[2].SetSyncSource(_voice[1]);

        SetSamplingParameters(985248, SamplingMethod.Interpolate, 44100);
    }

    public void SetChipModel(ChipModel model)
    {
        foreach (var v in _voice)
        {
            v.SetChipModel(model);
        }

        _filter.SetChipModel(model);
        _extFilter.SetChipModel(model);
    }

    /// <summary>Read-only access to one voice's own generators (waveform, envelope) - for
    /// debug/trace tooling (the Signal Path view) that needs to sample chip-internal state the
    /// register interface alone can't expose, not used by the chip's own audio path.</summary>
    public Voice Voice(int index) => _voice[index];

    private readonly bool[] _voiceMuted = new bool[3];

    /// <summary>Silences one voice in the final mix without touching its register state at all -
    /// unlike dropping that voice's register writes (which would leave a gate stuck open or an
    /// envelope frozen mid-release the moment it's unmuted), the oscillator/envelope/sync keep
    /// running normally underneath; only the sample <see cref="Clock"/> feeds into the filter is
    /// zeroed. That also means hard-sync from this voice into its neighbour (see
    /// <see cref="Voice.SetSyncSource"/>) is unaffected by mute, exactly as the real 3OFF-less
    /// SID has no way to stop a voice from driving sync while still silencing it.</summary>
    public void SetVoiceMuted(int voice, bool muted) => _voiceMuted[voice] = muted;

    public bool IsVoiceMuted(int voice) => _voiceMuted[voice];

    public void EnableFilter(bool enable) => _filter.EnableFilter(enable);

    public void EnableExternalFilter(bool enable) => _extFilter.EnableFilter(enable);

    public void Reset()
    {
        foreach (var v in _voice)
        {
            v.Reset();
        }

        _filter.Reset();
        _extFilter.Reset();

        _busValue = 0;
        _busValueTtl = 0;
    }

    public void SetSamplingParameters(double clockFreq, SamplingMethod method, double sampleFreq)
    {
        _sampling = method;
        _cyclesPerSample = (int)(clockFreq / sampleFreq * (1 << FixpShift) + 0.5);
        _sampleOffset = 0;
        _samplePrev = 0;
    }

    /// <summary>16-bit input (EXT IN).</summary>
    public void Input(int sample) => _extIn = (sample << 4) * 3;

    /// <summary>16-bit output (AUDIO OUT).</summary>
    public int Output()
    {
        const int range = 1 << 16;
        const int half = range >> 1;
        int sample = _extFilter.Output() / ((4095 * 255 >> 7) * 3 * 15 * 2 / range);
        if (sample >= half)
        {
            return half - 1;
        }
        if (sample < -half)
        {
            return -half;
        }
        return sample;
    }

    public int Read(int offset) => offset switch
    {
        0x1b => _voice[2].Wave.ReadOsc(),
        0x1c => _voice[2].Envelope.ReadEnv(),
        _ => _busValue,
    };

    public void Write(int offset, int value)
    {
        _busValue = value;
        _busValueTtl = 0x2000;

        switch (offset)
        {
            case 0x00: _voice[0].Wave.WriteFreqLo(value); break;
            case 0x01: _voice[0].Wave.WriteFreqHi(value); break;
            case 0x02: _voice[0].Wave.WritePwLo(value); break;
            case 0x03: _voice[0].Wave.WritePwHi(value); break;
            case 0x04: _voice[0].WriteControlReg(value); break;
            case 0x05: _voice[0].Envelope.WriteAttackDecay(value); break;
            case 0x06: _voice[0].Envelope.WriteSustainRelease(value); break;
            case 0x07: _voice[1].Wave.WriteFreqLo(value); break;
            case 0x08: _voice[1].Wave.WriteFreqHi(value); break;
            case 0x09: _voice[1].Wave.WritePwLo(value); break;
            case 0x0a: _voice[1].Wave.WritePwHi(value); break;
            case 0x0b: _voice[1].WriteControlReg(value); break;
            case 0x0c: _voice[1].Envelope.WriteAttackDecay(value); break;
            case 0x0d: _voice[1].Envelope.WriteSustainRelease(value); break;
            case 0x0e: _voice[2].Wave.WriteFreqLo(value); break;
            case 0x0f: _voice[2].Wave.WriteFreqHi(value); break;
            case 0x10: _voice[2].Wave.WritePwLo(value); break;
            case 0x11: _voice[2].Wave.WritePwHi(value); break;
            case 0x12: _voice[2].WriteControlReg(value); break;
            case 0x13: _voice[2].Envelope.WriteAttackDecay(value); break;
            case 0x14: _voice[2].Envelope.WriteSustainRelease(value); break;
            case 0x15: _filter.WriteFcLo(value); break;
            case 0x16: _filter.WriteFcHi(value); break;
            case 0x17: _filter.WriteResFilt(value); break;
            case 0x18: _filter.WriteModeVol(value); break;
            default: break;
        }
    }

    /// <summary>SID clocking - 1 cycle.</summary>
    public void Clock()
    {
        if (--_busValueTtl <= 0)
        {
            _busValue = 0;
            _busValueTtl = 0;
        }

        foreach (var v in _voice)
        {
            v.Envelope.Clock();
        }

        foreach (var v in _voice)
        {
            v.Wave.Clock();
        }

        foreach (var v in _voice)
        {
            v.Wave.Synchronize();
        }

        _filter.Clock(
            _voiceMuted[0] ? 0 : _voice[0].Output(),
            _voiceMuted[1] ? 0 : _voice[1].Output(),
            _voiceMuted[2] ? 0 : _voice[2].Output(),
            _extIn);
        _extFilter.Clock(_filter.Output());
    }

    /// <summary>
    /// SID clocking with audio sampling, fixed-point arithmetics - port of clock_fast /
    /// clock_interpolate. Advances up to <paramref name="deltaT"/> SID cycles, writing at
    /// most <paramref name="count"/> samples into <paramref name="buffer"/> starting at
    /// <paramref name="offset"/>, and returns the number of samples written. On return,
    /// <paramref name="deltaT"/> holds the number of cycles that were consumed.
    /// </summary>
    public int ClockAndSample(ref int deltaT, short[] buffer, int offset, int count) =>
        _sampling == SamplingMethod.Interpolate
            ? ClockInterpolate(ref deltaT, buffer, offset, count)
            : ClockFast(ref deltaT, buffer, offset, count);

    private int ClockFast(ref int deltaT, short[] buf, int offset, int n)
    {
        int s = 0;

        while (true)
        {
            int nextSampleOffset = _sampleOffset + _cyclesPerSample + (1 << (FixpShift - 1));
            int deltaTSample = nextSampleOffset >> FixpShift;
            if (deltaTSample > deltaT)
            {
                break;
            }
            if (s >= n)
            {
                return s;
            }

            for (int i = 0; i < deltaTSample; i++)
            {
                Clock();
            }

            deltaT -= deltaTSample;
            _sampleOffset = (nextSampleOffset & FixpMask) - (1 << (FixpShift - 1));
            buf[offset + s++] = (short)Output();
        }

        for (int i = 0; i < deltaT; i++)
        {
            Clock();
        }

        _sampleOffset -= deltaT << FixpShift;
        deltaT = 0;
        return s;
    }

    private int ClockInterpolate(ref int deltaT, short[] buf, int offset, int n)
    {
        int s = 0;
        int i;

        while (true)
        {
            int nextSampleOffset = _sampleOffset + _cyclesPerSample;
            int deltaTSample = nextSampleOffset >> FixpShift;
            if (deltaTSample > deltaT)
            {
                break;
            }
            if (s >= n)
            {
                return s;
            }

            for (i = 0; i < deltaTSample - 1; i++)
            {
                Clock();
            }
            if (i < deltaTSample)
            {
                _samplePrev = (short)Output();
                Clock();
            }

            deltaT -= deltaTSample;
            _sampleOffset = nextSampleOffset & FixpMask;

            short sampleNow = (short)Output();
            buf[offset + s++] = (short)(_samplePrev + ((_sampleOffset * (sampleNow - _samplePrev)) >> FixpShift));
            _samplePrev = sampleNow;
        }

        for (i = 0; i < deltaT - 1; i++)
        {
            Clock();
        }
        if (i < deltaT)
        {
            _samplePrev = (short)Output();
            Clock();
        }

        _sampleOffset -= deltaT << FixpShift;
        deltaT = 0;
        return s;
    }
}
