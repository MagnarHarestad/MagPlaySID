using MagPlay.Score;
using MagSid.Playback;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MagSid.Sid;
using NAudio.Wave;

namespace MagPlay.C64;

/// <summary>
/// Plays a <c>.sid</c> on the <see cref="C64"/>: the tune runs as it would on the machine - PSID or
/// RSID, on the raster or a CIA timer, multispeed, digis from an NMI - and the SID is clocked up to
/// each write's own cycle before the write, so every write sounds where the 6510 made it. The C64
/// runs a little ahead of the sound card, a slice at a time, for as long as anyone listens; it
/// keeps no record of the writes. A 2SID or 3SID tune plays on as many chips, clocked together and
/// mixed into one.
///
/// The cycles count from the C64's start, as a <see cref="SidTune"/> recorded for a transcription
/// counts them, so a view of the transcription follows this by <see cref="CycleAt"/>.
/// </summary>
public sealed class C64PlaybackEngine : IWaveProvider, IScopeSource, ISidPort
{
    public const int SampleRate = 44100;

    /// <summary>Cycles the C64 runs at a time: two milliseconds, well inside one audio buffer.</summary>
    private const int Slice = 2000;

    /// <summary>Half a voice's peak-to-peak swing, bringing a scope trace to [-1, 1] - as
    /// <see cref="SidFilePlaybackEngine"/> scales it.</summary>
    private const float VoiceOutputScale = 2048f * 255f;

    /// <summary>Samples of history the scope rings keep: 1.5 seconds, past any device's latency.</summary>
    private const int RingSamples = 65536;

    /// <summary>What the mix of several chips is scaled by, so two or three at full volume do not
    /// clip where one would not: by chip count.</summary>
    private static readonly double[] MixScale = [0, 1, 0.7, 0.55];

    /// <summary>What the sum of <paramref name="chips"/> chips is scaled by: the three-chip mix as it
    /// always was, and past it one over the square root of their number - as many chips again sound
    /// about as loud, not twice.</summary>
    private static double Scale(int chips) => chips < MixScale.Length ? MixScale[chips] : 1 / Math.Sqrt(chips);

    /// <summary>The most SID chips a tune plays on: the v5 format's fifteen.</summary>
    public const int MostChips = 15;

    /// <summary>For a tune heard in stereo, how much of each chip goes left and right: a chip to one
    /// side at the square root of two there, so a chip on each side is about as loud as the two in
    /// the middle - and every chip in the middle is the mono mix exactly.</summary>
    private readonly double[] _toLeft = [], _toRight = [];

    /// <summary>The right channel of the samples made and not yet handed over, for a stereo tune;
    /// <see cref="_pending"/> then holds the left.</summary>
    private short[] _pendingRight = [];

    /// <summary>How loud a fast-forward is: quieter than the tune, as tape winding on is.</summary>
    private const float FastForwardVolume = 0.3f;

    /// <summary>The fastest a tune is asked to play.</summary>
    public const int MaxSpeed = 32;

    /// <summary>A speed always played as asked, however long the computer takes over it.</summary>
    private const int SafeSpeed = 4;

    /// <summary>The share of the audio thread's time a fast-forward may take: the rest is the margin
    /// that keeps the sound from breaking up, and the transcription's and the window's.</summary>
    private const double Headroom = 0.5;

    private readonly SidChip[] _chips;
    private readonly SidTune _tune;
    private long _chipCycle;
    private bool _silent;

    /// <summary>Set while the tune is run ahead with the chips standing still: their registers are
    /// written, but not a cycle of them is worked out - see <see cref="SkipTo"/>.</summary>
    private bool _skipping;

    /// <summary>How long before the place jumped to the chips are run along, unheard, so their
    /// envelopes and filter are where the music has them.</summary>
    private const double SettleSeconds = 3;
    private short[] _pending = new short[8192];
    private int _pendingCount;

    private readonly float[][] _voiceScopes;
    private readonly float[] _mixScope = new float[RingSamples];

    /// <summary>The volume the first chip is set to, sample by sample: a digi's sound, as it plays
    /// its samples through the volume register.</summary>
    private readonly float[] _digiScope = new float[RingSamples];

    /// <summary>Writes to the volume register a second that make it a digi's: a player sets it a few
    /// times a second at most, a digi thousands.</summary>
    private const int DigiWritesPerSecond = 500;

    private long _volumeSecond = -1;
    private int _volumeWrites;
    private readonly int[][] _voiceStates;
    private readonly int[][] _registers;
    private readonly short[] _extraSample = new short[1];
    private long _scopeWritten;
    private readonly object _scopeLock = new();

    /// <summary>Where the output went silent for a pause, or on again, or changed speed: the device's
    /// sample and the tune's there, and the tune's samples to each of the device's from there on -
    /// 0 while silent, 1 as it plays, more while it fast-forwards.</summary>
    private readonly List<(long Device, long Tune, int Rate)> _turns = [(0, 0, 1)];
    private long _device;
    private long _tuneSample;

    /// <param name="song">The subtune, counting from 1.</param>
    /// <param name="model">The SID chip every SID of the tune is played on, whatever the tune says;
    /// null for the one it asks for.</param>
    public C64PlaybackEngine(SidFile file, int song, ChipModel? model = null)
    {
        int chips = 1 + Math.Min(MostChips - 1, file.ExtraSids.Count);
        IsStereo = chips > 1 && file.IsStereo;
        if (IsStereo)
        {
            _toLeft = new double[chips];
            _toRight = new double[chips];
            for (int chip = 0; chip < chips; chip++)
            {
                var pan = chip < file.Panning.Count ? file.Panning[chip] : SidPan.Center;
                (_toLeft[chip], _toRight[chip]) = pan switch
                {
                    SidPan.Left => (Math.Sqrt(2), 0),
                    SidPan.Right => (0, Math.Sqrt(2)),
                    _ => (1.0, 1.0),
                };
            }
            _pendingRight = new short[_pending.Length];
        }
        WaveFormat = new(SampleRate, 16, IsStereo ? 2 : 1);
        _chips = new SidChip[chips];
        _registers = new int[chips][];
        _voiceStates = new int[3 * chips][];
        _voiceScopes = new float[3 * chips][];
        for (int voice = 0; voice < _voiceStates.Length; voice++)
        {
            _voiceStates[voice] = new int[RingSamples];
            _voiceScopes[voice] = new float[RingSamples];
        }

        // A tune for the Sound Expander has its FM chip heard with the SIDs.
        if (file.RequiresFmOpl)
            _fm = new FmSound(SampleRate);
        _tune = SidTune.Load(file, Math.Clamp(song, 1, Math.Max(1, file.Songs)), this, record: false);
        _tune.Computer.KeepsCalls = false;
        File = file;
        Song = _tune.Song;
        ClockHz = Machines.ClockHz(_tune.Computer.Machine);
        for (int chip = 0; chip < chips; chip++)
        {
            bool is8580 = model is { } chosen ? chosen == ChipModel.Mos8580
                : chip == 0 ? file.Is8580 : file.ExtraSids8580[chip - 1];
            _chips[chip] = new SidChip();
            _chips[chip].SetChipModel(is8580 ? ChipModel.Mos8580 : ChipModel.Mos6581);
            _chips[chip].SetSamplingParameters(ClockHz, SamplingMethod.Interpolate, SampleRate);
            _registers[chip] = new int[0x19];
        }

        // Init, unheard, up to the first call - or two frames, for a tune that plays without - and
        // the chip let settle from the volume it set, so the music starts without a thump.
        _silent = true;
        long until = 2L * Machines.CyclesPerFrame(_tune.Computer.Machine);
        while (_tune.Computer.Calls.Count == 0 && _tune.Computer.Cycle < until)
            _tune.Run(64);
        ClockTo(_tune.Computer.Cycle);
        foreach (var chip in _chips)
            chip.SettleStartupTransient(ClockHz);
        _silent = false;
        StartCycle = _chipCycle;
    }

    public SidFile File { get; }

    public int Song { get; }

    /// <summary>The C64's clock: PAL's or NTSC's.</summary>
    public double ClockHz { get; }

    /// <summary>The C64's cycle the first sample is taken on.</summary>
    public long StartCycle { get; }

    /// <summary>Why the tune stopped, if it did - it ran into something that is not code.</summary>
    public string? Failure { get; private set; }

    public bool Paused { get; set; }

    /// <summary>How many times its own speed the tune plays: 1, or more to fast-forward - the C64 runs
    /// that much faster, heard higher and quieter, and every view of it moves as fast.</summary>
    public int Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 1, MaxSpeed);
    }

    private volatile int _speed = 1;

    /// <summary>How many times its own speed the tune is playing now: <see cref="Speed"/>, or less
    /// when this computer cannot make its sound that fast; 0 while silent.</summary>
    public int PlayingSpeed => _playing;

    private volatile int _playing;

    /// <summary>The time a sample of the tune takes to make, in seconds, as it has lately.</summary>
    private double _secondsPerSample;

    /// <summary>What the sound is multiplied by, 0-1: the listener's volume. Only what is heard - the
    /// scopes and the spectrum show the tune as it is, whatever the volume.</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    private volatile float _volume = 1f;

    /// <summary>The volume the last buffer ended on, each buffer gliding from it to the one asked
    /// for, so a turn of the knob does not click. Below zero until the first.</summary>
    private float _applied = -1f;

    /// <summary>16-bit at <see cref="SampleRate"/>: mono, or stereo for a tune whose header places
    /// its SIDs apart (<see cref="IsStereo"/>), left and right in turn.</summary>
    public WaveFormat WaveFormat { get; }

    /// <summary>Whether the sound is stereo: a v5 tune with SIDs to the left and the right.</summary>
    public bool IsStereo { get; }

    /// <summary>The tune's own sample the device is playing at its sample <paramref name="device"/>:
    /// the samples of silence a pause gave the device are not the tune's, so a view following this
    /// stands still while paused - and runs ahead as fast as the tune while it fast-forwards.</summary>
    public long TuneSampleAt(long device)
    {
        lock (_turns)
        {
            int at = _turns.FindLastIndex(t => t.Device <= device);
            var (from, tune, rate) = _turns[Math.Max(0, at)];
            return tune + Math.Max(0, device - from) * rate;
        }
    }

    /// <summary>The tune run on, unheard, to sample <paramref name="target"/> - for a jump: the C64
    /// alone, its SID writes only kept, to a few seconds before; then with the chips, so they sound
    /// as the music has them there. A tune that reads a chip's oscillator or envelope has the chips
    /// run along from that read on, so it goes the way it would. The tune's samples, its rows' and
    /// its scopes', count on from there.</summary>
    public void SkipTo(long target, CancellationToken cancel = default)
    {
        if (target <= 0)
            return;
        long targetCycle = CycleAt(target);
        long fastUntil = targetCycle - (long)(SettleSeconds * ClockHz);
        _silent = true;
        if (fastUntil > _tune.Computer.Cycle)
        {
            _skipping = true;
            while (_skipping && _tune.Computer.Cycle < fastUntil)
            {
                cancel.ThrowIfCancellationRequested();
                _tune.Run(Math.Min(Slice * 50, fastUntil - _tune.Computer.Cycle));
            }
            if (_skipping)
            {
                // The chips take up from here: what was written to them is where the music is.
                _skipping = false;
                _chipCycle = _tune.Computer.Cycle;
            }
        }
        while (_tune.Computer.Cycle < targetCycle)
        {
            cancel.ThrowIfCancellationRequested();
            _tune.Run(Math.Min(Slice * 10, targetCycle - _tune.Computer.Cycle));
            ClockTo(_tune.Computer.Cycle);
        }
        _silent = false;
        _pendingCount = 0;
        lock (_turns)
        {
            _turns.Clear();
            _turns.Add((0, target, 1));
            _device = 0;
            _tuneSample = target;
        }
        lock (_scopeLock)
            _scopeWritten = target;
    }

    /// <summary>The C64's cycle at sample <paramref name="sample"/>.</summary>
    public long CycleAt(double sample) => StartCycle + (long)(sample * ClockHz / SampleRate);

    /// <summary>The SID chips the tune plays on: 1, or up to 15 for a multi-SID tune.</summary>
    public int Chips => _chips.Length;

    /// <summary>A voice muted: 0-2 the first chip's, 3-5 the second's, 6-8 the third's.</summary>
    public void SetVoiceMuted(int voice, bool muted)
    {
        if (voice >= 0 && voice < 3 * _chips.Length)
            _chips[voice / 3].SetVoiceMuted(voice % 3, muted);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (IsStereo)
            return ReadStereo(buffer, offset, count);
        int wanted = count / 2;
        int rate = Paused || Failure is not null ? 0 : Affordable(_speed);
        _playing = rate;
        lock (_turns)
        {
            if (_turns[^1].Rate != rate)
                _turns.Add((_device, _tuneSample, rate));
            if (_turns.Count > 256)
                _turns.RemoveRange(0, _turns.Count - 256);
            _device += wanted;
            _tuneSample += (long)wanted * rate;
        }

        if (rate == 0)
        {
            SwitchWhileSilent();
            Array.Clear(buffer, offset, wanted * 2);
            return wanted * 2;
        }

        PlanSwitch();
        int needed = wanted * rate;
        int before = _pendingCount;
        long started = Stopwatch.GetTimestamp();
        try
        {
            while (_pendingCount < needed)
            {
                _tune.Run(Slice);
                ClockTo(_tune.Computer.Cycle);
                SwitchIfDue();
            }
        }
        catch (Exception ex)
        {
            // The audio thread must not die with the tune: what it played stays, then silence.
            Failure = ex.Message;
        }
        if (_pendingCount > before)
        {
            double each = Stopwatch.GetElapsedTime(started).TotalSeconds / (_pendingCount - before);
            _secondsPerSample = _secondsPerSample == 0 ? each : _secondsPerSample + ((each - _secondsPerSample) * 0.1);
        }

        // Fast-forwarding, each sample out is the average of as many of the tune's - its pitch goes
        // up as tape's does, without the grit of samples dropped - at the fast-forward's volume.
        int ready = Math.Min(wanted, _pendingCount / rate);
        var output = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(offset, wanted * 2));
        if (rate == 1)
            _pending.AsSpan(0, ready).CopyTo(output);
        else
            for (int i = 0; i < ready; i++)
            {
                int sum = 0;
                for (int j = 0; j < rate; j++)
                    sum += _pending[i * rate + j];
                output[i] = (short)(sum * FastForwardVolume / rate);
            }
        float volume = _volume;
        float gain = _applied < 0 ? volume : _applied;
        if (gain != 1f || volume != 1f)
        {
            float step = (volume - gain) / Math.Max(1, ready);
            for (int i = 0; i < ready; i++)
            {
                gain += step;
                output[i] = (short)(output[i] * gain);
            }
        }
        _applied = volume;
        FadeAroundSwitch(output, ready, rate, 1);
        output[ready..].Clear();

        int used = ready * rate;
        Array.Copy(_pending, used, _pending, 0, _pendingCount - used);
        _pendingCount -= used;
        HandedOver(used);
        return wanted * 2;
    }

    /// <summary><see cref="Read"/> for a stereo tune: frames of left and right, made and handed over
    /// as the mono one's samples are.</summary>
    private int ReadStereo(byte[] buffer, int offset, int count)
    {
        int wanted = count / 4;
        int rate = Paused || Failure is not null ? 0 : Affordable(_speed);
        _playing = rate;
        lock (_turns)
        {
            if (_turns[^1].Rate != rate)
                _turns.Add((_device, _tuneSample, rate));
            if (_turns.Count > 256)
                _turns.RemoveRange(0, _turns.Count - 256);
            _device += wanted;
            _tuneSample += (long)wanted * rate;
        }

        if (rate == 0)
        {
            SwitchWhileSilent();
            Array.Clear(buffer, offset, wanted * 4);
            return wanted * 4;
        }

        PlanSwitch();
        int needed = wanted * rate;
        int before = _pendingCount;
        long started = Stopwatch.GetTimestamp();
        try
        {
            while (_pendingCount < needed)
            {
                _tune.Run(Slice);
                ClockTo(_tune.Computer.Cycle);
                SwitchIfDue();
            }
        }
        catch (Exception ex)
        {
            Failure = ex.Message;
        }
        if (_pendingCount > before)
        {
            double each = Stopwatch.GetElapsedTime(started).TotalSeconds / (_pendingCount - before);
            _secondsPerSample = _secondsPerSample == 0 ? each : _secondsPerSample + ((each - _secondsPerSample) * 0.1);
        }

        int ready = Math.Min(wanted, _pendingCount / rate);
        var output = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(offset, wanted * 4));
        for (int i = 0; i < ready; i++)
        {
            if (rate == 1)
            {
                output[2 * i] = _pending[i];
                output[(2 * i) + 1] = _pendingRight[i];
                continue;
            }
            int left = 0, right = 0;
            for (int j = 0; j < rate; j++)
            {
                left += _pending[(i * rate) + j];
                right += _pendingRight[(i * rate) + j];
            }
            output[2 * i] = (short)(left * FastForwardVolume / rate);
            output[(2 * i) + 1] = (short)(right * FastForwardVolume / rate);
        }
        float volume = _volume;
        float gain = _applied < 0 ? volume : _applied;
        if (gain != 1f || volume != 1f)
        {
            float step = (volume - gain) / Math.Max(1, ready);
            for (int i = 0; i < ready; i++)
            {
                gain += step;
                output[2 * i] = (short)(output[2 * i] * gain);
                output[(2 * i) + 1] = (short)(output[(2 * i) + 1] * gain);
            }
        }
        _applied = volume;
        FadeAroundSwitch(output, ready, rate, 2);
        output[(ready * 2)..].Clear();

        int used = ready * rate;
        Array.Copy(_pending, used, _pending, 0, _pendingCount - used);
        Array.Copy(_pendingRight, used, _pendingRight, 0, _pendingCount - used);
        _pendingCount -= used;
        HandedOver(used);
        return wanted * 4;
    }

    // ------------------------------------------------------------------ the chip changed as it plays

    /// <summary>A change of SID chip asked for while playing: the chip, or null for the tune's own.</summary>
    private sealed record ChipChange(ChipModel? Model);

    private volatile ChipChange? _chipAsked;
    private ChipChange? _chipChanging;

    /// <summary>The tune's samples handed over so far: <see cref="_pending"/>[0] is the next.</summary>
    private long _handedOver;

    /// <summary>The tune's sample the sound is faded down to before the chip changes, and the one the
    /// new chip's sound starts at - below 0 while there is no change under way.</summary>
    private long _switchPlanned = -1, _switchedAt = -1;

    /// <summary>The samples the sound fades down over before the chip changes, and up over after:
    /// 15 ms - the jump in the 6581's DC level is not heard as a click, nor the change as a gap.</summary>
    private const int SwitchFade = SampleRate * 15 / 1000;

    /// <summary>Every SID of the tune heard on <paramref name="model"/> from now on - null for the
    /// chip each asks for - its sound faded down and up around the change, the tune playing on as it
    /// was. Safe from any thread; the change is made on the sound's.</summary>
    public void UseChip(ChipModel? model) => _chipAsked = new ChipChange(model);

    private void SetChips(ChipModel? model)
    {
        for (int chip = 0; chip < _chips.Length; chip++)
        {
            bool is8580 = model is { } chosen ? chosen == ChipModel.Mos8580
                : chip == 0 ? File.Is8580 : File.ExtraSids8580[chip - 1];
            _chips[chip].SetChipModel(is8580 ? ChipModel.Mos8580 : ChipModel.Mos6581);
        }
    }

    /// <summary>Paused or stopped, the change made at once: nothing is heard to fade.</summary>
    private void SwitchWhileSilent()
    {
        if (_chipChanging is { } changing && _switchedAt < 0)
            SetChips(changing.Model);
        if (Interlocked.Exchange(ref _chipAsked, null) is { } asked)
            SetChips(asked.Model);
        (_chipChanging, _switchPlanned, _switchedAt) = (null, -1, -1);
    }

    /// <summary>A change asked for planned: the sound faded down over the samples from the next to
    /// be handed over, and the chip changed after them.</summary>
    private void PlanSwitch()
    {
        if (_chipChanging is not null || Interlocked.Exchange(ref _chipAsked, null) is not { } asked)
            return;
        _chipChanging = asked;
        _switchPlanned = _handedOver + SwitchFade;
        _switchedAt = -1;
    }

    /// <summary>The chip changed once the tune has been made up to the sample planned.</summary>
    private void SwitchIfDue()
    {
        if (_chipChanging is { } changing && _switchedAt < 0 && _handedOver + _pendingCount >= _switchPlanned)
        {
            SetChips(changing.Model);
            _switchedAt = _handedOver + _pendingCount;
        }
    }

    /// <summary>The sound handed over faded down to the change and up from it: each frame by the
    /// tune's first sample in it.</summary>
    private void FadeAroundSwitch(Span<short> output, int frames, int rate, int channels)
    {
        if (_chipChanging is null)
            return;
        for (int i = 0; i < frames; i++)
        {
            long at = _handedOver + ((long)i * rate);
            float gain = at < _switchPlanned ? Math.Clamp((_switchPlanned - at) / (float)SwitchFade, 0, 1)
                : _switchedAt < 0 || at < _switchedAt ? 0
                : Math.Clamp((at - _switchedAt) / (float)SwitchFade, 0, 1);
            if (gain >= 1)
                continue;
            for (int c = 0; c < channels; c++)
                output[(i * channels) + c] = (short)(output[(i * channels) + c] * gain);
        }
    }

    /// <summary><paramref name="used"/> of the tune's samples handed over; the change over once its
    /// fade up has been.</summary>
    private void HandedOver(int used)
    {
        _handedOver += used;
        if (_switchedAt >= 0 && _handedOver >= _switchedAt + SwitchFade)
            (_chipChanging, _switchPlanned, _switchedAt) = (null, -1, -1);
    }

    /// <summary>The speed the tune can play at here: <paramref name="speed"/>, or as many of its
    /// samples to each of the device's as this computer makes in <see cref="Headroom"/> of the time
    /// a device's sample lasts - a 2SID tune, or a slower computer, tops out lower.</summary>
    private int Affordable(int speed)
    {
        if (speed <= SafeSpeed || _secondsPerSample <= 0)
            return speed;
        int most = (int)(Headroom / (SampleRate * _secondsPerSample));
        return Math.Clamp(most, SafeSpeed, speed);
    }

    void ISidPort.Write(long cycle, int chip, int register, int value)
    {
        if (chip >= _chips.Length)
            return;
        if (!_skipping)
            ClockTo(cycle);
        _chips[chip].Write(register, value);
        _registers[chip][register] = value & 0xFF;
        if (chip == 0 && register == 0x18 && !PlaysDigi)
        {
            long second = (long)(cycle / ClockHz);
            if (second != _volumeSecond)
                (_volumeSecond, _volumeWrites) = (second, 0);
            if (++_volumeWrites > DigiWritesPerSecond)
                PlaysDigi = true;
        }
    }

    void ISidPort.WriteFm(long cycle, int register, int value)
    {
        if (_fm is null)
            return;
        if (!_skipping)
            ClockTo(cycle);
        _fm.Write(register, value);
    }

    /// <summary>The Sound Expander's FM chip, for a tune that asks for it; null for none.</summary>
    private readonly FmSound? _fm;

    /// <summary>The FM chip's sample to the SIDs': one to one - on the tunes made for it, the FM chip
    /// and the SIDs then sound about as loud as each other.</summary>
    private static double FmGain = 1.0;

    /// <summary>The SIDs and the FM chip together taken down by this, so the two at their loudest do
    /// not clip: given back by the loudness after (<see cref="FmHeadroomDb"/>).</summary>
    private const double FmMix = 0.6;

    /// <summary>The headroom <see cref="FmMix"/> leaves, in dB, for the loudness to give back.</summary>
    public static readonly double FmHeadroomDb = -20 * Math.Log10(FmMix);

    /// <summary>Whether the tune is heard with a Sound Expander's FM chip.</summary>
    public bool HasFm => _fm is not null;

    /// <summary>The FM chip's next sample added to the one just made - to both sides of a stereo tune.</summary>
    private void AddFm()
    {
        double fm = _fm!.Next() * FmGain;
        _pending[_pendingCount] = (short)Math.Clamp((_pending[_pendingCount] + fm) * FmMix, short.MinValue, short.MaxValue);
        if (IsStereo)
            _pendingRight[_pendingCount] = (short)Math.Clamp((_pendingRight[_pendingCount] + fm) * FmMix, short.MinValue, short.MaxValue);
    }

    /// <summary>Whether the tune plays a digi through the volume register - known once it has.</summary>
    public bool PlaysDigi { get; private set; }

    /// <summary>The scope index of the digi: the one after the voices.</summary>
    public int DigiScope => _voiceScopes.Length;

    byte ISidPort.Read(long cycle, int chip, int register)
    {
        if (chip >= _chips.Length)
            return 0;
        if (_skipping)
        {
            // The oscillator or envelope read - the music may wait on it, or take a random number
            // from it: from here the chips run along.
            if ((register & 0x1F) is not (0x1B or 0x1C))
                return (byte)_chips[chip].Read(register);
            _skipping = false;
            _chipCycle = cycle;
        }
        ClockTo(cycle);
        return (byte)_chips[chip].Read(register);
    }

    /// <summary>The chips clocked on to <paramref name="cycle"/>, a sample at a time so each voice's
    /// own output can be read at the instant its sample is taken, as the scopes draw it. The other
    /// chips take the cycles the first took for its sample, so theirs fall on the same instant.</summary>
    private void ClockTo(long cycle)
    {
        int cycles = (int)Math.Min(int.MaxValue, cycle - _chipCycle);
        if (cycles <= 0)
            return;
        _chipCycle = cycle;

        while (cycles > 0)
        {
            if (_pendingCount == _pending.Length)
            {
                Array.Resize(ref _pending, _pending.Length * 2);
                if (IsStereo)
                    Array.Resize(ref _pendingRight, _pending.Length);
            }
            int before = cycles;
            int sampled = _chips[0].ClockAndSample(ref cycles, _pending, _pendingCount, 1);
            if (_chips.Length > 1)
                MixIn(before - cycles, sampled);
            if (sampled == 0)
                break;
            if (_fm is not null)
                AddFm();
            if (_silent)
                continue;
            Capture(IsStereo ? (short)((_pending[_pendingCount] + _pendingRight[_pendingCount]) / 2) : _pending[_pendingCount]);
            _pendingCount++;
        }
    }

    /// <summary>The other chips through the same <paramref name="cycles"/>, their sample added to the
    /// first's, the sum scaled for the number of chips.</summary>
    private void MixIn(int cycles, int sampled)
    {
        if (IsStereo)
        {
            MixInStereo(cycles, sampled);
            return;
        }
        int sum = sampled > 0 ? _pending[_pendingCount] : 0;
        for (int chip = 1; chip < _chips.Length; chip++)
        {
            int left = cycles;
            if (_chips[chip].ClockAndSample(ref left, _extraSample, 0, 1) > 0)
                sum += _extraSample[0];
        }
        if (sampled > 0)
            _pending[_pendingCount] = (short)Math.Clamp(sum * Scale(_chips.Length), short.MinValue, short.MaxValue);
    }

    /// <summary><see cref="MixIn"/> for a stereo tune: each chip's sample to the left and right as its
    /// place has it, the first chip's too - the left into <see cref="_pending"/>, the right beside it.</summary>
    private void MixInStereo(int cycles, int sampled)
    {
        double first = sampled > 0 ? _pending[_pendingCount] : 0;
        double left = first * _toLeft[0], right = first * _toRight[0];
        for (int chip = 1; chip < _chips.Length; chip++)
        {
            int run = cycles;
            if (_chips[chip].ClockAndSample(ref run, _extraSample, 0, 1) > 0)
            {
                left += _extraSample[0] * _toLeft[chip];
                right += _extraSample[0] * _toRight[chip];
            }
        }
        if (sampled > 0)
        {
            double scale = Scale(_chips.Length);
            _pending[_pendingCount] = (short)Math.Clamp(left * scale, short.MinValue, short.MaxValue);
            _pendingRight[_pendingCount] = (short)Math.Clamp(right * scale, short.MinValue, short.MaxValue);
        }
    }

    private void Capture(short mix)
    {
        lock (_scopeLock)
        {
            int at = (int)(_scopeWritten % RingSamples);
            for (int voice = 0; voice < _voiceStates.Length; voice++)
            {
                var chip = _chips[voice / 3];
                var registers = _registers[voice / 3];
                int b = voice % 3 * 7;
                _voiceScopes[voice][at] = chip.Voice(voice % 3).Output() / VoiceOutputScale;
                _voiceStates[voice][at] = registers[b] | (registers[b + 1] << 8) | (registers[b + 4] << 16)
                                          | (chip.Voice(voice % 3).Envelope.Output() << 24);
            }
            _mixScope[at] = mix / 32768f;
            _digiScope[at] = ((_registers[0][0x18] & 0x0F) - 7.5f) / 7.5f;
            _scopeWritten++;
        }
    }

    /// <summary>What a voice is doing at sample <paramref name="at"/> - the one the listener hears:
    /// its frequency register, control byte and envelope level, 0-255. Voices 3-5 are the second
    /// chip's, 6-8 the third's.</summary>
    public (int Frequency, int Control, int Envelope) VoiceAt(int voice, long at)
    {
        lock (_scopeLock)
        {
            if (_scopeWritten == 0)
                return default;
            long sample = Math.Clamp(at, Math.Max(0, _scopeWritten - RingSamples), _scopeWritten - 1);
            int state = _voiceStates[voice][(int)(sample % RingSamples)];
            return (state & 0xFFFF, (state >> 16) & 0xFF, (state >> 24) & 0xFF);
        }
    }

    /// <summary>The note a frequency register plays on this machine, C-0 as 0 - fractional, between keys.</summary>
    public double NoteOf(int frequency) =>
        frequency <= 0 ? double.NaN : 12 * Math.Log2(frequency * ClockHz / 16777216.0 / 16.3516);

    /// <summary>A voice's own output; past the last voice, the digi's.</summary>
    public void CaptureVoiceScope(int voice, float[] destination, long endingAt) =>
        CaptureRing(voice < _voiceScopes.Length ? _voiceScopes[voice] : _digiScope, destination, endingAt);

    public void CaptureMix(float[] destination, long endingAt) => CaptureRing(_mixScope, destination, endingAt);

    /// <summary>The window of <paramref name="source"/> ending at <paramref name="endingAt"/>, never
    /// past what is written nor further back than the ring holds; silence before a window exists.</summary>
    private void CaptureRing(float[] source, float[] destination, long endingAt)
    {
        int length = destination.Length;
        lock (_scopeLock)
        {
            long end = Math.Min(endingAt, _scopeWritten);
            end = Math.Max(end, Math.Min(_scopeWritten, length));
            end = Math.Max(end, _scopeWritten - RingSamples + length);
            long start = end - length;
            if (start < 0)
            {
                Array.Clear(destination);
                return;
            }

            int from = (int)(start % RingSamples);
            int first = Math.Min(length, RingSamples - from);
            Array.Copy(source, from, destination, 0, first);
            if (first < length)
                Array.Copy(source, 0, destination, first, length - first);
        }
    }
}
