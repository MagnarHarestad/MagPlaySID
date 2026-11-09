using System.Runtime.InteropServices;
using MagPlay.Amiga;
using MagPlay.C64;
using MagSid.Playback;
using NAudio.Wave;

namespace MagPlaySid.Player;

/// <summary>
/// A tune playing, whatever it is - a .sid on the C64, or an Amiga module - as the window, the
/// scopes and the meters see it: its sound, its voices - a SID's three, or a module's channels -
/// each as a SID voice would say it is: a frequency register, a control byte with the gate in bit
/// 0, and an envelope level, 0-255.
/// </summary>
internal interface ITunePlayback : IWaveProvider, IScopeSource
{
    /// <summary>How many voices: 3 a SID, or a module's channels.</summary>
    int Voices { get; }

    /// <summary>Why the tune stopped, if it did.</summary>
    string? Failure { get; }

    bool Paused { get; set; }

    /// <summary>How many times its own speed it is asked to play: 1, or more to fast-forward.</summary>
    int Speed { get; set; }

    /// <summary>How many times its own speed it is playing now; 0 while silent.</summary>
    int PlayingSpeed { get; }

    /// <summary>The listener's volume, 0-1.</summary>
    float Volume { get; set; }

    /// <summary>Whether a .sid plays a digi through the volume register - a scope of its own.</summary>
    bool PlaysDigi { get; }

    /// <summary>The tune's own sample the device is at - paused, it stands still.</summary>
    long TuneSampleAt(long device);

    void SetVoiceMuted(int voice, bool muted);

    /// <summary>What a voice is doing at the tune's sample <paramref name="at"/>, as a SID voice says it.</summary>
    (int Frequency, int Control, int Envelope) VoiceAt(int voice, long at);

    /// <summary>The note of a frequency from <see cref="VoiceAt"/>, C-0 as 0.</summary>
    double NoteOf(int frequency);
}

/// <summary>A .sid on the C64: <see cref="C64PlaybackEngine"/> as an <see cref="ITunePlayback"/>.</summary>
internal sealed class C64Tune(C64PlaybackEngine engine) : ITunePlayback
{
    public C64PlaybackEngine Engine { get; } = engine;

    public int Voices => 3 * Engine.Chips;
    public string? Failure => Engine.Failure;
    public bool Paused { get => Engine.Paused; set => Engine.Paused = value; }
    public int Speed { get => Engine.Speed; set => Engine.Speed = value; }
    public int PlayingSpeed => Engine.PlayingSpeed;
    public float Volume { get => Engine.Volume; set => Engine.Volume = value; }
    public bool PlaysDigi => Engine.PlaysDigi;
    public WaveFormat WaveFormat => Engine.WaveFormat;
    public int Read(byte[] buffer, int offset, int count) => Engine.Read(buffer, offset, count);
    public long TuneSampleAt(long device) => Engine.TuneSampleAt(device);
    public void SetVoiceMuted(int voice, bool muted) => Engine.SetVoiceMuted(voice, muted);
    public (int Frequency, int Control, int Envelope) VoiceAt(int voice, long at) => Engine.VoiceAt(voice, at);
    public double NoteOf(int frequency) => Engine.NoteOf(frequency);
    public void CaptureVoiceScope(int voice, float[] destination, long endingAt) => Engine.CaptureVoiceScope(voice, destination, endingAt);
    public void CaptureMix(float[] destination, long endingAt) => Engine.CaptureMix(destination, endingAt);
}

/// <summary>
/// A tracker module playing - ProTracker's, Scream Tracker's, FastTracker's or Impulse Tracker's:
/// its player's sound in stereo, and rings of the last
/// second and a half of each channel's sound and state, and of the order and row, to show what the
/// listener hears - the device plays a buffer behind what is made. A channel is said as a SID voice
/// is: its pitch as the frequency register that plays the same note, ProTracker's C-2 and a PC
/// tracker's middle C as C-4; its
/// volume as the envelope; sounding as the gate on - and, for a moment after a note is struck, off,
/// so a note struck again on a note is seen to start.
/// </summary>
internal sealed class ModuleTune : ITunePlayback
{
    public const int SampleRate = 44100;
    private const int RingSamples = 65536;
    private const int Chunk = 1024;
    private const float FastForwardVolume = 0.3f;

    /// <summary>Samples after a note is struck for which its gate is said to be off.</summary>
    private const int StrikeGap = 1100;

    /// <summary>The PAL C64's clock, for the frequency register that plays a note.</summary>
    private const double SidClock = 985248;

    private readonly ITrackerPlayer _player;
    private readonly float[][] _scopes;
    private readonly int[][] _states;
    private readonly int[] _rows = new int[RingSamples];
    private readonly float[] _mix = new float[RingSamples];
    private readonly float[] _left = new float[Chunk * ModPlayer_MaxSpeed], _right = new float[Chunk * ModPlayer_MaxSpeed];
    private readonly float[][] _chunkScopes;
    private readonly int[][] _chunkStates;
    private readonly int[] _chunkRows = new int[Chunk * ModPlayer_MaxSpeed];
    private readonly object _lock = new();
    private long _written;
    private readonly List<(long Device, long Tune, int Rate)> _turns = [(0, 0, 1)];
    private long _device, _tuneSample;
    private float _applied = -1;

    private const int ModPlayer_MaxSpeed = 32;

    public ModuleTune(ITrackerModule module)
    {
        Module = module;
        _player = module.CreatePlayer(SampleRate);
        int channels = module.Channels;
        _scopes = new float[channels][];
        _states = new int[channels][];
        _chunkScopes = new float[channels][];
        _chunkStates = new int[channels][];
        for (int c = 0; c < channels; c++)
        {
            _scopes[c] = new float[RingSamples];
            _states[c] = new int[RingSamples];
            _chunkScopes[c] = new float[Chunk * ModPlayer_MaxSpeed];
            _chunkStates[c] = new int[Chunk * ModPlayer_MaxSpeed];
        }
    }

    public ITrackerModule Module { get; }

    public int Voices => Module.Channels;
    public string? Failure => null;
    public bool Paused { get; set; }

    public int Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 1, ModPlayer_MaxSpeed);
    }

    private volatile int _speed = 1;

    public int PlayingSpeed => _playing;

    private volatile int _playing;

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    private volatile float _volume = 1f;

    public bool PlaysDigi => false;

    public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 2);

    /// <summary>The order position and row heard at the tune's sample <paramref name="at"/>.</summary>
    public (int Order, int Row) RowAt(long at)
    {
        var (order, row, _) = PlaceAt(at);
        return (order, row);
    }

    /// <summary>The order position and row heard at the tune's sample <paramref name="at"/>, and
    /// how far into that row, 0-1.</summary>
    public (int Order, int Row, double Into) PlaceAt(long at)
    {
        lock (_lock)
        {
            if (_written == 0)
                return (0, 0, 0);
            return TrackerModules.UnpackRow(_rows[(int)(Math.Clamp(at, Math.Max(0, _written - RingSamples), _written - 1) % RingSamples)]);
        }
    }

    /// <summary>Left and right apart, 0 mono to 1 as the Amiga plays them - heard from the next
    /// few milliseconds rendered.</summary>
    public double StereoWidth
    {
        get => _player.StereoSeparation;
        set => _player.StereoSeparation = Math.Clamp(value, 0, 1);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int frames = count / 4;
        int rate = Paused ? 0 : _speed;
        _playing = rate;
        lock (_turns)
        {
            if (_turns[^1].Rate != rate)
                _turns.Add((_device, _tuneSample, rate));
            if (_turns.Count > 256)
                _turns.RemoveRange(0, _turns.Count - 256);
            _device += frames;
            _tuneSample += (long)frames * rate;
        }

        var output = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(offset, frames * 4));
        if (rate == 0)
        {
            output.Clear();
            return frames * 4;
        }

        float volume = _volume, gain = _applied < 0 ? volume : _applied;
        float step = (volume - gain) / Math.Max(1, frames);
        for (int done = 0; done < frames;)
        {
            int n = Math.Min(Chunk, frames - done), made = n * rate;
            _player.Render(_left.AsSpan(0, made), _right.AsSpan(0, made), _chunkScopes, _chunkStates, _chunkRows);
            Capture(made);
            for (int i = 0; i < n; i++)
            {
                float l = 0, r = 0;
                for (int j = 0; j < rate; j++)
                {
                    l += _left[i * rate + j];
                    r += _right[i * rate + j];
                }
                float scale = rate == 1 ? 1 : FastForwardVolume / rate;
                gain += step;
                output[(done + i) * 2] = (short)Math.Clamp(l * scale * gain * 30000, short.MinValue, short.MaxValue);
                output[(done + i) * 2 + 1] = (short)Math.Clamp(r * scale * gain * 30000, short.MinValue, short.MaxValue);
            }
            done += n;
        }
        _applied = volume;
        return frames * 4;
    }

    private void Capture(int made)
    {
        lock (_lock)
        {
            // Into the rings a block at a time - in two where a ring wraps.
            for (int done = 0; done < made;)
            {
                int at = (int)((_written + done) % RingSamples);
                int n = Math.Min(made - done, RingSamples - at);
                for (int c = 0; c < _scopes.Length; c++)
                {
                    Array.Copy(_chunkScopes[c], done, _scopes[c], at, n);
                    Array.Copy(_chunkStates[c], done, _states[c], at, n);
                }
                Array.Copy(_chunkRows, done, _rows, at, n);
                for (int i = 0; i < n; i++)
                    _mix[at + i] = (_left[done + i] + _right[done + i]) * 0.5f;
                done += n;
            }
            _written += made;
        }
    }

    public long TuneSampleAt(long device)
    {
        lock (_turns)
        {
            // Searched by hand: a lambda would capture the device and be made anew each frame.
            int at = _turns.Count - 1;
            while (at > 0 && _turns[at].Device > device)
                at--;
            var (from, tune, rate) = _turns[Math.Max(0, at)];
            return tune + Math.Max(0, device - from) * rate;
        }
    }

    public void SetVoiceMuted(int voice, bool muted) => _player.SetMuted(voice, muted);

    public (int Frequency, int Control, int Envelope) VoiceAt(int voice, long at)
    {
        lock (_lock)
        {
            if (_written == 0 || voice >= _states.Length)
                return default;
            long sample = Math.Clamp(at, Math.Max(0, _written - RingSamples), _written - 1);
            var state = TrackerModules.Unpack(_states[voice][(int)(sample % RingSamples)]);
            bool struck = false;
            for (long s = sample; s > sample - StrikeGap && s >= Math.Max(0, _written - RingSamples) && !struck; s -= 64)
                struck = TrackerModules.Struck(_states[voice][(int)(s % RingSamples)]);
            int envelope = state.Sounding ? state.Volume * 255 / 64 : 0;
            int control = 0x40 | (state.Sounding && !struck ? 1 : 0);
            return (state.Sounding || state.Note > 0 ? FrequencyOf(state.Note) : 0, control, envelope);
        }
    }

    /// <summary>The sample a channel is sounding at the tune's sample <paramref name="at"/>; 0 for none.</summary>
    public int SampleAt(int channel, long at)
    {
        lock (_lock)
        {
            if (_written == 0 || channel >= _states.Length)
                return 0;
            var state = TrackerModules.Unpack(_states[channel][(int)(Math.Clamp(at, Math.Max(0, _written - RingSamples), _written - 1) % RingSamples)]);
            return state.Sounding ? state.Sample : 0;
        }
    }

    /// <summary>The SID frequency register playing a note - in semitones, C-4 as 48.</summary>
    private static int FrequencyOf(double note)
    {
        if (note <= 0)
            return 0;
        double hertz = 16.3516 * Math.Pow(2, note / 12);
        return (int)Math.Clamp(Math.Round(hertz * 16777216 / SidClock), 1, 65535);
    }

    public double NoteOf(int frequency) =>
        frequency <= 0 ? double.NaN : 12 * Math.Log2(frequency * SidClock / 16777216.0 / 16.3516);

    public void CaptureVoiceScope(int voice, float[] destination, long endingAt) =>
        CaptureRing(voice < _scopes.Length ? _scopes[voice] : _mix, destination, endingAt);

    public void CaptureMix(float[] destination, long endingAt) => CaptureRing(_mix, destination, endingAt);

    private void CaptureRing(float[] source, float[] destination, long endingAt)
    {
        int length = destination.Length;
        lock (_lock)
        {
            long end = Math.Min(endingAt, _written);
            end = Math.Max(end, Math.Min(_written, length));
            end = Math.Max(end, _written - RingSamples + length);
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
