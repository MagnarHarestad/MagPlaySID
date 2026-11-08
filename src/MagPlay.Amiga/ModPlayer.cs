namespace MagPlay.Amiga;

/// <summary>What a channel is playing at a moment: the sample, its pitch as Paula's period and in
/// hertz, and its volume - for meters and scopes.</summary>
public readonly record struct ChannelState(int Sample, int Period, double Hertz, int Volume, bool Sounding);

/// <summary>
/// A module played as ProTracker 2 plays it on an Amiga: rows of a pattern every so many ticks -
/// the speed - and ticks at the tempo's rate, 125 beats a minute making 50 a second; the effects
/// on the row's first tick or on the ticks after it, as ProTracker has them; and each channel a
/// sample read at Paula's rate for its period - the whole sample once, then its loop - its
/// channels to the left and right as an Amiga's are, 1 and 4 left, 2 and 3 right, through the
/// A500's filter.
/// </summary>
public sealed class ModPlayer
{
    /// <summary>Paula's clock on a PAL Amiga: a period of 428 plays a sample at 8287 Hz, C-2.</summary>
    public const double PaulaClock = 3546895;

    private const int LowestPeriod = 113, HighestPeriod = 856;

    /// <summary>A tick's length in seconds at <paramref name="tempo"/>, as the Amiga times it: CIA B's
    /// timer, counting at 709,379 Hz, loaded with 1,773,447 / tempo - 125 giving 50 ticks a second.</summary>
    public static double TickSeconds(int tempo) => Math.Floor(1773447.0 / tempo) / 709379.0;

    /// <summary>ProTracker's periods for C-1 to B-3, finetune 0; the other finetunes from them.</summary>
    private static readonly int[] Periods =
    [
        856, 808, 762, 720, 678, 640, 604, 570, 538, 508, 480, 453,
        428, 404, 381, 360, 339, 320, 302, 285, 269, 254, 240, 226,
        214, 202, 190, 180, 170, 160, 151, 143, 135, 127, 120, 113,
    ];

    private static readonly int[,] Tuned = BuildTuned();

    private static readonly byte[] Sine =
    [
        0, 24, 49, 74, 97, 120, 141, 161, 180, 197, 212, 224, 235, 244, 250, 253,
        255, 253, 250, 244, 235, 224, 212, 197, 180, 161, 141, 120, 97, 74, 49, 24,
    ];

    private readonly ModFile _mod;
    private readonly int _rate;
    private readonly Channel[] _channels;

    private int _speed = 6, _tempo = 125, _tick;
    private int _order, _row;
    private int _patternDelay, _delayLeft;
    private bool _jump;
    private int _jumpOrder, _jumpRow;
    private bool _loopJump;
    private double _tickSamplesLeft;
    private readonly HashSet<int> _visited = [];

    // The A500's filter: one pole near 4.4 kHz, on each side.
    private readonly double _filterCoefficient;
    private double _filterLeft, _filterRight;

    public ModPlayer(ModFile mod, int sampleRate = 44100, double stereoSeparation = 0.6, bool a500Filter = true)
    {
        _mod = mod;
        _rate = sampleRate;
        StereoSeparation = stereoSeparation;
        _channels = new Channel[mod.Channels];
        for (int c = 0; c < _channels.Length; c++)
            _channels[c] = new Channel { Left = c % 4 is 0 or 3 };
        _filterCoefficient = a500Filter ? 1 - Math.Exp(-2 * Math.PI * 4421 / sampleRate) : 1;
        StartRow();
    }

    public ModFile Module => _mod;

    public double StereoSeparation { get; set; }

    /// <summary>The order position and row playing.</summary>
    public int Order => _order;

    public int Row => _row;

    public int Pattern => _mod.Order[_order];

    /// <summary>The song has come round to a row it played before - its end, though it goes on.</summary>
    public bool Looped { get; private set; }

    /// <summary>F00 stopped the song.</summary>
    public bool Stopped { get; private set; }

    /// <summary>Samples rendered so far.</summary>
    public long Position { get; private set; }

    public int Speed => _speed;

    public int Tempo => _tempo;

    public void SetMuted(int channel, bool muted)
    {
        if ((uint)channel < (uint)_channels.Length)
            _channels[channel].Muted = muted;
    }

    public ChannelState StateOf(int channel)
    {
        var c = _channels[channel];
        bool sounding = c.Active && c.OutVolume > 0 && c.OutPeriod > 0;
        return new ChannelState(c.SampleNumber, c.OutPeriod, c.OutPeriod > 0 ? PaulaClock / c.OutPeriod : 0, c.OutVolume, sounding);
    }

    /// <summary>How long the song plays before it comes round to a row played before - or stops -
    /// played through with no sound made; null when it does neither within an hour.</summary>
    public static TimeSpan? Measure(ModFile mod)
    {
        var player = new ModPlayer(mod, 1000, a500Filter: false);
        double seconds = 0;
        while (!player.Looped && !player.Stopped && seconds < 3600)
        {
            player.NextTick();
            seconds += TickSeconds(player._tempo);
        }
        return player.Looped || player.Stopped ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>Renders stereo frames into <paramref name="left"/> and <paramref name="right"/>, from
    /// -1 to 1 - after F00, silence. Given <paramref name="channels"/>, each channel's own sound goes
    /// to its array from <paramref name="at"/> on, as its scope shows it; given <paramref name="states"/>,
    /// what it plays - see <see cref="Pack"/>; given <paramref name="rows"/>, the order and row.</summary>
    public void Render(Span<float> left, Span<float> right, float[][]? channels = null, int[][]? states = null,
                       int[]? rows = null, int at = 0)
    {
        int frames = left.Length;
        double scale = 1.0 / Math.Max(2, _channels.Length / 2);
        double sep = Math.Clamp(StereoSeparation, 0, 1);
        double near = 0.5 + 0.5 * sep, far = 0.5 - 0.5 * sep;
        for (int f = 0; f < frames; f++)
        {
            if (_tickSamplesLeft <= 0)
            {
                if (!Stopped)
                    NextTick();
                _tickSamplesLeft += _rate * TickSeconds(_tempo);
            }
            _tickSamplesLeft--;

            double l = 0, r = 0;
            for (int ch = 0; ch < _channels.Length; ch++)
            {
                var c = _channels[ch];
                double value = 0;
                if (!Stopped && c.Active && c.OutPeriod > 0 && c.Sample is { } sample)
                    value = c.Read(sample, PaulaClock / c.OutPeriod / _rate) * c.OutVolume / 64.0;
                if (channels is not null)
                    channels[ch][at + f] = (float)value;
                if (states is not null)
                    states[ch][at + f] = Pack(c);
                if (c.Muted)
                    continue;
                if (c.Left)
                    (l, r) = (l + value * near, r + value * far);
                else
                    (l, r) = (l + value * far, r + value * near);
            }
            if (rows is not null)
                rows[at + f] = (_order << 8) | _row;
            _filterLeft += (l * scale - _filterLeft) * _filterCoefficient;
            _filterRight += (r * scale - _filterRight) * _filterCoefficient;
            left[f] = (float)_filterLeft;
            right[f] = (float)_filterRight;
        }
        Position += frames;
    }

    /// <summary>What one channel alone sounds like now, for its scope: its sample at its volume.</summary>
    public double ChannelOutput(int channel)
    {
        var c = _channels[channel];
        return c.Active && c.Sample is { } sample && c.Position < sample.Data.Length
            ? sample.Data[(int)c.Position] / 128.0 * c.OutVolume / 64.0 : 0;
    }

    /// <summary>A channel's state as one number: its period in bits 0-11, volume in 12-18, whether
    /// it sounds in 19, its sample in 20-27, and in 28 whether a note started on it this tick.</summary>
    private static int Pack(Channel c) =>
        Math.Min(c.OutPeriod, 0xFFF) | (c.OutVolume << 12) | ((c.Active && c.OutVolume > 0 && c.OutPeriod > 0 ? 1 : 0) << 19)
        | (c.SampleNumber << 20) | ((c.Struck ? 1 : 0) << 28);

    /// <summary>A state packed by <see cref="Render"/>, unpacked.</summary>
    public static ChannelState Unpack(int state)
    {
        int period = state & 0xFFF;
        return new ChannelState((state >> 20) & 0xFF, period, period > 0 ? PaulaClock / period : 0, (state >> 12) & 0x7F, (state >> 19 & 1) != 0);
    }

    /// <summary>Whether a packed state is a note's first tick.</summary>
    public static bool Struck(int state) => (state >> 28 & 1) != 0;

    // ------------------------------------------------------------------ ticks and rows

    private void NextTick()
    {
        foreach (var c in _channels)
            c.Struck = false;
        if (_tick == 0 && _delayLeft == 0)
            ReadRow();
        else
            foreach (var c in _channels)
                TickEffects(c);
        foreach (var c in _channels)
            c.Settle();

        if (++_tick < _speed)
            return;
        _tick = 0;
        if (_delayLeft > 0)
        {
            _delayLeft--;
            return;
        }
        if (_patternDelay > 0)
        {
            _delayLeft = _patternDelay;
            _patternDelay = 0;
            if (_delayLeft > 0)
                return;
        }
        NextRow();
    }

    private void NextRow()
    {
        int order = _order, row = _row + 1;
        if (_jump)
        {
            (order, row) = (_jumpOrder, _jumpRow);
            if (_loopJump)
            {
                // A pattern loop plays its rows again on purpose: they are not the song coming round.
                for (int r = row; r <= _row; r++)
                    _visited.Remove(Key(_order, r));
            }
        }
        if (row >= ModFile.Rows)
        {
            row = 0;
            order++;
        }
        if (order >= _mod.Order.Count)
        {
            order = _mod.Restart;
            Looped = true;
        }
        (_order, _row) = (order, row);
        _jump = _loopJump = false;
        StartRow();
    }

    private void StartRow()
    {
        if (!_visited.Add(Key(_order, _row)))
            Looped = true;
    }

    private static int Key(int order, int row) => (order << 6) | row;

    private void ReadRow()
    {
        int pattern = _mod.Order[_order];
        for (int ch = 0; ch < _channels.Length; ch++)
        {
            var c = _channels[ch];
            var cell = pattern < _mod.Patterns.Count ? _mod.Cell(pattern, _row, ch) : default;
            c.Effect = cell.Effect;
            c.Parameter = cell.Parameter;
            int sub = cell.Effect == 0xE ? cell.Parameter >> 4 : -1, x = cell.Parameter & 0x0F;

            if (cell.Sample > 0 && cell.Sample <= _mod.Samples.Count)
            {
                var sample = _mod.Samples[cell.Sample - 1];
                c.Pending = sample;
                c.PendingNumber = cell.Sample;
                c.Volume = sample.Volume;
                c.FineTune = sample.FineTune;
            }

            c.DelayedPeriod = 0;
            if (cell.Period > 0)
            {
                if (sub == 5)
                    c.FineTune = x > 7 ? x - 16 : x;
                c.Note = NoteOf(cell.Period);
                int period = c.Note >= 0 ? Tuned[c.FineTune + 8, c.Note] : cell.Period;
                if (cell.Effect is 3 or 5)
                {
                    c.ToneTarget = period;
                    if (c.Sample is null && c.Pending is not null)
                        c.Trigger(period);
                }
                else if (sub == 0xD && x > 0)
                    c.DelayedPeriod = period;
                else
                {
                    c.Trigger(period);
                    if (cell.Effect == 9)
                    {
                        if (cell.Parameter > 0)
                            c.OffsetMemory = cell.Parameter;
                        c.SetOffset(c.OffsetMemory * 256);
                    }
                }
            }

            switch (cell.Effect)
            {
                case 0:
                    c.OutPeriod = c.Period;
                    break;
                case 3:
                    if (cell.Parameter > 0)
                        c.ToneSpeed = cell.Parameter;
                    break;
                case 4:
                    if ((cell.Parameter >> 4) > 0)
                        c.VibratoSpeed = cell.Parameter >> 4;
                    if (x > 0)
                        c.VibratoDepth = x;
                    break;
                case 7:
                    if ((cell.Parameter >> 4) > 0)
                        c.TremoloSpeed = cell.Parameter >> 4;
                    if (x > 0)
                        c.TremoloDepth = x;
                    break;
                case 0xB:
                    _jump = true;
                    _jumpOrder = cell.Parameter < _mod.Order.Count ? cell.Parameter : 0;
                    _jumpRow = 0;
                    break;
                case 0xC:
                    c.Volume = Math.Min(64, cell.Parameter);
                    break;
                case 0xD:
                    int breakTo = (cell.Parameter >> 4) * 10 + x;
                    if (!_jump)
                        _jumpOrder = _order + 1;
                    _jump = true;
                    _jumpRow = breakTo < ModFile.Rows ? breakTo : 0;
                    break;
                case 0xE:
                    RowExtended(c, sub, x);
                    break;
                case 0xF:
                    if (cell.Parameter == 0)
                        Stopped = true;
                    else if (cell.Parameter < 32)
                        _speed = cell.Parameter;
                    else
                        _tempo = cell.Parameter;
                    break;
            }
        }
    }

    private void RowExtended(Channel c, int sub, int x)
    {
        switch (sub)
        {
            case 1:
                c.Period = Math.Max(LowestPeriod, c.Period - x);
                break;
            case 2:
                c.Period = Math.Min(HighestPeriod, c.Period + x);
                break;
            case 3:
                c.Glissando = x != 0;
                break;
            case 4:
                c.VibratoWave = x;
                break;
            case 6:
                if (x == 0)
                    c.LoopRow = _row;
                else
                {
                    if (c.LoopCount == 0)
                        c.LoopCount = x;
                    else
                        c.LoopCount--;
                    if (c.LoopCount > 0)
                    {
                        _jump = _loopJump = true;
                        (_jumpOrder, _jumpRow) = (_order, c.LoopRow);
                    }
                }
                break;
            case 7:
                c.TremoloWave = x;
                break;
            case 0xA:
                c.Volume = Math.Min(64, c.Volume + x);
                break;
            case 0xB:
                c.Volume = Math.Max(0, c.Volume - x);
                break;
            case 0xC:
                if (x == 0)
                    c.Volume = 0;
                break;
            case 0xE:
                if (_delayLeft == 0 && _patternDelay == 0)
                    _patternDelay = x;
                break;
        }
    }

    private void TickEffects(Channel c)
    {
        int p = c.Parameter, x = p >> 4, y = p & 0x0F;
        switch (c.Effect)
        {
            case 0:
                if (p != 0 && c.Note >= 0)
                {
                    int add = (_tick % 3) switch { 1 => x, 2 => y, _ => 0 };
                    c.OutPeriod = Tuned[c.FineTune + 8, Math.Min(35, c.Note + add)];
                }
                break;
            case 1:
                c.Period = Math.Max(LowestPeriod, c.Period - p);
                break;
            case 2:
                c.Period = Math.Min(HighestPeriod, c.Period + p);
                break;
            case 3:
                TonePortamento(c);
                break;
            case 4:
                Vibrato(c);
                break;
            case 5:
                TonePortamento(c);
                VolumeSlide(c, p);
                break;
            case 6:
                Vibrato(c);
                VolumeSlide(c, p);
                break;
            case 7:
                Tremolo(c);
                break;
            case 0xA:
                VolumeSlide(c, p);
                break;
            case 0xE:
                if (x == 9 && y > 0 && _tick % y == 0)
                    c.Retrigger();
                else if (x == 0xC && _tick == y)
                    c.Volume = 0;
                else if (x == 0xD && _tick == y && c.DelayedPeriod > 0)
                    c.Trigger(c.DelayedPeriod);
                break;
        }
    }

    private static void VolumeSlide(Channel c, int p)
    {
        int up = p >> 4, down = p & 0x0F;
        c.Volume = up > 0 ? Math.Min(64, c.Volume + up) : Math.Max(0, c.Volume - down);
    }

    private static void TonePortamento(Channel c)
    {
        if (c.ToneTarget <= 0 || c.Period == c.ToneTarget)
            return;
        c.Period = c.Period < c.ToneTarget
            ? Math.Min(c.ToneTarget, c.Period + c.ToneSpeed)
            : Math.Max(c.ToneTarget, c.Period - c.ToneSpeed);
        if (c.Glissando)
            c.OutPeriod = Tuned[c.FineTune + 8, NoteOf(c.Period, c.FineTune)];
    }

    private static int Wave(int wave, int position)
    {
        int at = position & 31;
        return (wave & 3) switch
        {
            1 => (position & 32) == 0 ? at * 8 : 255 - at * 8,
            2 => 255,
            _ => Sine[at],
        };
    }

    private static void Vibrato(Channel c)
    {
        int delta = Wave(c.VibratoWave, c.VibratoPosition) * c.VibratoDepth >> 7;
        c.OutPeriod = (c.VibratoPosition & 32) == 0 ? c.Period + delta : c.Period - delta;
        c.VibratoPosition = (c.VibratoPosition + c.VibratoSpeed) & 63;
        c.Vibrating = true;
    }

    private static void Tremolo(Channel c)
    {
        int delta = Wave(c.TremoloWave, c.TremoloPosition) * c.TremoloDepth >> 6;
        c.OutVolume = Math.Clamp((c.TremoloPosition & 32) == 0 ? c.Volume + delta : c.Volume - delta, 0, 64);
        c.TremoloPosition = (c.TremoloPosition + c.TremoloSpeed) & 63;
        c.Trembling = true;
    }

    /// <summary>The note of a period, 0 for C-1 to 35 for B-3, at finetune 0; -1 outside them.</summary>
    private static int NoteOf(int period)
    {
        if (period > Periods[0] + 30 || period < Periods[^1] - 6)
            return -1;
        int best = 0;
        for (int n = 1; n < Periods.Length; n++)
            if (Math.Abs(Periods[n] - period) < Math.Abs(Periods[best] - period))
                best = n;
        return best;
    }

    private static int NoteOf(int period, int fineTune)
    {
        int best = 0;
        for (int n = 1; n < 36; n++)
            if (Math.Abs(Tuned[fineTune + 8, n] - period) < Math.Abs(Tuned[fineTune + 8, best] - period))
                best = n;
        return best;
    }

    private static int[,] BuildTuned()
    {
        var table = new int[16, 36];
        for (int fine = -8; fine < 8; fine++)
            for (int n = 0; n < 36; n++)
                table[fine + 8, n] = fine == 0 ? Periods[n] : (int)Math.Round(Periods[n] * Math.Pow(2, -fine / 96.0));
        return table;
    }

    // ------------------------------------------------------------------ a channel

    private sealed class Channel
    {
        public bool Left;
        public bool Muted;
        public ModSample? Sample, Pending;
        public int SampleNumber, PendingNumber;
        public double Position;
        public int End;
        public bool Active;
        public int Period, OutPeriod, Volume, OutVolume, FineTune, Note = -1;
        public int Effect, Parameter;
        public int ToneTarget, ToneSpeed;
        public bool Glissando;
        public int VibratoSpeed, VibratoDepth, VibratoPosition, VibratoWave;
        public int TremoloSpeed, TremoloDepth, TremoloPosition, TremoloWave;
        public bool Vibrating, Trembling;
        public bool Struck;
        public int OffsetMemory;
        public int LoopRow, LoopCount;
        public int DelayedPeriod;

        public void Trigger(int period)
        {
            if (Pending is not null)
                (Sample, SampleNumber) = (Pending, PendingNumber);
            Period = OutPeriod = period;
            Retrigger();
            if ((VibratoWave & 4) == 0)
                VibratoPosition = 0;
            if ((TremoloWave & 4) == 0)
                TremoloPosition = 0;
        }

        public void Retrigger()
        {
            Struck = true;
            Position = 0;
            End = Sample?.Data.Length ?? 0;
            Active = End > 0;
        }

        public void SetOffset(int offset)
        {
            if (Sample is null)
                return;
            if (offset < Sample.Data.Length)
                Position = offset;
            else if (Sample.Loops)
                (Position, End) = (Sample.LoopStart, Sample.LoopStart + Sample.LoopLength);
            else
                Active = false;
        }

        /// <summary>The tick's pitch and volume as heard: vibrato and tremolo only on the ticks they run.</summary>
        public void Settle()
        {
            if (!Vibrating && Effect != 0 && !(Effect == 3 && Glissando))
                OutPeriod = Period;
            else if (Effect == 0 && Parameter == 0)
                OutPeriod = Period;
            if (!Trembling)
                OutVolume = Volume;
            Vibrating = Trembling = false;
        }

        /// <summary>The sample at the position, the next a fraction on - the whole once, then its loop.</summary>
        public double Read(ModSample sample, double step)
        {
            var data = sample.Data;
            int at = (int)Position;
            if (at >= End)
            {
                Active = false;
                return 0;
            }
            double fraction = Position - at;
            int next = at + 1 < End ? at + 1 : sample.Loops ? sample.LoopStart : at;
            double value = (data[at] + (data[next] - data[at]) * fraction) / 128.0;
            Position += step;
            if (Position >= End)
            {
                if (sample.Loops)
                {
                    int loopEnd = sample.LoopStart + sample.LoopLength;
                    Position = sample.LoopStart + (Position - End) % sample.LoopLength;
                    End = loopEnd;
                }
                else
                {
                    Active = false;
                }
            }
            return value;
        }
    }
}
