namespace MagPlay.Score;

/// <summary>A register write and the CPU cycle it happens on, counted from the tune's start.</summary>
public readonly record struct TimedWrite(long Cycle, int Register, int Value);

/// <summary>
/// Plays a <see cref="Score"/> as a C64 player would: call by call, each voice reading its rows,
/// running its instrument's programs and its effects, and writing the SID's registers. What
/// comes out is the timeline of writes - the call's own at the call's cycle, in the order made,
/// and any digi's spread over the cycles until the next call - which is what a score is checked
/// against: the original tune's own writes.
///
/// Modelled on GoatTracker's player, the common ground of C64 trackers: a row starts a note on
/// its first call; the hard restart quietens the voice a few calls before the next row's note;
/// a note's first call can write a control byte of its own before the wave program runs.
/// Registers are written when their value changes, except what a raw step writes, which is
/// written as it says.
/// </summary>
public sealed class ScorePlayer
{
    /// <summary>Jumps followed in one call before a program is taken to be stuck.</summary>
    private const int MaxJumps = 64;

    private readonly Score _score;
    private readonly VoiceState[] _voices;
    private readonly List<TimedWrite> _writes = [];
    private readonly List<TimedWrite> _streamWrites = [];

    /// <summary>What the player last wrote to each register; -1 for not yet.</summary>
    private readonly int[] _written = new int[0x19];
    private readonly StreamState?[] _streams = new StreamState?[4];

    private long _cycle;
    private int _cyclesPerCall;
    private int _tempo;
    private (int First, int Second)? _funk;
    private int _modeVolume;
    private int _cutoff;
    private int _filterControl;
    private Program _filter = Program.Stopped;

    public ScorePlayer(Score score)
    {
        _score = score;
        _voices = [new VoiceState(0), new VoiceState(1), new VoiceState(2)];
        _cyclesPerCall = Math.Max(1, score.CyclesPerCall);
        _tempo = Math.Max(1, score.Tempo);
        _funk = score.FunkTempo is { } funk ? (_tempo, funk) : null;
        _modeVolume = score.ModeVolume & 0xFF;
        Array.Fill(_written, -1);

        foreach (var voice in _voices)
            voice.Ended = score.Voices[voice.Index].Order.Count == 0;

        Write(0x18, _modeVolume);
        Init = [.. _writes];
        _writes.Clear();
    }

    /// <summary>What is written before the first call.</summary>
    public IReadOnlyList<TimedWrite> Init { get; }

    /// <summary>The cycle the next call happens on.</summary>
    public long Cycle => _cycle;

    public int CyclesPerCall => _cyclesPerCall;

    /// <summary>Plays one call: the writes it makes, and the digi writes up to the next call.</summary>
    public IReadOnlyList<TimedWrite> Call()
    {
        _writes.Clear();
        foreach (var voice in _voices)
            PlayVoice(voice);

        StepFilter();
        Write(0x15, _cutoff & 0x07);
        Write(0x16, (_cutoff >> 3) & 0xFF);
        Write(0x17, _filterControl);
        Write(0x18, _modeVolume);

        int cycles = _cyclesPerCall;
        PlayStreams(_cycle, _cycle + cycles);
        _cycle += cycles;

        var all = new List<TimedWrite>(_writes.Count + _streamWrites.Count);
        all.AddRange(_writes);
        all.AddRange(_streamWrites.OrderBy(w => w.Cycle));
        return all;
    }

    // ------------------------------------------------------------------ voices

    private sealed class VoiceState(int index)
    {
        public int Index { get; } = index;

        public bool Ended;
        public int Entry = -1;
        public int RepeatLeft;
        public Pattern? Pattern;
        public int NextRow;
        public int Transpose;

        public Row Row = Row.Hold;
        public int RowCall;
        public int RowCalls;
        public int? Tempo;
        public bool FunkSecond;

        public Instrument? Instrument;
        public int Note;
        public int Frequency;
        public int Pulse;
        public int Control;
        public bool Gate;
        public int AttackDecay;
        public int SustainRelease;
        public int Detune;

        public Program Wave = Program.Stopped;
        public Program Pulses = Program.Stopped;

        /// <summary>The note's first call writes the instrument's first control byte.</summary>
        public bool FirstCall;

        public bool OnFirstCall => FirstCall && CallsSinceNote == 0;

        /// <summary>A note started with the gate already on: off and on again in the same call.</summary>
        public bool Retrigger;

        public int CallsSinceNote;
        public int? GlideTarget;
        public int VibratoCall;

        public int NoteAtCall = -1;
        public int CutAtCall = -1;
    }

    /// <summary>A program's place in its table: the step, the calls it has left, and what it adds a call.</summary>
    private sealed record Program(int Step, int CallsLeft, int Add)
    {
        public static Program Stopped { get; } = new(-1, 0, 0);

        public bool Running => Step >= 0;
    }

    private void PlayVoice(VoiceState voice)
    {
        if (voice.Ended)
            return;

        if (voice.RowCall >= voice.RowCalls && !StartRow(voice))
            return;

        int call = voice.RowCall;
        if (call == voice.NoteAtCall)
            StartNote(voice);

        if (call > 0 || voice.NoteAtCall > 0)
            PitchEffects(voice, call);

        if (!OnFirstCall(voice))
            StepWave(voice);
        StepPulse(voice);

        if (call == voice.CutAtCall)
            voice.Gate = false;

        HardRestartBeforeNextNote(voice, call);
        WriteVoice(voice);

        voice.CallsSinceNote++;
        voice.RowCall++;
    }

    /// <summary>The voice's next row: its length, and its note and effects - now, or later in the
    /// row with a note delay. False when the voice has nothing more to play.</summary>
    private bool StartRow(VoiceState voice)
    {
        if (NextRow(voice, advance: true) is not { } row)
        {
            voice.Ended = true;
            return false;
        }

        voice.Row = row;
        voice.RowCall = 0;
        voice.RowCalls = RowLength(voice, row);
        voice.CutAtCall = row.Effects.OfType<NoteCut>().Select(c => c.Calls).DefaultIfEmpty(-1).First();
        int delay = row.Effects.OfType<NoteDelay>().Select(d => d.Calls).DefaultIfEmpty(0).First();
        voice.NoteAtCall = Math.Clamp(delay, 0, Math.Max(0, voice.RowCalls - 1));
        voice.GlideTarget = null;
        voice.VibratoCall = 0;
        return true;
    }

    /// <summary>Calls the row lasts: its own, or the voice's tempo, or funk tempo's in turn.</summary>
    private int RowLength(VoiceState voice, Row row)
    {
        if (row.Calls is { } calls)
            return Math.Max(1, calls);

        if (voice.Tempo is { } own)
            return own;

        if (_funk is { } funk)
        {
            voice.FunkSecond = !voice.FunkSecond;
            return Math.Max(1, voice.FunkSecond ? funk.First : funk.Second);
        }

        return _tempo;
    }

    /// <summary>The row after the voice's current one, through the order list - taking it when
    /// <paramref name="advance"/>, only looking otherwise.</summary>
    private Row? NextRow(VoiceState voice, bool advance)
    {
        var order = _score.Voices[voice.Index].Order;
        int entry = voice.Entry, repeatLeft = voice.RepeatLeft, next = voice.NextRow, transpose = voice.Transpose;
        var pattern = voice.Pattern;

        for (int guard = 0; guard < order.Count * 2 + 4; guard++)
        {
            if (pattern is not null && next < pattern.Rows.Count)
            {
                if (advance)
                {
                    voice.Entry = entry;
                    voice.RepeatLeft = repeatLeft;
                    voice.Pattern = pattern;
                    voice.NextRow = next + 1;
                    voice.Transpose = transpose;
                }
                return pattern.Rows[next];
            }

            if (pattern is not null && repeatLeft > 1)
            {
                repeatLeft--;
                next = 0;
                continue;
            }

            entry++;
            if (entry >= order.Count)
                entry = _score.Voices[voice.Index].LoopTo;
            if (entry < 0 || entry >= order.Count)
                return null;

            var orderEntry = order[entry];
            pattern = _score.PatternNumber(orderEntry.Pattern);
            repeatLeft = Math.Max(1, orderEntry.Repeat);
            transpose = orderEntry.Transpose;
            next = 0;
        }

        return null;
    }

    /// <summary>The row's note, instrument and effects take hold.</summary>
    private void StartNote(VoiceState voice)
    {
        var row = voice.Row;
        if (row.Instrument > 0 && _score.InstrumentNumber(row.Instrument) is { } instrument)
            voice.Instrument = instrument;

        var glide = row.Effects.OfType<TonePortamento>().FirstOrDefault();
        switch (row.Note.Kind)
        {
            case RowNoteKind.Note when glide is not null:
                voice.Note = Math.Clamp(row.Note.Note + voice.Transpose, 0, _score.NoteTable.Length - 1);
                voice.GlideTarget = NoteFrequency(voice, voice.Note);
                if (glide.Speed == 0)
                    voice.Frequency = voice.GlideTarget.Value;
                break;

            case RowNoteKind.Note:
                Trigger(voice, Math.Clamp(row.Note.Note + voice.Transpose, 0, _score.NoteTable.Length - 1));
                break;

            case RowNoteKind.GateOff:
                voice.Gate = false;
                break;

            case RowNoteKind.GateOn:
                voice.Gate = true;
                break;
        }

        foreach (var effect in row.Effects)
            Apply(voice, effect);
    }

    /// <summary>A note from its beginning: the gate on, the instrument's envelope and programs.</summary>
    private void Trigger(VoiceState voice, int note)
    {
        voice.Retrigger = voice.Gate;
        voice.FirstCall = false;
        voice.Note = note;
        voice.Frequency = NoteFrequency(voice, note);
        voice.Gate = true;
        voice.CallsSinceNote = 0;
        voice.VibratoCall = 0;

        if (voice.Instrument is not { } instrument)
            return;

        voice.AttackDecay = instrument.AttackDecay;
        voice.SustainRelease = instrument.SustainRelease;
        voice.Wave = instrument.WaveProgram >= 0 ? new Program(instrument.WaveProgram, 0, 0) : Program.Stopped;
        voice.Pulses = instrument.PulseProgram >= 0 ? new Program(instrument.PulseProgram, 0, 0) : Program.Stopped;
        if (instrument.FilterProgram >= 0)
            _filter = new Program(instrument.FilterProgram, 0, 0);
        voice.FirstCall = instrument.FirstControl is not null;
    }

    private static bool OnFirstCall(VoiceState voice) => voice.OnFirstCall;

    private int NoteFrequency(VoiceState voice, int note) =>
        (_score.NoteTable[Math.Clamp(note, 0, _score.NoteTable.Length - 1)] + voice.Detune) & 0xFFFF;

    /// <summary>A one-time effect; the pitch effects are run call by call in <see cref="PitchEffects"/>.</summary>
    private void Apply(VoiceState voice, Effect effect)
    {
        switch (effect)
        {
            case SetAttackDecay e: voice.AttackDecay = e.Value & 0xFF; break;
            case SetSustainRelease e: voice.SustainRelease = e.Value & 0xFF; break;
            case SetControl e: voice.Control = e.Value & 0xFF; break;
            case SetWaveProgram e: voice.Wave = new Program(e.Step, 0, 0); break;
            case SetPulseProgram e: voice.Pulses = new Program(e.Step, 0, 0); break;
            case SetFilterProgram e: _filter = new Program(e.Step, 0, 0); break;
            case FilterControl e: _filterControl = e.Value & 0xFF; break;
            case FilterCutoff e: _cutoff = e.Value & 0x7FF; break;
            case MasterVolume e: _modeVolume = (_modeVolume & 0xF0) | (e.Value & 0x0F); break;
            case FilterMode e: _modeVolume = (e.Value & 0xF0) | (_modeVolume & 0x0F); break;
            case FunkTempo e: _funk = (Math.Max(1, e.First), Math.Max(1, e.Second)); break;
            case Tempo { ThisVoiceOnly: true } e: voice.Tempo = Math.Max(1, e.Calls); break;
            case Tempo e:
                _tempo = Math.Max(1, e.Calls);
                _funk = null;
                foreach (var each in _voices)
                    each.Tempo = null;
                break;
            case CallRate e: _cyclesPerCall = Math.Max(1, e.Cycles); break;
            case Detune e: voice.Detune = e.Amount; break;
            case SetFrequency e: voice.Frequency = e.Value & 0xFFFF; break;
            case SetPulse e: voice.Pulse = e.Value & 0xFFF; break;
            case PlaySample e: StartStream(e); break;
        }
    }

    /// <summary>Portamento, tone portamento, vibrato and arpeggio, a call at a time; without any,
    /// the instrument's own vibrato once its delay is past.</summary>
    private void PitchEffects(VoiceState voice, int call)
    {
        bool pitched = false;
        foreach (var effect in voice.Row.Effects)
        {
            switch (effect)
            {
                case Portamento e:
                    voice.Frequency = (voice.Frequency + e.Speed) & 0xFFFF;
                    pitched = true;
                    break;

                case TonePortamento e when voice.GlideTarget is { } target:
                    voice.Frequency = voice.Frequency < target
                        ? Math.Min(target, voice.Frequency + Math.Max(1, e.Speed))
                        : Math.Max(target, voice.Frequency - Math.Max(1, e.Speed));
                    pitched = true;
                    break;

                case VibratoEffect e:
                    Wobble(voice, e.Speed, e.Depth);
                    pitched = true;
                    break;

                case Arpeggio e:
                    int step = (call % 3) switch { 0 => 0, 1 => e.First, _ => e.Second };
                    voice.Frequency = NoteFrequency(voice, voice.Note + step);
                    pitched = true;
                    break;
            }
        }

        if (!pitched && voice.Instrument?.Vibrato is { } vibrato && voice.CallsSinceNote >= vibrato.Delay)
            Wobble(voice, vibrato.Speed, vibrato.Depth);
    }

    /// <summary>One call of vibrato: up a quarter period, down a half, up a quarter - round the
    /// note's pitch and back.</summary>
    private static void Wobble(VoiceState voice, int speed, int depth)
    {
        speed = Math.Max(1, speed);
        int quarter = voice.VibratoCall / speed % 4;
        voice.Frequency = (voice.Frequency + (quarter is 0 or 3 ? depth : -depth)) & 0xFFFF;
        voice.VibratoCall++;
    }

    /// <summary>When the row's remaining calls reach the instrument's gate timer and the next row
    /// starts a note, the gate goes off - and the envelope falls, for an envelope hard restart -
    /// so the next note's attack starts clean.</summary>
    private void HardRestartBeforeNextNote(VoiceState voice, int call)
    {
        if (voice.Instrument is not { GateTimer: > 0 } instrument || instrument.HardRestart == HardRestartKind.None)
            return;

        int at = voice.RowCalls - instrument.GateTimer;
        if (at < 1 || call != at)
            return;

        if (NextRow(voice, advance: false) is not { Note.Kind: RowNoteKind.Note } next
            || next.Effects.Any(e => e is TonePortamento or NoteDelay))
            return;

        voice.Gate = false;
        if (instrument.HardRestart == HardRestartKind.Envelope)
        {
            voice.AttackDecay = (instrument.HardRestartEnvelope >> 8) & 0xFF;
            voice.SustainRelease = instrument.HardRestartEnvelope & 0xFF;
        }
    }

    // ------------------------------------------------------------------ programs

    private void StepWave(VoiceState voice)
    {
        voice.Wave = Step(voice.Wave, _score.WaveTable, step =>
        {
            switch (step)
            {
                case WaveStep wave:
                    if (wave.Control is { } control)
                        voice.Control = control & 0xFF;
                    if (wave.Pitch is { } pitch)
                    {
                        voice.Frequency = pitch.Kind switch
                        {
                            PitchKind.Relative => NoteFrequency(voice, voice.Note + pitch.Value),
                            PitchKind.Absolute => NoteFrequency(voice, pitch.Value),
                            _ => pitch.Value & 0xFFFF,
                        };
                    }
                    return (Math.Max(1, wave.Calls), 0);

                case EffectStep effect:
                    Apply(voice, effect.Effect);
                    return (0, 0);

                case RawStep raw:
                    WriteRaw(voice, raw.Writes);
                    return (Math.Max(1, raw.Calls), 0);

                default:
                    return (1, 0);
            }
        }, _ => { });
    }

    private void StepPulse(VoiceState voice)
    {
        voice.Pulses = Step(voice.Pulses, _score.PulseTable, step =>
        {
            switch (step)
            {
                case PulseStep pulse:
                    if (pulse.Width is { } width)
                        voice.Pulse = width & 0xFFF;
                    else
                        voice.Pulse = (voice.Pulse + pulse.Add) & 0xFFF;
                    return (Math.Max(1, pulse.Calls), pulse.Add);

                case EffectStep effect:
                    Apply(voice, effect.Effect);
                    return (0, 0);

                default:
                    return (1, 0);
            }
        }, add => voice.Pulse = (voice.Pulse + add) & 0xFFF);
    }

    private void StepFilter()
    {
        _filter = Step(_filter, _score.FilterTable, step =>
        {
            switch (step)
            {
                case FilterStep filter:
                    if (filter.Control is { } control)
                        _filterControl = control & 0xFF;
                    if (filter.Mode is { } mode)
                        _modeVolume = (mode & 0xF0) | (_modeVolume & 0x0F);
                    if (filter.Cutoff is { } cutoff)
                        _cutoff = cutoff & 0x7FF;
                    else
                        _cutoff = (_cutoff + filter.Add) & 0x7FF;
                    return (Math.Max(1, filter.Calls), filter.Add);

                case EffectStep effect:
                    Apply(_voices[0], effect.Effect);
                    return (0, 0);

                case RawStep raw:
                    WriteRaw(null, raw.Writes);
                    return (Math.Max(1, raw.Calls), 0);

                default:
                    return (1, 0);
            }
        }, add => _cutoff = (_cutoff + add) & 0x7FF);
    }

    /// <summary>
    /// One call of a program. A step still holding takes its add again; otherwise the next step
    /// runs - following jumps, and running zero-call steps (effects) along the way - and says how
    /// many calls it holds and what it adds on each of the later ones.
    /// </summary>
    private static Program Step(Program program, List<TableStep> table,
                                Func<TableStep, (int Calls, int Add)> run, Action<int> hold)
    {
        if (!program.Running)
            return program;

        if (program.CallsLeft > 0)
        {
            hold(program.Add);
            return program with { CallsLeft = program.CallsLeft - 1 };
        }

        int at = program.Step;
        for (int jumps = 0; jumps < MaxJumps; jumps++)
        {
            if (at < 0 || at >= table.Count)
                return Program.Stopped;

            if (table[at] is JumpStep jump)
            {
                if (jump.To < 0)
                    return Program.Stopped;
                at = jump.To;
                continue;
            }

            var (calls, add) = run(table[at]);
            if (calls == 0)
            {
                at++;
                continue;
            }

            return new Program(at + 1, calls - 1, add);
        }

        return Program.Stopped;
    }

    // ------------------------------------------------------------------ writing

    private void WriteVoice(VoiceState voice)
    {
        int b = voice.Index * 7;
        Write(b + 0, voice.Frequency & 0xFF);
        Write(b + 1, (voice.Frequency >> 8) & 0xFF);
        Write(b + 2, voice.Pulse & 0xFF);
        Write(b + 3, (voice.Pulse >> 8) & 0x0F);
        Write(b + 5, voice.AttackDecay);
        Write(b + 6, voice.SustainRelease);

        int control = OnFirstCall(voice) && voice.Instrument?.FirstControl is { } first
            ? first & 0xFF
            : voice.Control & (voice.Gate ? 0xFF : 0xFE);

        // A note started with the gate still on: off and on again in this call, so the envelope
        // starts its attack - the way a player restarts a note without leaving a frame of silence.
        if (voice.Retrigger && (control & 1) != 0 && _written[b + 4] is var last and >= 0 && (last & 1) != 0)
            Write(b + 4, last & 0xFE, force: true);
        voice.Retrigger = false;
        Write(b + 4, control);
    }

    /// <summary>A raw step's writes, as written, and the voice and chip state carried on from them.</summary>
    private void WriteRaw(VoiceState? voice, IReadOnlyList<(int Register, int Value)> writes)
    {
        foreach (var (register, value) in writes)
        {
            int target = register is >= 0 and <= 6 && voice is not null ? voice.Index * 7 + register : register;
            if (target is < 0 or > 0x18)
                continue;

            Write(target, value & 0xFF, force: true);
            int offset = target % 7;
            var owner = target < 0x15 ? _voices[target / 7] : null;
            switch (target)
            {
                case < 0x15 when offset == 0: owner!.Frequency = (owner.Frequency & 0xFF00) | (value & 0xFF); break;
                case < 0x15 when offset == 1: owner!.Frequency = (owner.Frequency & 0x00FF) | ((value & 0xFF) << 8); break;
                case < 0x15 when offset == 2: owner!.Pulse = (owner.Pulse & 0xF00) | (value & 0xFF); break;
                case < 0x15 when offset == 3: owner!.Pulse = (owner.Pulse & 0x0FF) | ((value & 0x0F) << 8); break;
                case < 0x15 when offset == 4:
                    owner!.Control = value & 0xFF;
                    owner.Gate = (value & 1) != 0;
                    break;
                case < 0x15 when offset == 5: owner!.AttackDecay = value & 0xFF; break;
                case < 0x15: owner!.SustainRelease = value & 0xFF; break;
                case 0x15: _cutoff = (_cutoff & 0x7F8) | (value & 0x07); break;
                case 0x16: _cutoff = (_cutoff & 0x007) | ((value & 0xFF) << 3); break;
                case 0x17: _filterControl = value & 0xFF; break;
                default: _modeVolume = value & 0xFF; break;
            }
        }
    }

    /// <summary>The bits of each register the SID keeps: the pulse width's high register has four,
    /// the cutoff's low three. A value differing only in the others changes nothing.</summary>
    private static readonly int[] Used = [.. Enumerable.Range(0, 0x19).Select(r => r switch
    {
        3 or 10 or 17 => 0x0F,
        0x15 => 0x07,
        _ => 0xFF,
    })];

    private void Write(int register, int value, bool force = false)
    {
        int used = Used[register];
        if (!force && _written[register] >= 0 && (_written[register] & used) == (value & used))
            return;

        _written[register] = value;
        _writes.Add(new TimedWrite(_cycle, register, value));
    }

    // ------------------------------------------------------------------ digis

    private sealed class StreamState(Sample sample, double next)
    {
        public Sample Sample { get; } = sample;
        public double Next { get; set; } = next;
        public int Position { get; set; }
    }

    private void StartStream(PlaySample play)
    {
        int stream = Math.Clamp(play.Stream, 0, _streams.Length - 1);
        _streams[stream] = play.Sample >= 0 && play.Sample < _score.Samples.Count
            ? new StreamState(_score.Samples[play.Sample], _cycle)
            : null;
    }

    /// <summary>Every stream's values falling from <paramref name="from"/> up to <paramref name="to"/>.</summary>
    private void PlayStreams(long from, long to)
    {
        _streamWrites.Clear();
        for (int s = 0; s < _streams.Length; s++)
        {
            if (_streams[s] is not { } stream)
                continue;

            var sample = stream.Sample;
            while (stream.Next < to)
            {
                if (stream.Position >= sample.Values.Length)
                {
                    if (sample.LoopFrom < 0 || sample.LoopFrom >= sample.Values.Length)
                    {
                        _streams[s] = null;
                        break;
                    }
                    stream.Position = sample.LoopFrom;
                }

                int value = sample.Values[stream.Position++];
                if (sample.VolumeNibble && sample.Register == 0x18)
                    value = (_modeVolume & 0xF0) | (value & 0x0F);
                _streamWrites.Add(new TimedWrite(Math.Max(from, (long)stream.Next), sample.Register, value & 0xFF));
                stream.Next += Math.Max(1, sample.CyclesPerValue);
            }
        }
    }
}
