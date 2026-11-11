using System.Runtime.InteropServices;
using MagPlay.Audio;
using NAudio.Wave;

namespace MagPlaySid.Player;

/// <summary>A tune that can say every note sounding at once, for the piano: a recording's notes as
/// heard. Keys are the piano's: C-0 as 0.</summary>
internal interface IPolyphonicTune
{
    /// <summary>The keys sounding at the tune's sample <paramref name="at"/> and how strongly, 0-1,
    /// into <paramref name="keys"/>; how many.</summary>
    int KeysAt(long at, Span<(int Key, float Strength)> keys);
}

/// <summary>
/// A recording playing - WAV, FLAC or MP3 - as the window sees a tune: its sound read a few seconds
/// ahead on a thread of its own, so a slow disk or a file still coming down from the cloud never
/// keeps the sound card waiting; made 44.1 kHz stereo whatever it was; and the last second and a
/// half of each side and the mix kept, for the scopes. Its two voices are the left and the right.
/// The notes heard in it - given as the transcription finds them - say what the piano lights and
/// what the meters dance to: the highest note sounding as the first voice, the lowest as the second.
/// </summary>
internal sealed class AudioTune : ITunePlayback, IPolyphonicTune, IDisposable
{
    public const int SampleRate = 44100;
    private const int Ahead = 1 << 18;
    private const int RingSamples = 65536;
    private const float FastForwardVolume = 0.3f;
    private const double SidClock = 985248;

    /// <summary>Seconds after a note starts for which its voice's gate is said to be off, so a
    /// note struck again is seen to start.</summary>
    private const double StrikeGap = 0.025;

    private readonly string _path;
    private readonly Thread _reader;
    private volatile bool _stopping;
    private readonly AutoResetEvent _wanted = new(false);

    // Read ahead: stereo frames, written by the reader thread and taken by the device's.
    private readonly float[] _ahead = new float[Ahead * 2];
    private long _aheadWritten, _aheadTaken;
    private volatile bool _ended;
    private volatile string? _failure;

    // What was heard, for the scopes and the clock.
    private readonly object _lock = new();
    private readonly float[] _left = new float[RingSamples], _right = new float[RingSamples], _mix = new float[RingSamples];
    private long _written;
    private readonly List<(long Device, long Tune, int Rate)> _turns = [(0, 0, 1)];
    private long _device, _tuneSample;
    private float _applied = -1;
    private readonly bool[] _muted = new bool[2];

    // The notes heard, in order of their start, and the longest of them - how far back to look.
    private IReadOnlyList<HeardNote> _notes = [];
    private double _longest;

    /// <param name="path">The recording, opened here and again by the reader.</param>
    /// <param name="start">Where it starts, in samples at <see cref="SampleRate"/>: past 0 for a jump.</param>
    public AudioTune(string path, AudioDecoder decoder, long start = 0)
    {
        _path = path;
        if (start > 0)
        {
            decoder.Skip((long)Math.Round(start * (double)decoder.SampleRate / SampleRate));
            _turns[0] = (0, start, 1);
            _tuneSample = _written = start;
        }
        SourceRate = decoder.SampleRate;
        SourceChannels = decoder.Channels;
        Duration = decoder.Duration;
        Description = decoder.Description;
        _reader = new Thread(() => ReadAhead(decoder)) { IsBackground = true, Name = "Recording read-ahead", Priority = ThreadPriority.AboveNormal };
        _reader.Start();
        // A moment of it ready before the device asks - a quarter second - but not waited for long:
        // this is the window's thread, and a file still coming off iCloud held it two seconds. Read
        // late, it is silent and stands still till it is read.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Interlocked.Read(ref _aheadWritten) < SampleRate / 4 && !_ended && waited.ElapsedMilliseconds < 100)
            Thread.Sleep(2);
    }

    public int SourceRate { get; }
    public int SourceChannels { get; }
    public TimeSpan? Duration { get; }
    public string Description { get; }

    public int Voices => 2;
    public string? Failure => _failure;
    public bool Paused { get; set; }

    public int Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 1, 32);
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

    /// <summary>The notes heard so far, in order of their start: given again as more are heard.</summary>
    public void SetNotes(IReadOnlyList<HeardNote> notes)
    {
        double longest = 0;
        foreach (var note in notes)
            longest = Math.Max(longest, note.End - note.Start);
        (_notes, _longest) = (notes, longest);
    }

    // ------------------------------------------------------------------ reading ahead

    private void ReadAhead(AudioDecoder decoder)
    {
        try
        {
            int channels = decoder.Channels;
            var resampler = new Resampler(decoder.SampleRate, SampleRate, 2);
            var read = new float[4096 * channels];
            var stereo = new float[4096 * 2];
            var made = new float[16384];
            while (!_stopping)
            {
                int got = decoder.Read(read);
                int count;
                if (got == 0)
                {
                    count = resampler.Flush(ref made);
                    Put(made.AsSpan(0, count));
                    break;
                }
                int frames = got / channels;
                for (int i = 0; i < frames; i++)
                {
                    // Mono to both sides; more than two channels, the first two.
                    float l = read[i * channels], r = channels > 1 ? read[(i * channels) + 1] : l;
                    stereo[2 * i] = l;
                    stereo[(2 * i) + 1] = r;
                }
                count = resampler.Process(stereo.AsSpan(0, frames * 2), ref made);
                Put(made.AsSpan(0, count));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException
                                       or IndexOutOfRangeException or ArgumentException or NotSupportedException)
        {
            _failure = $"The recording cannot be read on: {ex.Message}";
        }
        finally
        {
            decoder.Dispose();
            _ended = true;
        }
    }

    /// <summary>Stereo samples into the read-ahead, waiting while it is full.</summary>
    private void Put(ReadOnlySpan<float> samples)
    {
        int frames = samples.Length / 2, at = 0;
        while (at < frames && !_stopping)
        {
            long free = Ahead - (Interlocked.Read(ref _aheadWritten) - Interlocked.Read(ref _aheadTaken));
            if (free <= 0)
            {
                _wanted.WaitOne(50);
                continue;
            }
            int take = (int)Math.Min(free, frames - at);
            long written = Interlocked.Read(ref _aheadWritten);
            for (int i = 0; i < take; i++)
            {
                int slot = (int)((written + i) % Ahead) * 2;
                _ahead[slot] = samples[(at + i) * 2];
                _ahead[slot + 1] = samples[((at + i) * 2) + 1];
            }
            Interlocked.Add(ref _aheadWritten, take);
            at += take;
        }
    }

    // ------------------------------------------------------------------ the device's side

    public int Read(byte[] buffer, int offset, int count)
    {
        int frames = count / 4;
        int rate = Paused ? 0 : _speed;
        _playing = rate;
        var output = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(offset, frames * 4));
        if (rate == 0)
        {
            Turn(rate);
            lock (_turns)
                _device += frames;
            output.Clear();
            return frames * 4;
        }

        float volume = _volume, gain = _applied < 0 ? volume : _applied;
        float step = (volume - gain) / Math.Max(1, frames);
        float scale = rate == 1 ? 1 : FastForwardVolume / rate;
        Turn(rate);
        lock (_lock)
        {
            for (int i = 0; i < frames; i++)
            {
                long available = Interlocked.Read(ref _aheadWritten) - _aheadTaken;
                if (available < rate && !_ended)
                {
                    // Not read in time: silence, and the tune stands still until it is.
                    output[(i * 2)..].Clear();
                    lock (_turns)
                    {
                        _device += i;
                        _tuneSample += (long)i * rate;
                        _turns.Add((_device, _tuneSample, 0));
                        _device += frames - i;
                    }
                    _wanted.Set();
                    _applied = gain;
                    return frames * 4;
                }
                float l = 0, r = 0;
                for (int j = 0; j < rate; j++)
                {
                    float sl = 0, sr = 0;
                    if (_aheadTaken < Interlocked.Read(ref _aheadWritten))
                    {
                        int slot = (int)(_aheadTaken % Ahead) * 2;
                        sl = _ahead[slot];
                        sr = _ahead[slot + 1];
                        _aheadTaken++;
                    }
                    int at = (int)(_written % RingSamples);
                    _left[at] = sl;
                    _right[at] = sr;
                    _mix[at] = (sl + sr) * 0.5f;
                    _written++;
                    l += sl;
                    r += sr;
                }
                gain += step;
                float lv = _muted[0] ? 0 : l * scale * gain, rv = _muted[1] ? 0 : r * scale * gain;
                output[i * 2] = (short)Math.Clamp(lv * 32767, short.MinValue, short.MaxValue);
                output[(i * 2) + 1] = (short)Math.Clamp(rv * 32767, short.MinValue, short.MaxValue);
            }
        }
        lock (_turns)
        {
            _device += frames;
            _tuneSample += (long)frames * rate;
        }
        _wanted.Set();
        _applied = volume;
        return frames * 4;
    }

    private void Turn(int rate)
    {
        lock (_turns)
        {
            if (_turns[^1].Rate != rate)
                _turns.Add((_device, _tuneSample, rate));
            if (_turns.Count > 256)
                _turns.RemoveRange(0, _turns.Count - 256);
        }
    }

    public long TuneSampleAt(long device)
    {
        lock (_turns)
        {
            int at = _turns.Count - 1;
            while (at > 0 && _turns[at].Device > device)
                at--;
            var (from, tune, rate) = _turns[at];
            return tune + (Math.Max(0, device - from) * rate);
        }
    }

    public void SetVoiceMuted(int voice, bool muted)
    {
        if ((uint)voice < 2)
            _muted[voice] = muted;
    }

    public void CaptureVoiceScope(int voice, float[] destination, long endingAt) =>
        CaptureRing(voice == 0 ? _left : voice == 1 ? _right : _mix, destination, endingAt);

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

    // ------------------------------------------------------------------ the notes

    /// <summary>The notes sounding at <paramref name="seconds"/>, into <paramref name="into"/>; how many.</summary>
    public int NotesAt(double seconds, Span<HeardNote> into)
    {
        var notes = _notes;
        double longest = _longest;
        // The last note starting by then, and back from it as far as the longest note reaches.
        int low = 0, high = notes.Count - 1, last = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (notes[mid].Start <= seconds)
            {
                last = mid;
                low = mid + 1;
            }
            else
                high = mid - 1;
        }
        int count = 0;
        for (int i = last; i >= 0 && count < into.Length; i--)
        {
            var note = notes[i];
            if (note.Start < seconds - longest - 0.001)
                break;
            if (note.End > seconds)
                into[count++] = note;
        }
        return count;
    }

    public int KeysAt(long at, Span<(int Key, float Strength)> keys)
    {
        Span<HeardNote> sounding = stackalloc HeardNote[32];
        int count = NotesAt(at / (double)SampleRate, sounding);
        int n = 0;
        for (int i = 0; i < count && n < keys.Length; i++)
            keys[n++] = (sounding[i].Pitch - 12, 0.35f + (0.65f * sounding[i].Strength));
        return n;
    }

    /// <summary>The first voice the highest note sounding, the second the lowest - as a SID voice
    /// says it, so the meters that dance to notes dance to these.</summary>
    public (int Frequency, int Control, int Envelope) VoiceAt(int voice, long at)
    {
        double seconds = at / (double)SampleRate;
        Span<HeardNote> sounding = stackalloc HeardNote[32];
        int count = NotesAt(seconds, sounding);
        if (count == 0)
            return default;
        var chosen = sounding[0];
        for (int i = 1; i < count; i++)
            if (voice == 0 ? sounding[i].Pitch > chosen.Pitch : sounding[i].Pitch < chosen.Pitch)
                chosen = sounding[i];
        bool struck = seconds - chosen.Start < StrikeGap;
        int envelope = (int)(90 + (165 * chosen.Strength));
        return (FrequencyOf(chosen.Pitch - 12), 0x40 | (struck ? 0 : 1), envelope);
    }

    /// <summary>The SID frequency register that plays the piano's key, C-0 as 0.</summary>
    private static int FrequencyOf(int key)
    {
        double hertz = 16.3516 * Math.Pow(2, key / 12.0);
        return (int)Math.Clamp(Math.Round(hertz * 16777216 / SidClock), 1, 65535);
    }

    public double NoteOf(int frequency) =>
        frequency <= 0 ? double.NaN : 12 * Math.Log2(frequency * SidClock / 16777216.0 / 16.3516);

    public void Dispose()
    {
        _stopping = true;
        _wanted.Set();
        _reader.Join(1000);
        _wanted.Dispose();
    }
}
