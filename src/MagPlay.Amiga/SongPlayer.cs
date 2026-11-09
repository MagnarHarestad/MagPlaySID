namespace MagPlay.Amiga;

/// <summary>
/// A Scream Tracker 3, FastTracker 2 or Impulse Tracker module played as its tracker plays it:
/// rows of a pattern every so many ticks, a tick 2.5 / tempo seconds; each channel's notes played
/// through its instrument - which sample, its envelopes for volume, panning and pitch, its fadeout -
/// or straight from a sample; the effects and the volume column on the row's first tick or on the
/// ticks after it, each tracker's way, with each effect's own memory of its last parameter. A note
/// struck over one still sounding cuts it - or, by an Impulse Tracker instrument's new note action,
/// leaves it sounding on, released or fading, in the background. Voices change volume and panning
/// over a millisecond and a half, as trackers ramp them, so nothing clicks.
/// </summary>
public sealed class SongPlayer : ITrackerPlayer
{
    private enum Fx : byte
    {
        None, Arpeggio, PortaUp, PortaDown, FinePortaUp, FinePortaDown, ExtraFineUp, ExtraFineDown, TonePorta,
        Vibrato, FineVibrato, TonePortaVol, VibratoVol, Tremolo, Tremor, SetPan, PanSlide, Offset, VolSlide,
        FineVolUp, FineVolDown, Jump, SetVolume, Break, Speed, Tempo, GlobalVolume, GlobalVolSlide, KeyOff,
        EnvelopePos, Retrig, ChannelVolume, ChannelVolSlide, Panbrello, NoteCut, NoteDelay, PatternDelay,
        FinePatternDelay, PatternLoop, VibratoWave, TremoloWave, PanbrelloWave, InstrumentControl, HighOffset, Stop, Filter,
    }

    // The memories effects recall a parameter from when theirs is 0.
    private enum Slot
    {
        VolSlide, PortaUp, PortaDown, FinePortaUp, FinePortaDown, ExtraUp, ExtraDown, TonePorta, Vibrato,
        Tremolo, Tremor, Offset, PanSlide, Retrig, GlobalVolSlide, ChannelVolSlide, Panbrello, Arpeggio,
        Tempo, VolumeColumn, FineVolUp, FineVolDown, Special, Count,
    }

    private static readonly int[] RetrigAdd = [0, -1, -2, -4, -8, -16, 0, 0, 0, 1, 2, 4, 8, 16, 0, 0];
    private static readonly int[] PortaTable = [0, 1, 4, 8, 16, 32, 64, 96, 128, 255];

    private readonly Song _song;
    private readonly int _rate;
    private readonly Channel[] _channels;
    private readonly List<Voice> _voices = [];
    private readonly int _rampSamples;

    private int _speed, _tempo, _globalVolume, _tick;
    private int _order, _row;
    private int _patternDelay, _delayLeft, _extraTicks;
    private bool _jump, _loopJump;
    private int _jumpOrder, _jumpRow;
    private double _tickSamplesLeft, _tickSamples = 1;
    private int _heardOrder, _heardRow, _rowTick, _rowTicks = 6;
    private readonly HashSet<int> _visited = [];

    public SongPlayer(Song song, int sampleRate = 44100, double stereoSeparation = 0.6)
    {
        _song = song;
        _rate = sampleRate;
        StereoSeparation = stereoSeparation;
        _rampSamples = Math.Max(1, sampleRate * 3 / 2000);
        _channels = new Channel[song.Channels];
        for (int c = 0; c < _channels.Length; c++)
        {
            _channels[c] = new Channel
            {
                Index = c,
                Pan = c < song.ChannelPan.Count ? song.ChannelPan[c] : 128,
                ChannelVolume = c < song.ChannelVolume.Count ? song.ChannelVolume[c] : 64,
            };
        }
        (_speed, _tempo, _globalVolume) = (song.InitialSpeed, song.InitialTempo, song.GlobalVolume);
        _order = Settle(0, out _);
        StartRow();
    }

    public double StereoSeparation { get; set; }

    public bool Looped { get; private set; }

    public bool Stopped { get; private set; }

    public void SetMuted(int channel, bool muted)
    {
        if ((uint)channel < (uint)_channels.Length)
            _channels[channel].Muted = muted;
    }

    public double SilentTick()
    {
        // Only the song's timing is wanted: no voice is worked out, and none is kept.
        _silent = true;
        NextTick();
        _voices.Clear();
        return 2.5 / _tempo;
    }

    private bool _silent;

    // ------------------------------------------------------------------ the sound

    public void Render(Span<float> left, Span<float> right, float[][]? channels = null, int[][]? states = null, int[]? rows = null, int at = 0)
    {
        double sep = Math.Clamp(StereoSeparation, 0, 1);
        // Loud enough beside a ProTracker module, with headroom for many channels: the song's own
        // mixing volume, as its tracker sets it.
        double scale = _song.Level * _song.MixVolume / 128.0;
        int frames = left.Length;
        if (_mixLeft.Length < frames)
            (_mixLeft, _mixRight) = (new double[frames], new double[frames]);

        // Tick by tick: between two ticks nothing but the voices moves, so each voice is mixed a
        // run at a time - the voices added in the same order as one frame at a time, so the sound
        // is the same to the last bit.
        for (int f = 0; f < frames;)
        {
            if (_tickSamplesLeft <= 0)
            {
                if (!Stopped)
                    NextTick();
                UpdateTargets(sep);
                _tickSamples = _rate * 2.5 / _tempo;
                _tickSamplesLeft += _tickSamples;
            }

            // The frames up to the next tick: each counts one off, and the tick comes on the frame
            // that finds none left.
            int run = 0;
            double before = _tickSamplesLeft;
            do
            {
                _tickSamplesLeft--;
                run++;
            }
            while (_tickSamplesLeft > 0 && f + run < frames);

            Array.Clear(_mixLeft, f, run);
            Array.Clear(_mixRight, f, run);
            if (channels is not null)
                for (int c = 0; c < _channels.Length; c++)
                    Array.Clear(channels[c], at + f, run);
            for (int i = 0; i < _voices.Count; i++)
            {
                var v = _voices[i];
                v.Mix(_mixLeft, _mixRight, f, run, channels?[v.Channel], at + f, _channels[v.Channel].Muted);
            }
            if (states is not null)
                for (int c = 0; c < _channels.Length; c++)
                    states[c].AsSpan(at + f, run).Fill(_channels[c].State);
            if (rows is not null)
            {
                double countLeft = before;
                for (int j = 0; j < run; j++)
                {
                    countLeft--;
                    double into = (_rowTick + 1 - Math.Max(0, countLeft) / _tickSamples) / _rowTicks;
                    rows[at + f + j] = TrackerModules.PackRow(_heardOrder, _heardRow, into);
                }
            }
            for (int j = f; j < f + run; j++)
            {
                left[j] = (float)(_mixLeft[j] * scale);
                right[j] = (float)(_mixRight[j] * scale);
            }
            f += run;
        }
        _voices.RemoveAll(v => v.Gone);
    }

    /// <summary>The voices' sum, left and right, for the frames being rendered.</summary>
    private double[] _mixLeft = [], _mixRight = [];

    /// <summary>Each voice's volume and panning for the tick, ramped to from where they are; each
    /// channel's state for the meters.</summary>
    private void UpdateTargets(double separation)
    {
        _voices.RemoveAll(v => v.Gone);
        foreach (var v in _voices)
        {
            double gain = v.Done ? 0 : v.Gain * _globalVolume / 128.0;
            double pan = 128 + ((v.FinalPan - 128) * separation);
            double targetLeft = gain * (256 - pan) / 256 * 2, targetRight = gain * pan / 256 * 2;
            if (!_song.Stereo)
                targetLeft = targetRight = gain;
            if (v.Fresh)
            {
                // A new note starts at once - its own attack is in the sample - with no ramp up.
                (v.GainLeft, v.GainRight, v.Fresh) = (targetLeft, targetRight, false);
                v.Ramp = 0;
                continue;
            }
            v.StepLeft = (targetLeft - v.GainLeft) / _rampSamples;
            v.StepRight = (targetRight - v.GainRight) / _rampSamples;
            v.Ramp = _rampSamples;
        }
        foreach (var c in _channels)
        {
            var v = c.Voice;
            bool sounding = v is { Done: false, Ended: false } && v.Gain > 0;
            double note = v is not null && v.Hertz > 0 && v.Sample is { } sample ? 48 + 12 * Math.Log2(v.Hertz / sample.BaseRate) : 0;
            c.State = TrackerModules.Pack(c.Instrument, note, (int)Math.Round(Math.Min(1, v?.Gain ?? 0) * 64), sounding, c.Struck);
        }
    }

    // ------------------------------------------------------------------ ticks and rows

    private void NextTick()
    {
        foreach (var c in _channels)
            c.Struck = false;
        if (_tick == 0 && _delayLeft == 0)
        {
            (_heardOrder, _heardRow, _rowTick) = (_order, _row, 0);
            _extraTicks = 0;
            ReadRow();
            _rowTicks = Math.Max(1, (_speed + _extraTicks) * (1 + _patternDelay));
        }
        else
        {
            _rowTick++;
            foreach (var c in _channels)
            {
                if (c.DelayTick == _tick && c.Delayed is { } cell)
                {
                    c.Delayed = null;
                    PlayCell(c, cell, firstTick: true);
                }
                TickEffects(c);
            }
        }
        if (!_silent)
        {
            foreach (var c in _channels)
                Pitch(c);
            foreach (var v in _voices)
                v.Tick(this);
        }

        if (++_tick < _speed + _extraTicks)
            return;
        _tick = 0;
        // A pattern delay plays the row as many times again - effects going on, notes not struck
        // again - and then the next: not the same row read again, which delays it once more.
        if (_delayLeft > 0)
        {
            if (--_delayLeft > 0)
                return;
        }
        else if (_patternDelay > 0)
        {
            _delayLeft = _patternDelay;
            _patternDelay = 0;
            return;
        }
        NextRow();
    }

    private int RowsHere => _song.RowsIn(_song.Order[_order]);

    private void NextRow()
    {
        int order = _order, row = _row + 1;
        if (_jump)
        {
            (order, row) = (_jumpOrder, _jumpRow);
            if (_loopJump)
            {
                for (int r = row; r <= _row; r++)
                    _visited.Remove(Key(_order, r));
            }
        }
        else if (row >= RowsHere)
        {
            row = 0;
            order++;
        }
        order = Settle(order, out bool wrapped);
        if (wrapped)
            Looped = true;
        if (row >= _song.RowsIn(_song.Order[order]))
            row = 0;
        (_order, _row) = (order, row);
        _jump = _loopJump = false;
        StartRow();
    }

    /// <summary>The order position <paramref name="order"/> comes to: markers skipped, the end
    /// and its marker round to the restart position.</summary>
    private int Settle(int order, out bool wrapped)
    {
        wrapped = false;
        var list = _song.Order;
        for (int guard = 0; guard < 2 * list.Count + 2; guard++)
        {
            if (order >= list.Count || list[order] == 255)
            {
                order = _song.Restart < list.Count ? _song.Restart : 0;
                wrapped = true;
                continue;
            }
            if (list[order] == 254 || list[order] >= _song.PatternCount)
            {
                order++;
                continue;
            }
            return order;
        }
        Stopped = true;
        return 0;
    }

    private void StartRow()
    {
        if (!_visited.Add(Key(_order, _row)))
            Looped = true;
    }

    private static int Key(int order, int row) => (order << 9) | row;

    private void ReadRow()
    {
        int pattern = _song.Order[_order];
        for (int ch = 0; ch < _channels.Length; ch++)
        {
            var c = _channels[ch];
            var cell = _song.Cell(pattern, _row, ch);
            c.Delayed = null;
            c.DelayTick = -1;
            var (fx, p) = Translate(cell);
            if (fx == Fx.NoteDelay && p > 0)
            {
                // The whole cell waits: note, instrument and volume.
                c.Delayed = cell;
                c.DelayTick = p;
                (c.Fx, c.Param, c.VolumeFx) = (Fx.None, 0, 0);
                continue;
            }
            PlayCell(c, cell, firstTick: true);
        }
    }

    // ------------------------------------------------------------------ a cell

    /// <summary>The effect a cell's column says, as one of ours with its parameter.</summary>
    private (Fx, int) Translate(SongCell cell)
    {
        int p = cell.Parameter, x = p >> 4, y = p & 0x0F;
        if (_song.Type == SongFormat.Xm)
        {
            return cell.Effect switch
            {
                0 => p != 0 ? (Fx.Arpeggio, p) : (Fx.None, 0),
                1 => (Fx.PortaUp, p),
                2 => (Fx.PortaDown, p),
                3 => (Fx.TonePorta, p),
                4 => (Fx.Vibrato, p),
                5 => (Fx.TonePortaVol, p),
                6 => (Fx.VibratoVol, p),
                7 => (Fx.Tremolo, p),
                8 => (Fx.SetPan, p),
                9 => (Fx.Offset, p),
                10 => (Fx.VolSlide, p),
                11 => (Fx.Jump, p),
                12 => (Fx.SetVolume, Math.Min(64, p)),
                13 => (Fx.Break, (x * 10) + y),
                14 => x switch
                {
                    1 => (Fx.FinePortaUp, y),
                    2 => (Fx.FinePortaDown, y),
                    4 => (Fx.VibratoWave, y),
                    6 => (Fx.PatternLoop, y),
                    7 => (Fx.TremoloWave, y),
                    8 => (Fx.SetPan, y * 17),
                    9 => (Fx.Retrig, y),
                    0xA => (Fx.FineVolUp, y),
                    0xB => (Fx.FineVolDown, y),
                    0xC => (Fx.NoteCut, y),
                    0xD => (Fx.NoteDelay, y),
                    0xE => (Fx.PatternDelay, y),
                    _ => (Fx.None, 0),
                },
                15 => p == 0 ? (Fx.None, 0) : p < 32 ? (Fx.Speed, p) : (Fx.Tempo, p),
                16 => (Fx.GlobalVolume, Math.Min(64, p) * 2),
                17 => (Fx.GlobalVolSlide, p),
                20 => (Fx.KeyOff, p),
                21 => (Fx.EnvelopePos, p),
                25 => (Fx.PanSlide, p),
                27 => (Fx.Retrig, p | 0x100),
                29 => (Fx.Tremor, p),
                33 => x == 1 ? (Fx.ExtraFineUp, y) : x == 2 ? (Fx.ExtraFineDown, y) : (Fx.None, 0),
                _ => (Fx.None, 0),
            };
        }
        bool it = _song.Type == SongFormat.It;
        return cell.Effect switch
        {
            1 => (Fx.Speed, p),
            2 => (Fx.Jump, p),
            3 => (Fx.Break, it ? p : (x * 10) + y),
            4 => (Fx.VolSlide, p),
            5 => (Fx.PortaDown, p),
            6 => (Fx.PortaUp, p),
            7 => (Fx.TonePorta, p),
            8 => (Fx.Vibrato, p),
            9 => (Fx.Tremor, p),
            10 => (Fx.Arpeggio, p),
            11 => (Fx.VibratoVol, p),
            12 => (Fx.TonePortaVol, p),
            13 => it ? (Fx.ChannelVolume, p) : (Fx.None, 0),
            14 => it ? (Fx.ChannelVolSlide, p) : (Fx.None, 0),
            15 => (Fx.Offset, p),
            16 => it ? (Fx.PanSlide, p) : (Fx.None, 0),
            17 => (Fx.Retrig, p),
            18 => (Fx.Tremolo, p),
            19 => Special(p),
            20 => (Fx.Tempo, p),
            21 => (Fx.FineVibrato, p),
            22 => (Fx.GlobalVolume, it ? Math.Min(128, p) : Math.Min(64, p) * 2),
            23 => it ? (Fx.GlobalVolSlide, p) : (Fx.None, 0),
            24 => (Fx.SetPan, it ? p : p <= 0x80 ? Math.Min(255, p * 2) : 128),
            25 => it ? (Fx.Panbrello, p) : (Fx.None, 0),
            26 => it ? (Fx.Filter, p) : (Fx.None, 0),
            _ => (Fx.None, 0),
        };
    }

    private (Fx, int) Special(int p)
    {
        int x = p >> 4, y = p & 0x0F;
        return x switch
        {
            3 => (Fx.VibratoWave, y),
            4 => (Fx.TremoloWave, y),
            5 => (Fx.PanbrelloWave, y),
            6 => (Fx.FinePatternDelay, y),
            7 => (Fx.InstrumentControl, y),
            8 => (Fx.SetPan, y * 17),
            0xA => (Fx.HighOffset, y),
            0xB => (Fx.PatternLoop, y),
            0xC => (Fx.NoteCut, y),
            0xD => (Fx.NoteDelay, y),
            0xE => (Fx.PatternDelay, y),
            _ => (Fx.None, 0),
        };
    }

    /// <summary>A parameter, or the slot's memory of the last when it is 0. Scream Tracker keeps one
    /// memory for most of its effects; FastTracker and Impulse Tracker one for each.</summary>
    private int Recall(Channel c, Slot slot, int p)
    {
        if (_song.Type == SongFormat.S3m && slot is Slot.VolSlide or Slot.PortaUp or Slot.PortaDown or Slot.Tremor
                or Slot.Arpeggio or Slot.Retrig or Slot.Tremolo)
            slot = Slot.Special;
        if (_song.Type == SongFormat.It && slot is Slot.PortaDown)
            slot = Slot.PortaUp;
        if (_song.Type == SongFormat.It && _song.CompatibleGxx && slot == Slot.TonePorta)
            slot = Slot.PortaUp;
        if (p != 0)
            c.Memory[(int)slot] = p;
        return c.Memory[(int)slot];
    }

    private void PlayCell(Channel c, SongCell cell, bool firstTick)
    {
        var (fx, p) = Translate(cell);
        (c.Fx, c.Param) = (fx, p);
        int volume = cell.Volume;
        bool xm = _song.Type == SongFormat.Xm;
        bool tonePorta = fx is Fx.TonePorta or Fx.TonePortaVol
            || (xm && volume >= 0xF0) || (!xm && volume is >= 193 and <= 202);

        if (cell.Instrument > 0)
            c.Instrument = cell.Instrument;

        SongSample? sample = null;
        SongInstrument? instrument = null;
        int note = 0;
        if (cell.Note is >= 1 and <= 120)
        {
            (sample, instrument, note) = Resolve(c, cell.Note);
            if (tonePorta && c.Voice is { Done: false } playing && playing.Sample is { } current)
            {
                c.TargetPeriod = PeriodOf(note, current);
                sample = null;
            }
            else if (sample is not null)
            {
                Trigger(c, sample, instrument, note);
            }
        }
        else if (cell.Note == SongCell.NoteOff)
            KeyOff(c);
        else if (cell.Note == SongCell.NoteCut)
            c.Voice?.Stop();
        else if (cell.Note == SongCell.NoteFade)
            c.Voice?.StartFade();

        // An instrument named resets the volume - and the panning its sample or instrument has.
        if (cell.Instrument > 0)
        {
            var (s, i, _) = Resolve(c, cell.Note is >= 1 and <= 120 ? cell.Note : Math.Max(1, c.Note));
            s ??= c.Voice?.Sample;
            if (s is not null)
            {
                c.Volume = s.Volume;
                if (i is { Pan: >= 0 })
                    c.Pan = i.Pan;
                else if (s.Pan >= 0)
                    c.Pan = s.Pan;
            }
            if (xm && c.Voice is { } v && cell.Note == 0)
                v.ResetEnvelopes();
        }

        VolumeColumn(c, volume, firstTick: true);
        FirstTick(c, fx, p);
    }

    /// <summary>The sample a note plays on the channel, the instrument it goes through, and the note
    /// the sample plays.</summary>
    private (SongSample?, SongInstrument?, int) Resolve(Channel c, int note)
    {
        if (_song.UsesInstruments)
        {
            if (c.Instrument <= 0 || c.Instrument > _song.Instruments.Count)
                return (null, null, note);
            var instrument = _song.Instruments[c.Instrument - 1];
            int number = instrument.SampleOf[note - 1];
            int plays = _song.Type == SongFormat.It ? instrument.NoteOf[note - 1] + 1 : note;
            var sample = number > 0 && number <= _song.SampleList.Count ? _song.SampleList[number - 1] : null;
            return (sample is { Data.Length: > 0 } ? sample : null, instrument, plays);
        }
        var alone = c.Instrument > 0 && c.Instrument <= _song.SampleList.Count ? _song.SampleList[c.Instrument - 1] : null;
        return (alone is { Data.Length: > 0 } ? alone : null, null, note);
    }

    private void Trigger(Channel c, SongSample sample, SongInstrument? instrument, int note)
    {
        if (c.Voice is { } old && !old.Done)
        {
            int action = c.NewNoteAction ?? old.Instrument?.NewNoteAction ?? 0;
            if (old.Instrument is null || _song.Type != SongFormat.It)
                action = 0;
            if (action == 0)
                old.Stop();
            else
            {
                old.Background = true;
                old.Freeze(c);
                if (action == 2)
                    old.KeyOff();
                else if (action == 3)
                    old.StartFade();
            }
        }
        // Impulse Tracker's duplicate check: the voices this channel left behind that play the same.
        if (instrument is { DuplicateCheck: > 0 } && _song.Type == SongFormat.It)
        {
            foreach (var v in _voices)
            {
                if (!v.Background || v.Channel != c.Index || v.Done || v.Instrument != instrument)
                    continue;
                bool same = instrument.DuplicateCheck switch { 1 => v.Note == note, 2 => v.Sample == sample, _ => true };
                if (!same)
                    continue;
                switch (instrument.DuplicateAction)
                {
                    case 0: v.Stop(); break;
                    case 1: v.KeyOff(); break;
                    default: v.StartFade(); break;
                }
            }
        }
        // Not too many voices left behind: the oldest go.
        int background = 0;
        for (int i = _voices.Count - 1; i >= 0; i--)
            if (_voices[i].Background && !_voices[i].Done && ++background > 96)
                _voices[i].Stop();

        var voice = new Voice(sample, instrument, note, c.Index, _rate);
        _voices.Add(voice);
        c.Voice = voice;
        c.Note = note;
        c.Period = c.TargetPeriod = PeriodOf(note, sample);
        c.Struck = true;
        c.NewNoteAction = null;
        if ((c.VibratoWave & 4) == 0)
            c.VibratoPosition = 0;
        if ((c.TremoloWave & 4) == 0)
            c.TremoloPosition = 0;
        c.TremorCount = 0;
        c.RetrigCount = 0;
        if (instrument is { FilterCutoff: >= 0 })
            c.Cutoff = instrument.FilterCutoff;
        if (instrument is { FilterResonance: >= 0 })
            c.Resonance = instrument.FilterResonance;
    }

    private void KeyOff(Channel c)
    {
        if (c.Voice is not { } v)
            return;
        v.KeyOff();
        // FastTracker: with no volume envelope, a key off silences at once.
        if (_song.Type == SongFormat.Xm && v.Instrument is { VolumeEnvelope.Enabled: false })
            c.Volume = 0;
        // Impulse Tracker's samples alone: the note is cut.
        if (_song.Type != SongFormat.Xm && v.Instrument is null && v.Sample is { SustainMode: 0 })
            v.Stop();
    }

    // ------------------------------------------------------------------ pitch

    /// <summary>A note's period: in sixty-fourths of a semitone below the sample's base note with
    /// linear slides, else the Amiga's period, four to its unit, for the sample's rate.</summary>
    private double PeriodOf(int note, SongSample sample) => _song.LinearSlides
        ? (_song.MiddleNote - note) * 64.0
        : 1712.0 * 8363 / sample.BaseRate * Math.Pow(2, (_song.MiddleNote - note) / 12.0);

    private double HertzOf(double period, SongSample sample) => _song.LinearSlides
        ? sample.BaseRate * Math.Pow(2, -period / 768.0)
        : period > 0 ? 8363.0 * 1712 / period : 0;

    /// <summary>The channel's pitch for the tick - its period, with vibrato and arpeggio - given to
    /// its voice.</summary>
    private void Pitch(Channel c)
    {
        if (c.Voice is not { Sample: { } sample } v || v.Background)
            return;
        double period = c.Period + c.VibratoOffset;
        double hertz = HertzOf(_song.LinearSlides ? period : Math.Max(1, period), sample);
        if (c.Arpeggio != 0)
            hertz *= Math.Pow(2, c.Arpeggio / 12.0);
        v.Hertz = hertz;
        v.Volume = c.TremorMuted ? 0 : Math.Clamp(c.Volume + c.TremoloOffset, 0, 64);
        v.ChannelVolume = c.ChannelVolume;
        v.Pan = Math.Clamp(c.Pan + c.PanbrelloOffset, 0, 256);
        (v.Cutoff, v.Resonance) = (c.Cutoff, c.Resonance);
        c.VibratoOffset = c.TremoloOffset = c.PanbrelloOffset = 0;
        c.Arpeggio = 0;
    }

    private void Slide(Channel c, double amount)
    {
        // Up in pitch is down in period.
        c.Period -= amount;
        if (!_song.LinearSlides)
            c.Period = Math.Clamp(c.Period, 1, 1712 * 32);
    }

    private void TonePortamento(Channel c, int speed)
    {
        double step = speed * 4.0;
        if (c.Period < c.TargetPeriod)
            c.Period = Math.Min(c.TargetPeriod, c.Period + step);
        else if (c.Period > c.TargetPeriod)
            c.Period = Math.Max(c.TargetPeriod, c.Period - step);
    }

    private static int Wave(int wave, int position)
    {
        int at = position & 63;
        return (wave & 3) switch
        {
            1 => 255 - (at * 8),                                   // ramp down
            2 => at < 32 ? 255 : -255,                             // square
            3 => (int)((((uint)(position * 1103515245 + 12345) >> 8) % 511) - 255),  // random, the same each time
            _ => (int)Math.Round(255 * Math.Sin(at * Math.PI / 32)),
        };
    }

    private void Vibrato(Channel c, int fine)
    {
        int depth = c.VibratoDepth;
        c.VibratoOffset = Wave(c.VibratoWave, c.VibratoPosition) * depth / (fine == 1 ? 128.0 : 32.0);
        if (_song.OldEffects)
            c.VibratoOffset = -c.VibratoOffset * 2;
        c.VibratoPosition += c.VibratoSpeed;
    }

    // ------------------------------------------------------------------ effects

    private void FirstTick(Channel c, Fx fx, int p)
    {
        int x = p >> 4, y = p & 0x0F;
        bool xm = _song.Type == SongFormat.Xm;
        switch (fx)
        {
            case Fx.Arpeggio:
                c.ArpeggioParam = Recall(c, Slot.Arpeggio, p);
                break;
            case Fx.PortaUp:
            case Fx.PortaDown:
            {
                int value = Recall(c, fx == Fx.PortaUp ? Slot.PortaUp : Slot.PortaDown, p);
                c.SlideParam = value;
                if (!xm && value >= 0xE0)
                {
                    // Fine (Fx) and extra fine (Ex) slides happen once, here.
                    double amount = value >= 0xF0 ? (value & 0x0F) * 4 : value & 0x0F;
                    Slide(c, fx == Fx.PortaUp ? amount : -amount);
                }
                break;
            }
            case Fx.FinePortaUp:
                Slide(c, Recall(c, Slot.FinePortaUp, p) * 4);
                break;
            case Fx.FinePortaDown:
                Slide(c, -Recall(c, Slot.FinePortaDown, p) * 4);
                break;
            case Fx.ExtraFineUp:
                Slide(c, Recall(c, Slot.ExtraUp, p));
                break;
            case Fx.ExtraFineDown:
                Slide(c, -Recall(c, Slot.ExtraDown, p));
                break;
            case Fx.TonePorta:
                c.PortaSpeed = Recall(c, Slot.TonePorta, p);
                break;
            case Fx.Vibrato:
            case Fx.FineVibrato:
                if (x > 0) c.VibratoSpeed = x;
                if (y > 0) c.VibratoDepth = y;
                c.FineVibrato = fx == Fx.FineVibrato;
                if (_song.Type == SongFormat.It)
                    Vibrato(c, c.FineVibrato ? 1 : 0);
                break;
            case Fx.TonePortaVol:
            case Fx.VibratoVol:
            case Fx.VolSlide:
                c.VolSlideParam = Recall(c, Slot.VolSlide, p);
                if (!xm)
                    FineVolumeSlide(c, c.VolSlideParam, ref c.Volume, 64);
                break;
            case Fx.Tremolo:
                p = Recall(c, Slot.Tremolo, p);
                if ((p >> 4) > 0) c.TremoloSpeed = p >> 4;
                if ((p & 0x0F) > 0) c.TremoloDepth = p & 0x0F;
                break;
            case Fx.Tremor:
                c.TremorParam = Recall(c, Slot.Tremor, p);
                break;
            case Fx.SetPan:
                c.Pan = Math.Clamp(p + (p >> 7), 0, 256);
                break;
            case Fx.PanSlide:
                c.PanSlideParam = Recall(c, Slot.PanSlide, p);
                if (!xm)
                    FinePanSlide(c);
                break;
            case Fx.Offset:
            {
                int offset = (Recall(c, Slot.Offset, p) << 8) + (c.HighOffset << 16);
                if (c.Voice is { Sample: { } sample } v && c.Struck)
                {
                    if (offset < sample.Data.Length)
                        v.Position = offset;
                    else if (_song.Type == SongFormat.It && !_song.OldEffects)
                        v.Position = sample.Data.Length - 1;
                    else if (sample.LoopMode > 0)
                        v.Position = sample.LoopStart;
                    else
                        v.Stop();
                }
                break;
            }
            case Fx.HighOffset:
                c.HighOffset = p;
                break;
            case Fx.Jump:
                _jump = true;
                _jumpOrder = p;
                _jumpRow = 0;
                break;
            case Fx.Break:
                if (!_jump)
                    _jumpOrder = _order + 1;
                _jump = true;
                _jumpRow = p;
                break;
            case Fx.SetVolume:
                c.Volume = p;
                break;
            case Fx.Speed:
                if (p > 0)
                    _speed = p;
                break;
            case Fx.Tempo:
                if (p >= 0x20)
                    _tempo = p;
                else if (_song.Type == SongFormat.It)
                    c.TempoSlide = Recall(c, Slot.Tempo, p);
                break;
            case Fx.GlobalVolume:
                _globalVolume = p;
                break;
            case Fx.GlobalVolSlide:
                c.GlobalSlideParam = Recall(c, Slot.GlobalVolSlide, p);
                if (!xm)
                    FineVolumeSlide(c, c.GlobalSlideParam, ref _globalVolume, 128);
                break;
            case Fx.ChannelVolume:
                c.ChannelVolume = Math.Min(64, p);
                break;
            case Fx.ChannelVolSlide:
                c.ChannelSlideParam = Recall(c, Slot.ChannelVolSlide, p);
                FineVolumeSlide(c, c.ChannelSlideParam, ref c.ChannelVolume, 64);
                break;
            case Fx.FineVolUp:
                c.Volume = Math.Min(64, c.Volume + Recall(c, Slot.FineVolUp, p));
                break;
            case Fx.FineVolDown:
                c.Volume = Math.Max(0, c.Volume - Recall(c, Slot.FineVolDown, p));
                break;
            case Fx.Retrig:
                c.RetrigParam = xm && p < 0x100 ? p : Recall(c, Slot.Retrig, p & 0xFF);
                c.RetrigSimple = xm && p < 0x100;
                break;
            case Fx.Panbrello:
                p = Recall(c, Slot.Panbrello, p);
                if ((p >> 4) > 0) c.PanbrelloSpeed = p >> 4;
                if ((p & 0x0F) > 0) c.PanbrelloDepth = p & 0x0F;
                break;
            case Fx.NoteCut:
                if (p == 0 && _song.Type != SongFormat.S3m)
                    c.Volume = 0;
                break;
            case Fx.PatternDelay:
                if (_delayLeft == 0 && _patternDelay == 0)
                    _patternDelay = p;
                break;
            case Fx.FinePatternDelay:
                _extraTicks += p;
                break;
            case Fx.PatternLoop:
                if (p == 0)
                    c.LoopRow = _row;
                else
                {
                    c.LoopCount = c.LoopCount == 0 ? p : c.LoopCount - 1;
                    if (c.LoopCount > 0)
                    {
                        _jump = _loopJump = true;
                        (_jumpOrder, _jumpRow) = (_order, c.LoopRow);
                    }
                    else if (_song.Type == SongFormat.It)
                        c.LoopRow = _row + 1;
                }
                break;
            case Fx.VibratoWave:
                c.VibratoWave = p;
                break;
            case Fx.TremoloWave:
                c.TremoloWave = p;
                break;
            case Fx.PanbrelloWave:
                c.PanbrelloWave = p;
                break;
            case Fx.EnvelopePos:
                c.Voice?.SetEnvelopePosition(p);
                break;
            case Fx.KeyOff:
                if (p == 0)
                    KeyOff(c);
                break;
            case Fx.InstrumentControl:
                InstrumentControl(c, p);
                break;
            case Fx.Filter:
                // Impulse Tracker's default macros: Z00-Z7F the cutoff, Z80-Z8F the resonance.
                if (p < 0x80)
                    c.Cutoff = p;
                else if (p <= 0x8F)
                    c.Resonance = (p & 0x0F) * 8;
                break;
        }
    }

    private void InstrumentControl(Channel c, int p)
    {
        switch (p)
        {
            case <= 2:
                // The notes this channel left behind: cut, released or faded.
                foreach (var v in _voices)
                {
                    if (!v.Background || v.Channel != c.Index || v.Done)
                        continue;
                    if (p == 0) v.Stop();
                    else if (p == 1) v.KeyOff();
                    else v.StartFade();
                }
                break;
            case <= 6:
                c.NewNoteAction = p switch { 3 => 0, 4 => 1, 5 => 2, _ => 3 };
                break;
            case 7 or 8:
                if (c.Voice is { } volume) volume.VolumeEnvelopeOn = p == 8;
                break;
            case 9 or 10:
                if (c.Voice is { } pan) pan.PanEnvelopeOn = p == 10;
                break;
            case 11 or 12:
                if (c.Voice is { } pitch) pitch.PitchEnvelopeOn = p == 12;
                break;
        }
    }

    /// <summary>Scream and Impulse Tracker's fine slides, on the first tick: DxF up x, DFy down y.</summary>
    private static void FineVolumeSlide(Channel c, int p, ref int value, int top)
    {
        int x = p >> 4, y = p & 0x0F;
        if (y == 0xF && x > 0)
            value = Math.Min(top, value + x);
        else if (x == 0xF && y > 0)
            value = Math.Max(0, value - y);
    }

    private void FinePanSlide(Channel c)
    {
        int x = c.PanSlideParam >> 4, y = c.PanSlideParam & 0x0F;
        if (y == 0xF && x > 0)
            c.Pan = Math.Max(0, c.Pan - (x * 4));
        else if (x == 0xF && y > 0)
            c.Pan = Math.Min(256, c.Pan + (y * 4));
    }

    private void TickEffects(Channel c)
    {
        bool xm = _song.Type == SongFormat.Xm;
        int p = c.Param;
        VolumeColumn(c, c.VolumeFx, firstTick: false);
        switch (c.Fx)
        {
            case Fx.Arpeggio:
            {
                int step = _tick % 3;
                c.Arpeggio = step == 1 ? c.ArpeggioParam >> 4 : step == 2 ? c.ArpeggioParam & 0x0F : 0;
                break;
            }
            case Fx.PortaUp:
            case Fx.PortaDown:
                if (xm || c.SlideParam < 0xE0)
                    Slide(c, (c.Fx == Fx.PortaUp ? 1 : -1) * c.SlideParam * 4.0);
                break;
            case Fx.TonePorta:
                TonePortamento(c, c.PortaSpeed);
                break;
            case Fx.Vibrato:
            case Fx.FineVibrato:
                Vibrato(c, c.FineVibrato ? 1 : 0);
                break;
            case Fx.TonePortaVol:
                TonePortamento(c, c.PortaSpeed);
                VolumeSlide(c, c.VolSlideParam);
                break;
            case Fx.VibratoVol:
                Vibrato(c, c.FineVibrato ? 1 : 0);
                VolumeSlide(c, c.VolSlideParam);
                break;
            case Fx.VolSlide:
                VolumeSlide(c, c.VolSlideParam);
                break;
            case Fx.Tremolo:
                c.TremoloOffset = Wave(c.TremoloWave, c.TremoloPosition) * c.TremoloDepth / 64;
                c.TremoloPosition += c.TremoloSpeed;
                break;
            case Fx.Tremor:
            {
                int on = (c.TremorParam >> 4) + (xm || _song.OldEffects ? 1 : 0), off = (c.TremorParam & 0x0F) + (xm || _song.OldEffects ? 1 : 0);
                on = Math.Max(1, on);
                off = Math.Max(1, off);
                c.TremorMuted = c.TremorCount % (on + off) >= on;
                c.TremorCount++;
                break;
            }
            case Fx.PanSlide:
            {
                int x = c.PanSlideParam >> 4, y = c.PanSlideParam & 0x0F;
                if (xm)
                    c.Pan = Math.Clamp(c.Pan + (x > 0 ? x : -y), 0, 256);
                else if (y != 0xF && x != 0xF)
                    c.Pan = Math.Clamp(c.Pan + (x > 0 ? -x * 4 : y * 4), 0, 256);
                break;
            }
            case Fx.GlobalVolSlide:
                if (xm)
                {
                    int x = c.GlobalSlideParam >> 4, y = c.GlobalSlideParam & 0x0F;
                    _globalVolume = Math.Clamp(_globalVolume + (x > 0 ? x * 2 : -y * 2), 0, 128);
                }
                else
                    _globalVolume = SlideOnTick(c.GlobalSlideParam, _globalVolume, 128);
                break;
            case Fx.ChannelVolSlide:
                c.ChannelVolume = SlideOnTick(c.ChannelSlideParam, c.ChannelVolume, 64);
                break;
            case Fx.Retrig:
                Retrig(c);
                break;
            case Fx.Panbrello:
                c.PanbrelloOffset = Wave(c.PanbrelloWave, c.PanbrelloPosition) * c.PanbrelloDepth / 32;
                c.PanbrelloPosition += c.PanbrelloSpeed;
                break;
            case Fx.NoteCut:
                if (_tick == p)
                {
                    if (_song.Type == SongFormat.It)
                        c.Voice?.Stop();
                    else
                        c.Volume = 0;
                }
                break;
            case Fx.KeyOff:
                if (_tick == p)
                    KeyOff(c);
                break;
            case Fx.Tempo:
                if (_song.Type == SongFormat.It && p < 0x20)
                {
                    int slide = c.TempoSlide;
                    _tempo = Math.Clamp(_tempo + ((slide >> 4) == 1 ? slide & 0x0F : -(slide & 0x0F)), 32, 255);
                }
                break;
        }
    }

    private void VolumeSlide(Channel c, int p)
    {
        int x = p >> 4, y = p & 0x0F;
        if (_song.Type == SongFormat.Xm)
            c.Volume = x > 0 ? Math.Min(64, c.Volume + x) : Math.Max(0, c.Volume - y);
        else
            c.Volume = SlideOnTick(c.VolSlideParam, c.Volume, 64);
    }

    /// <summary>Scream and Impulse Tracker's slide on the ticks after the first: D0y down y - D0F too -
    /// and Dx0 up x; the fine ones, DxF and DFy, were on the first tick.</summary>
    private static int SlideOnTick(int p, int value, int top)
    {
        int x = p >> 4, y = p & 0x0F;
        if (x == 0)
            return Math.Max(0, value - y);
        if (y == 0)
            return Math.Min(top, value + x);
        return value;
    }

    private void Retrig(Channel c)
    {
        int p = c.RetrigParam, ticks = p & 0x0F;
        if (ticks == 0 || c.Voice is not { } v)
            return;
        if (++c.RetrigCount < ticks)
            return;
        c.RetrigCount = 0;
        if (!c.RetrigSimple)
        {
            int change = p >> 4;
            c.Volume = change switch
            {
                6 => c.Volume * 2 / 3,
                7 => c.Volume / 2,
                0xE => c.Volume * 3 / 2,
                0xF => c.Volume * 2,
                _ => c.Volume + RetrigAdd[change],
            };
            c.Volume = Math.Clamp(c.Volume, 0, 64);
        }
        v.Restart();
        c.Struck = true;
    }

    /// <summary>The volume column: FastTracker's commands in its high nibble, Impulse Tracker's ranges.</summary>
    private void VolumeColumn(Channel c, int volume, bool firstTick)
    {
        if (firstTick)
            c.VolumeFx = volume;
        if (_song.Type == SongFormat.Xm)
        {
            int x = volume & 0x0F;
            switch (volume >> 4)
            {
                case >= 1 and <= 4 when firstTick:
                    c.Volume = volume - 0x10;
                    break;
                case 5 when firstTick && volume == 0x50:
                    c.Volume = 64;
                    break;
                case 6 when !firstTick:
                    c.Volume = Math.Max(0, c.Volume - x);
                    break;
                case 7 when !firstTick:
                    c.Volume = Math.Min(64, c.Volume + x);
                    break;
                case 8 when firstTick:
                    c.Volume = Math.Max(0, c.Volume - x);
                    break;
                case 9 when firstTick:
                    c.Volume = Math.Min(64, c.Volume + x);
                    break;
                case 0xA when firstTick:
                    if (x > 0) c.VibratoSpeed = x;
                    break;
                case 0xB:
                    if (firstTick && x > 0) c.VibratoDepth = x;
                    else if (!firstTick) Vibrato(c, 0);
                    break;
                case 0xC when firstTick:
                    c.Pan = x * 17;
                    break;
                case 0xD when !firstTick:
                    c.Pan = Math.Max(0, c.Pan - x);
                    break;
                case 0xE when !firstTick:
                    c.Pan = Math.Min(256, c.Pan + x);
                    break;
                case 0xF:
                    if (firstTick && x > 0) c.PortaSpeed = x << 4;
                    else if (!firstTick) TonePortamento(c, c.PortaSpeed);
                    break;
            }
            return;
        }
        switch (volume)
        {
            case <= 64 when firstTick:
                c.Volume = volume;
                break;
            case >= 65 and <= 74 when firstTick:
                c.Volume = Math.Min(64, c.Volume + Recall(c, Slot.VolumeColumn, volume - 65));
                break;
            case >= 75 and <= 84 when firstTick:
                c.Volume = Math.Max(0, c.Volume - Recall(c, Slot.VolumeColumn, volume - 75));
                break;
            case >= 85 and <= 94:
                if (firstTick) Recall(c, Slot.VolumeColumn, volume - 85);
                else c.Volume = Math.Min(64, c.Volume + c.Memory[(int)Slot.VolumeColumn]);
                break;
            case >= 95 and <= 104:
                if (firstTick) Recall(c, Slot.VolumeColumn, volume - 95);
                else c.Volume = Math.Max(0, c.Volume - c.Memory[(int)Slot.VolumeColumn]);
                break;
            case >= 105 and <= 114 when !firstTick:
                Slide(c, -Recall(c, Slot.PortaUp, (volume - 105) * 4) * 4.0);
                break;
            case >= 115 and <= 124 when !firstTick:
                Slide(c, Recall(c, Slot.PortaUp, (volume - 115) * 4) * 4.0);
                break;
            case >= 128 and <= 192 when firstTick:
                c.Pan = (volume - 128) * 4;
                break;
            case >= 193 and <= 202:
                if (firstTick) c.PortaSpeed = Recall(c, Slot.TonePorta, PortaTable[volume - 193]);
                else if (c.Fx is not (Fx.TonePorta or Fx.TonePortaVol)) TonePortamento(c, c.PortaSpeed);
                break;
            case >= 203 and <= 212:
                if (firstTick && volume > 203) c.VibratoDepth = volume - 203;
                else if (!firstTick && c.Fx is not (Fx.Vibrato or Fx.VibratoVol)) Vibrato(c, 0);
                break;
        }
    }

    // ------------------------------------------------------------------ a channel

    private sealed class Channel
    {
        public int Index;
        public bool Muted;
        public Voice? Voice;
        public int Instrument, Note;
        public int Volume = 64, ChannelVolume = 64, Pan = 128;
        public double Period, TargetPeriod, VibratoOffset;
        public int Arpeggio, ArpeggioParam;
        public Fx Fx;
        public int Param, VolumeFx;
        public readonly int[] Memory = new int[(int)Slot.Count];
        public int SlideParam, PortaSpeed, VolSlideParam, PanSlideParam, GlobalSlideParam, ChannelSlideParam;
        public int VibratoSpeed, VibratoDepth, VibratoPosition, VibratoWave;
        public bool FineVibrato;
        public int TremoloSpeed, TremoloDepth, TremoloPosition, TremoloWave, TremoloOffset;
        public int PanbrelloSpeed, PanbrelloDepth, PanbrelloPosition, PanbrelloWave, PanbrelloOffset;
        public int TremorParam, TremorCount;
        public bool TremorMuted;
        public int RetrigParam, RetrigCount;
        public bool RetrigSimple;
        public int HighOffset, TempoSlide;
        public int Cutoff = 127, Resonance;
        public int LoopRow, LoopCount;
        public SongCell? Delayed;
        public int DelayTick = -1;
        public int? NewNoteAction;
        public bool Struck;
        public int State;
    }

    // ------------------------------------------------------------------ a voice

    /// <summary>A sample sounding: on its channel, or left behind in the background by a new note.</summary>
    private sealed class Voice(SongSample sample, SongInstrument? instrument, int note, int channel, int rate)
    {
        public readonly SongSample Sample = sample;
        public readonly SongInstrument? Instrument = instrument;
        public readonly int Note = note, Channel = channel;
        public bool Background, KeyOn = true, Fading, Done, Ended, Gone, Fresh = true;
        public double Position;
        private int _direction = 1;
        public double Hertz;
        public int Volume = 64, ChannelVolume = 64, Pan = 128;
        public int Cutoff = 127, Resonance;
        public double Gain, FinalPan = 128;

        // Impulse Tracker's resonant low-pass: on while the cutoff is below its top, there is
        // resonance, or a filter envelope moves it.
        private bool _filtered;
        private double _a0, _b0, _b1, _y1, _y2;
        private int _filterKey = -1;
        private int _filterTick;
        public double GainLeft, GainRight, StepLeft, StepRight;
        public int Ramp;
        public int Fade = 65536;
        public bool VolumeEnvelopeOn = instrument?.VolumeEnvelope.Enabled ?? false;
        public bool PanEnvelopeOn = instrument?.PanEnvelope.Enabled ?? false;
        public bool PitchEnvelopeOn = instrument is { PitchEnvelope: { Enabled: true, Filter: false } };
        private int _volumeTick, _panTick, _pitchTick;
        private int _vibratoPosition, _vibratoTicks;
        private double _step;

        /// <summary>Its volume and panning kept as they were when its channel moved on.</summary>
        public void Freeze(Channel c)
        {
            Volume = Math.Clamp(c.Volume, 0, 64);
            ChannelVolume = c.ChannelVolume;
            Pan = c.Pan;
        }

        public void Stop() => Done = true;

        public void KeyOff()
        {
            KeyOn = false;
            // Released with no volume envelope - or one that loops - an instrument fades out.
            if (Instrument is not null && (!VolumeEnvelopeOn || Instrument.VolumeEnvelope.Loop))
                Fading = true;
        }

        public void StartFade() => Fading = true;

        public void Restart()
        {
            Position = 0;
            _direction = 1;
            Ended = false;
            _volumeTick = _panTick = _pitchTick = 0;
            Fade = 65536;
            Fading = false;
            KeyOn = true;
        }

        /// <summary>FastTracker's instrument named with no note: its envelopes and fade start again,
        /// the sample plays on.</summary>
        public void ResetEnvelopes()
        {
            _volumeTick = _panTick = _pitchTick = _filterTick = 0;
            Fade = 65536;
            Fading = false;
            KeyOn = true;
        }

        public void SetEnvelopePosition(int tick)
        {
            _volumeTick = tick;
            _panTick = tick;
        }

        /// <summary>The tick: envelopes on, the fade, auto-vibrato; its gain and panning for the tick.</summary>
        public void Tick(SongPlayer player)
        {
            if (Done)
                return;
            double envelope = 1;
            if (Instrument is { } i)
            {
                if (VolumeEnvelopeOn)
                {
                    envelope = Envelope(i.VolumeEnvelope, ref _volumeTick) / 64.0;
                    // At the end of an envelope that ends silent, the note is over.
                    if (envelope <= 0 && Past(i.VolumeEnvelope, _volumeTick) && player._song.Type == SongFormat.It)
                        Done = true;
                }
                if (Fading || (!KeyOn && player._song.Type == SongFormat.Xm && VolumeEnvelopeOn))
                {
                    Fade = Math.Max(0, Fade - i.Fadeout);
                    if (Fade == 0)
                        Done = true;
                }
            }
            double pan = Pan;
            if (Instrument is { } withPan && PanEnvelopeOn)
            {
                double value = Envelope(withPan.PanEnvelope, ref _panTick);
                double offset = player._song.Type == SongFormat.Xm ? value - 32 : value;
                pan += offset * (128 - Math.Abs(pan - 128)) / 32;
            }
            FinalPan = Math.Clamp(pan, 0, 256);

            double hertz = Hertz;
            if (Instrument is { } withPitch && PitchEnvelopeOn)
                hertz *= Math.Pow(2, Envelope(withPitch.PitchEnvelope, ref _pitchTick) / 2.0 / 12.0);
            if (Sample.VibratoDepth > 0 && Sample.VibratoRate > 0)
            {
                _vibratoTicks++;
                double sweep = player._song.Type == SongFormat.Xm
                    ? (Sample.VibratoSweep == 0 ? 1 : Math.Min(1, (double)_vibratoTicks / Sample.VibratoSweep))
                    : Math.Min(1, _vibratoTicks * Math.Max(1, Sample.VibratoSweep) / 256.0);
                int wave = Sample.VibratoType switch { 1 => 2, 2 => 1, 3 => 1, _ => 0 };
                // At most the depth in FastTracker's pitch units - 64 a semitone - as in Impulse Tracker.
                double semitones = Wave(wave, _vibratoPosition >> 2) / 255.0 * Sample.VibratoDepth * sweep / 64.0;
                hertz *= Math.Pow(2, semitones / 12);
                _vibratoPosition += Sample.VibratoRate;
            }
            _step = hertz / rate;

            int modifier = 256;
            bool filterEnvelope = Instrument is { PitchEnvelope: { Enabled: true, Filter: true } };
            if (filterEnvelope)
                modifier = (int)Math.Round((Envelope(Instrument!.PitchEnvelope, ref _filterTick) + 32) * 8);
            int key = (Cutoff << 16) | (Resonance << 9) | modifier;
            if (key != _filterKey)
            {
                _filterKey = key;
                _filtered = Cutoff < 127 || Resonance > 0 || filterEnvelope;
                if (_filtered)
                {
                    double cutoffHz = 110.0 * Math.Pow(2, 0.25 + (Cutoff * (modifier + 256) / (24.0 * 512)));
                    cutoffHz = Math.Min(cutoffHz, rate * 0.45);
                    double damping = Math.Pow(10, -Resonance * (24.0 / 128.0) / 20.0);
                    double r = rate / (2 * Math.PI * cutoffHz);
                    double d = (damping * r) + damping - 1, e = r * r;
                    _a0 = 1 / (1 + d + e);
                    _b0 = (d + e + e) / (1 + d + e);
                    _b1 = -e / (1 + d + e);
                }
            }

            Gain = Volume / 64.0 * ChannelVolume / 64.0 * Sample.GlobalVolume / 64.0
                   * (Instrument?.GlobalVolume ?? 128) / 128.0 * envelope * Fade / 65536.0;
        }

        private static bool Past(SongEnvelope e, int tick) => e.Points.Length > 0 && tick > e.Points[^1].Tick && !e.Loop;

        /// <summary>An envelope's value at its tick, which moves on - round its sustain loop while the
        /// key is held, round its loop otherwise.</summary>
        private double Envelope(SongEnvelope e, ref int tick)
        {
            var points = e.Points;
            if (points.Length == 0)
                return 64;
            double value = points[^1].Value;
            for (int p = 0; p + 1 < points.Length; p++)
            {
                if (tick < points[p + 1].Tick)
                {
                    int span = Math.Max(1, points[p + 1].Tick - points[p].Tick);
                    value = points[p].Value + ((double)(points[p + 1].Value - points[p].Value) * Math.Max(0, tick - points[p].Tick) / span);
                    break;
                }
            }
            if (tick <= points[0].Tick)
                value = points[0].Value;
            tick++;
            if (e.Sustain && KeyOn && tick > points[e.SustainEnd].Tick)
                tick = points[e.SustainStart].Tick;
            else if (e.Loop && tick > points[e.LoopEnd].Tick)
                tick = points[e.LoopStart].Tick;
            return value;
        }

        /// <summary><paramref name="count"/> frames of the voice added into <paramref name="left"/>
        /// and <paramref name="right"/> from <paramref name="from"/> - its gains ramping as they go -
        /// and into its channel's own sound, <paramref name="scope"/>, from <paramref name="scopeAt"/>.
        /// Stopped and ramped down, or ended, it is gone.</summary>
        public void Mix(double[] left, double[] right, int from, int count, float[]? scope, int scopeAt, bool muted)
        {
            // The sample read as the frames go - at its position, the next a step on, linearly
            // between - through its sustain loop while the key is held, its loop otherwise; what
            // changes kept in locals for the run, written back at its end.
            var data = Sample.Data;
            int length = data.Length;
            int start, end, mode;
            if (KeyOn && Sample.SustainMode > 0)
                (start, end, mode) = (Sample.SustainStart, Sample.SustainEnd, Sample.SustainMode);
            else
                (start, end, mode) = (Sample.LoopStart, Sample.LoopEnd, Sample.LoopMode);
            if (mode == 0)
                end = length;
            double position = Position, step = _step;
            int direction = _direction;
            bool filtered = _filtered, ended = Ended;
            double a0 = _a0, b0 = _b0, b1 = _b1, y1 = _y1, y2 = _y2;
            double gainLeft = GainLeft, gainRight = GainRight;
            int ramp = Ramp;

            for (int j = 0; j < count; j++)
            {
                if (ramp > 0)
                {
                    gainLeft += StepLeft;
                    gainRight += StepRight;
                    ramp--;
                }
                else if (Done)
                {
                    // Stopped, and ramped down to nothing: let go.
                    Gone = true;
                    continue;
                }
                if (ended)
                {
                    Gone = true;
                    continue;
                }

                double s = 0;
                if (length == 0)
                    ended = true;
                else
                {
                    int at = (int)position;
                    if (at >= length || at < 0)
                    {
                        if (mode == 0)
                        {
                            ended = true;
                            goto Heard;
                        }
                        position = start;
                        at = start;
                    }
                    // Between the sample at and the one after it, whichever way a ping-pong loop
                    // goes: the one before, on the way back, ran each step's wave backwards.
                    double fraction = position - at;
                    int next = at + 1;
                    if (next >= end)
                        next = mode == 1 ? start : at;
                    next = Math.Clamp(next, 0, length - 1);
                    s = (data[at] + ((data[next] - data[at]) * fraction)) / 32768.0;
                    if (filtered)
                    {
                        double y = (a0 * s) + (b0 * y1) + (b1 * y2);
                        (y2, y1) = (y1, y);
                        s = y;
                    }

                    position += step * direction;
                    if (mode == 0)
                    {
                        if (position >= length)
                            ended = true;
                    }
                    else if (mode == 1)
                    {
                        if (position >= end)
                            position = start + ((position - end) % Math.Max(1, end - start));
                    }
                    else
                    {
                        if (direction > 0 && position >= end)
                        {
                            position = Math.Max(start, end - 1 - (position - end));
                            direction = -1;
                        }
                        else if (direction < 0 && position < start)
                        {
                            position = Math.Min(end - 1, start + (start - position));
                            direction = 1;
                        }
                    }
                }

            Heard:
                if (scope is not null)
                    scope[scopeAt + j] += (float)(s * (gainLeft + gainRight));
                if (muted)
                    continue;
                left[from + j] += s * gainLeft;
                right[from + j] += s * gainRight;
            }

            (Position, _direction, Ended, _y1, _y2) = (position, direction, ended, y1, y2);
            (GainLeft, GainRight, Ramp) = (gainLeft, gainRight, ramp);
        }
    }
}
