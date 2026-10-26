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

    /// <summary>What is on each register: what the player last wrote, 0 before it has - the SID
    /// starts with every register 0, so writing 0 to one changes nothing.</summary>
    private readonly int[] _written = new int[0x19];
    private readonly StreamState?[] _streams = new StreamState?[4];

    private long _cycle;
    private int _call;
    private int _between;
    private readonly List<TimedWrite> _rawBetween;
    private readonly int[] _before = new int[0x19];
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
        _modeVolume = (score.ModeVolume ?? 0) & 0xFF;
        if (score.FilterProgram >= 0)
            _filter = new Program(score.FilterProgram, 0, 0);
        _rawBetween = [.. score.Raw.Between.OrderBy(w => w.Cycle)];
        _cycle = score.CallCycles is { Count: > 0 } cycles ? cycles[0] : score.FirstCall;

        foreach (var voice in _voices)
            voice.Ended = score.Voices[voice.Index].Order.Count == 0;

        if (score.ModeVolume is not null)
            Write(0x18, _modeVolume);
        var init = new List<TimedWrite>(_writes);
        _writes.Clear();
        for (; _between < _rawBetween.Count && _rawBetween[_between].Cycle < _cycle; _between++)
            init.Add(_rawBetween[_between]);
        Init = init;
    }

    /// <summary>What is written before the first call.</summary>
    public IReadOnlyList<TimedWrite> Init { get; }

    /// <summary>The cycle the next call happens on.</summary>
    public long Cycle => _cycle;

    public int CyclesPerCall => _cyclesPerCall;

    /// <summary>The number of the next call, from 0.</summary>
    public int CallNumber => _call;

    /// <summary>The last call's own writes - the music's and the raw layer's for that call.</summary>
    public IReadOnlyList<TimedWrite> CallWrites => _writes;

    /// <summary>The writes after the last call's own up to the next call: digis, and the raw
    /// layer's writes between calls.</summary>
    public IReadOnlyList<TimedWrite> BetweenWrites => _streamWrites;

    /// <summary>Plays one call: the writes it makes, and the digi writes up to the next call.</summary>
    public IReadOnlyList<TimedWrite> Call() => Call(null);

    /// <summary>
    /// Plays one call, its raw writes decided by <paramref name="raw"/> - given the music's own
    /// writes for the call, it says which registers to write as given instead - rather than taken
    /// from the score. How a transcriber finds the raw layer: playing the score as it goes, and
    /// writing outright what the music gets wrong.
    /// </summary>
    public IReadOnlyList<TimedWrite> Call(Func<int, IReadOnlyList<TimedWrite>, RawCall?>? raw)
    {
        _writes.Clear();
        Array.Copy(_written, _before, _written.Length);
        foreach (var voice in _voices)
            PlayVoice(voice);

        StepFilter();
        Write(0x15, _cutoff & 0x07);
        Write(0x16, (_cutoff >> 3) & 0xFF);
        Write(0x17, _filterControl);
        Write(0x18, _modeVolume);

        var given = raw is not null ? raw(_call, _writes) : _score.Raw.Calls.GetValueOrDefault(_call);
        if (given is not null)
            WriteRawCall(given);

        long next = _score.CallCycles is { } cycles && _call + 1 < cycles.Count ? cycles[_call + 1] : _cycle + _cyclesPerCall;
        PlayStreams(_cycle, next);
        for (; _between < _rawBetween.Count && _rawBetween[_between].Cycle < next; _between++)
            _streamWrites.Add(_rawBetween[_between]);
        _streamWrites.Sort((a, b) => a.Cycle.CompareTo(b.Cycle));
        _cycle = next;
        _call++;

        var all = new List<TimedWrite>(_writes.Count + _streamWrites.Count);
        all.AddRange(_writes);
        all.AddRange(_streamWrites);
        return all;
    }

    /// <summary>A call's raw writes in place of the music's to the registers they replace - and the
    /// voices and the chip carried on from what is then on them.</summary>
    private void WriteRawCall(RawCall raw)
    {
        _writes.RemoveAll(w => raw.Replaces(w.Register));
        for (int r = 0; r <= 0x18; r++)
        {
            if (!raw.Replaces(r))
                continue;
            _written[r] = _before[r];
            Carry(r, _before[r]);
        }

        foreach (var write in raw.Writes)
        {
            if (write.Register is < 0 or > 0x18)
                continue;
            _written[write.Register] = write.Value & 0xFF;
            _writes.Add(new TimedWrite(_cycle + write.Offset, write.Register, write.Value & 0xFF));
            Carry(write.Register, write.Value & 0xFF);
        }
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

        /// <summary>The note's frequency, from the call after a first control byte's.</summary>
        public int? PendingFrequency;

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

        if (!OnFirstCall(voice) && voice.PendingFrequency is { } pending)
        {
            voice.Frequency = pending;
            voice.PendingFrequency = null;
        }

        if (call > 0 || voice.NoteAtCall > 0)
            PitchEffects(voice, call);

        if (!OnFirstCall(voice))
        {
            StepWave(voice);
            StepPulse(voice);
        }

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

    /// <summary>A place in a voice's order list: the entry, the repeats of its pattern left, the
    /// pattern, its next row and the entry's transposition.</summary>
    private readonly record struct Place(int Entry, int RepeatLeft, Pattern? Pattern, int Next, int Transpose);

    private static Place PlaceOf(VoiceState voice) => new(voice.Entry, voice.RepeatLeft, voice.Pattern, voice.NextRow, voice.Transpose);

    /// <summary>Calls a row still to come will last, as <see cref="RowLength"/> will make it.</summary>
    private int LengthAhead(VoiceState voice, Row row, ref bool funkSecond)
    {
        if (row.Calls is { } calls)
            return Math.Max(1, calls);
        if (voice.Tempo is { } own)
            return own;
        if (_funk is { } funk)
        {
            funkSecond = !funkSecond;
            return Math.Max(1, funkSecond ? funk.First : funk.Second);
        }
        return _tempo;
    }

    /// <summary>The row after the voice's current one, through the order list - taking it when
    /// <paramref name="advance"/>, only looking otherwise.</summary>
    private Row? NextRow(VoiceState voice, bool advance)
    {
        if (RowAt(voice, PlaceOf(voice)) is not { } found)
            return null;

        if (advance)
        {
            var after = found.After;
            voice.Entry = after.Entry;
            voice.RepeatLeft = after.RepeatLeft;
            voice.Pattern = after.Pattern;
            voice.NextRow = after.Next;
            voice.Transpose = after.Transpose;
        }
        return found.Row;
    }

    /// <summary>The rows after the voice's current one, in turn, without taking any.</summary>
    private IEnumerable<Row> UpcomingRows(VoiceState voice)
    {
        var place = PlaceOf(voice);
        while (RowAt(voice, place) is { } found)
        {
            yield return found.Row;
            place = found.After;
        }
    }

    /// <summary>The row at <paramref name="at"/> - on into the next entries of the order list when
    /// its pattern is done - and the place after it.</summary>
    private (Row Row, Place After)? RowAt(VoiceState voice, Place at)
    {
        var order = _score.Voices[voice.Index].Order;
        var (entry, repeatLeft, pattern, next, transpose) = at;

        for (int guard = 0; guard < order.Count * 2 + 4; guard++)
        {
            if (pattern is not null && next < pattern.Rows.Count)
                return (pattern.Rows[next], new Place(entry, repeatLeft, pattern, next + 1, transpose));

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
        voice.PendingFrequency = null;
        voice.Note = note;
        voice.Gate = true;
        voice.CallsSinceNote = 0;
        voice.VibratoCall = 0;

        int frequency = NoteFrequency(voice, note);
        if (voice.Instrument is not { } instrument)
        {
            voice.Frequency = frequency;
            return;
        }

        voice.AttackDecay = instrument.AttackDecay;
        voice.SustainRelease = instrument.SustainRelease;
        voice.Wave = instrument.WaveProgram >= 0 ? new Program(instrument.WaveProgram, 0, 0) : Program.Stopped;
        if (instrument.PulseProgram >= 0)
            voice.Pulses = new Program(instrument.PulseProgram, 0, 0);
        if (instrument.FilterProgram >= 0)
            _filter = new Program(instrument.FilterProgram, 0, 0);
        voice.FirstCall = instrument.FirstControl is not null;
        if (voice.FirstCall)
            voice.PendingFrequency = frequency;
        else
            voice.Frequency = frequency;
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

    /// <summary>When the calls to the next note's start reach the instrument's gate timer, the
    /// gate goes off - and the envelope falls, for an envelope hard restart - so the next note's
    /// attack starts clean. The next note may start later in this row, or in a row after held
    /// ones, delayed or not; a note glided to is not restarted, and a note's own first call is
    /// never its hard restart.</summary>
    private void HardRestartBeforeNextNote(VoiceState voice, int call)
    {
        if (voice.Instrument is not { GateTimer: > 0 } instrument || instrument.HardRestart == HardRestartKind.None)
            return;

        int until;
        if (voice.Row.Note.Kind == RowNoteKind.Note && call < voice.NoteAtCall)
        {
            if (voice.Row.Effects.Any(e => e is TonePortamento))
                return;
            until = voice.NoteAtCall - call;
        }
        else
        {
            if (call == voice.NoteAtCall && voice.Row.Note.Kind == RowNoteKind.Note)
                return;

            until = voice.RowCalls - call;
            bool funkSecond = voice.FunkSecond, found = false;
            foreach (var row in UpcomingRows(voice))
            {
                if (row.Note.Kind == RowNoteKind.Note)
                {
                    if (row.Effects.Any(e => e is TonePortamento))
                        return;
                    until += row.Effects.OfType<NoteDelay>().Select(d => d.Calls).DefaultIfEmpty(0).First();
                    found = true;
                    break;
                }
                if (row.Note.Kind != RowNoteKind.Hold || until > instrument.GateTimer)
                    return;
                until += LengthAhead(voice, row, ref funkSecond);
            }
            if (!found)
                return;
        }

        if (until != instrument.GateTimer)
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
                            PitchKind.Offset => (NoteFrequency(voice, voice.Note) + pitch.Value) & 0xFFFF,
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
        if (voice.Retrigger && (control & 1) != 0 && _written[b + 4] is var last && (last & 1) != 0)
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
            Carry(target, value);
        }
    }

    /// <summary>The voice or chip state a write to <paramref name="target"/> sets.</summary>
    private void Carry(int target, int value)
    {
        {
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

    /// <summary>The bits of <paramref name="register"/> the SID keeps.</summary>
    public static int UsedBits(int register) => Used[register];

    private void Write(int register, int value, bool force = false)
    {
        int used = Used[register];
        if (!force && (_written[register] & used) == (value & used))
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
