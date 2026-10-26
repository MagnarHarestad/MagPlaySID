using MagPlay.Score;

namespace MagPlay.Score.Tests;

/// <summary>
/// The score player, effect by effect: what each does to the registers, call by call. These are
/// the semantics a transcription is written in, so each is pinned down here - the order things
/// happen in a call included.
/// </summary>
public class ScorePlayerTests
{
    private static readonly int[] Notes = NoteTables.MagSidPal();
    private const int C4 = 48, E4 = 52, G4 = 55, C5 = 60;

    /// <summary>One voice, one plain instrument: attack 0, decay 9, sustain A, a pulse wave held;
    /// no hard restart unless a test asks for one.</summary>
    private static Score Song(int tempo, params Row[] rows)
    {
        var score = new Score { Tempo = tempo };
        score.Instruments.Add(new Instrument
        {
            Number = 1, AttackDecay = 0x09, SustainRelease = 0xA0, WaveProgram = 0, GateTimer = 0,
        });
        score.WaveTable.AddRange([new WaveStep(0x41), new JumpStep(-1)]);
        var pattern = new Pattern { Number = 1 };
        pattern.Rows.AddRange(rows);
        score.Patterns.Add(pattern);
        score.Voices[0].Order.Add(new OrderEntry(1));
        return score;
    }

    /// <summary>The registers after each call.</summary>
    private static int[][] States(Score score, int calls)
    {
        var player = new ScorePlayer(score);
        var registers = new int[0x19];
        foreach (var write in player.Init)
            registers[write.Register] = write.Value;

        var states = new int[calls][];
        for (int i = 0; i < calls; i++)
        {
            foreach (var write in player.Call())
                registers[write.Register] = write.Value;
            states[i] = (int[])registers.Clone();
        }
        return states;
    }

    private static int Frequency(int[] state, int voice = 0) => state[voice * 7] | (state[voice * 7 + 1] << 8);

    private static int Control(int[] state, int voice = 0) => state[voice * 7 + 4];

    [Fact]
    public void A_note_starts_on_its_rows_first_call_with_the_tables_frequency_and_the_gate()
    {
        var state = States(Song(6, Row.Of(C4, 1)), 1)[0];

        Assert.Equal(Notes[C4], Frequency(state));
        Assert.Equal(0x41, Control(state));
        Assert.Equal(0x09, state[5]);
        Assert.Equal(0xA0, state[6]);
    }

    [Fact]
    public void A_row_lasts_the_tempos_calls()
    {
        var states = States(Song(6, Row.Of(C4, 1), Row.Hold, Row.Of(E4)), 13);

        Assert.Equal(Notes[C4], Frequency(states[11]));
        Assert.Equal(Notes[E4], Frequency(states[12]));
    }

    [Fact]
    public void The_hard_restart_quietens_the_voice_the_gate_timers_calls_before_the_next_note()
    {
        var score = Song(6, Row.Of(C4, 1), Row.Of(E4));
        score.Instruments[0].GateTimer = 2;
        var states = States(score, 7);

        Assert.Equal(0x41, Control(states[3]));
        Assert.Equal(0x40, Control(states[4]));
        Assert.Equal((0x0F, 0x00), (states[4][5], states[4][6]));
        Assert.Equal(0x41, Control(states[6]));
        Assert.Equal((0x09, 0xA0), (states[6][5], states[6][6]));
    }

    [Fact]
    public void No_hard_restart_comes_when_the_next_row_has_no_note()
    {
        var score = Song(6, Row.Of(C4, 1), Row.Hold);
        score.Instruments[0].GateTimer = 2;

        Assert.Equal(0x41, Control(States(score, 6)[4]));
    }

    [Fact]
    public void A_notes_first_call_writes_the_instruments_first_control_byte_then_the_wave_program_runs()
    {
        var score = Song(6, Row.Of(C4, 1), Row.Of(G4));
        score.Instruments[0].FirstControl = 0x09;
        score.Instruments[0].PulseProgram = score.PulseTable.Count;
        score.PulseTable.AddRange([new PulseStep(0x800, 0x10, 100), new JumpStep(-1)]);
        var states = States(score, 8);

        Assert.Equal([0x09, 0x41], states.Take(2).Select(s => Control(s)));
        Assert.Equal([0, Notes[C4]], states.Take(2).Select(s => Frequency(s)));
        Assert.Equal([0, 0x800, 0x810], states.Take(3).Select(s => s[2] | (s[3] << 8)));

        // The next note's first call: the voice as it was, then the new pitch and pulse program.
        Assert.Equal((0x09, Notes[C4], 0x840), (Control(states[6]), Frequency(states[6]), states[6][2] | (states[6][3] << 8)));
        Assert.Equal((Notes[G4], 0x800), (Frequency(states[7]), states[7][2] | (states[7][3] << 8)));
    }

    [Fact]
    public void A_wave_program_steps_relative_pitches_and_loops()
    {
        var score = Song(8, Row.Of(C4, 1));
        score.WaveTable.Clear();
        score.WaveTable.AddRange([new WaveStep(0x41, Pitch.Relative(0)), new WaveStep(null, Pitch.Relative(12)), new JumpStep(0)]);
        var states = States(score, 4);

        Assert.Equal([Notes[C4], Notes[C5], Notes[C4], Notes[C5]], states.Select(s => Frequency(s)));
    }

    [Fact]
    public void A_wave_step_holds_an_absolute_note_or_a_raw_frequency()
    {
        var score = Song(8, Row.Of(C4, 1));
        score.WaveTable.Clear();
        score.WaveTable.AddRange([new WaveStep(0x81, Pitch.Absolute(84), Calls: 2), new WaveStep(0x41, Pitch.Frequency(0x1234)), new JumpStep(-1)]);
        var states = States(score, 4);

        Assert.Equal((Notes[84], 0x81), (Frequency(states[1]), Control(states[1])));
        Assert.Equal((0x1234, 0x41), (Frequency(states[2]), Control(states[2])));
        Assert.Equal(0x1234, Frequency(states[3]));
    }

    [Fact]
    public void Portamento_moves_the_frequency_every_call_after_the_first_of_its_row()
    {
        var states = States(Song(6, Row.Of(C4, 1, new Portamento(0x20)), Row.Hold), 8);

        Assert.Equal(Notes[C4], Frequency(states[0]));
        Assert.Equal(Notes[C4] + 0x20, Frequency(states[1]));
        Assert.Equal(Notes[C4] + 5 * 0x20, Frequency(states[5]));
        Assert.Equal(Notes[C4] + 5 * 0x20, Frequency(states[7]));
    }

    [Fact]
    public void Tone_portamento_glides_to_the_note_without_starting_it_again_and_stops_there()
    {
        var score = Song(30, Row.Of(C4, 1), Row.Of(E4, 0, new TonePortamento(100)));
        var player = new ScorePlayer(score);
        var timeline = new List<TimedWrite>();
        for (int i = 0; i < 60; i++)
            timeline.AddRange(player.Call());
        var states = States(score, 60);

        Assert.Equal(Notes[C4], Frequency(states[30]));
        Assert.Equal(Notes[C4] + 100, Frequency(states[31]));
        Assert.Equal(Notes[E4], Frequency(states[59]));
        Assert.All(states.Skip(30), s => Assert.InRange(Frequency(s), Notes[C4], Notes[E4]));
        Assert.DoesNotContain(timeline, w => w.Register == 4 && (w.Value & 1) == 0);
    }

    [Fact]
    public void Tone_portamento_at_speed_zero_ties_the_note_on_at_once()
    {
        var states = States(Song(6, Row.Of(C4, 1), Row.Of(E4, 0, new TonePortamento(0))), 7);

        Assert.Equal(Notes[E4], Frequency(states[6]));
        Assert.Equal(0x41, Control(states[6]));
    }

    [Fact]
    public void Vibrato_goes_up_a_quarter_down_a_half_and_back_to_the_pitch()
    {
        var states = States(Song(9, Row.Of(C4, 1, new VibratoEffect(2, 10))), 9);
        int[] offsets = [.. states.Select(s => Frequency(s) - Notes[C4])];

        Assert.Equal([0, 10, 20, 10, 0, -10, -20, -10, 0], offsets);
    }

    [Fact]
    public void An_instruments_own_vibrato_starts_after_its_delay_on_rows_without_a_pitch_effect()
    {
        var score = Song(12, Row.Of(C4, 1));
        score.Instruments[0].Vibrato = new Vibrato(Delay: 4, Speed: 1, Depth: 5);
        var states = States(score, 6);

        Assert.Equal(Notes[C4], Frequency(states[3]));
        Assert.Equal(Notes[C4] + 5, Frequency(states[4]));
        Assert.Equal(Notes[C4], Frequency(states[5]));
    }

    [Fact]
    public void Arpeggio_steps_the_note_and_two_intervals_a_call_each()
    {
        var states = States(Song(6, Row.Of(C4, 1, new Arpeggio(4, 7))), 5);

        Assert.Equal([Notes[C4], Notes[E4], Notes[G4], Notes[C4], Notes[E4]], states.Select(s => Frequency(s)));
    }

    [Fact]
    public void A_pulse_program_sets_the_width_then_adds_to_it_a_call_at_a_time()
    {
        var score = Song(8, Row.Of(C4, 1));
        score.Instruments[0].PulseProgram = 0;
        score.PulseTable.AddRange([new PulseStep(Width: 0x800), new PulseStep(Add: 0x40, Calls: 3), new JumpStep(-1)]);
        var states = States(score, 5);

        Assert.Equal([0x800, 0x840, 0x880, 0x8C0, 0x8C0], states.Select(s => s[2] | (s[3] << 8)));
    }

    [Fact]
    public void A_filter_program_sets_cutoff_resonance_routing_and_type_then_sweeps()
    {
        var score = Song(8, Row.Of(C4, 1));
        score.Instruments[0].FilterProgram = 0;
        score.FilterTable.AddRange([new FilterStep(Cutoff: 0x400, Control: 0xF1, Mode: 0x10), new FilterStep(Add: -8, Calls: 2), new JumpStep(-1)]);
        var states = States(score, 3);

        Assert.Equal((0x00, 0x80, 0xF1, 0x1F), (states[0][0x15], states[0][0x16], states[0][0x17], states[0][0x18]));
        Assert.Equal(0x7F, states[1][0x16]);
        Assert.Equal(0x7E, states[2][0x16]);
    }

    [Fact]
    public void The_order_list_transposes_repeats_and_loops()
    {
        var score = Song(2, Row.Of(C4, 1));
        score.Patterns.Add(new Pattern { Number = 2, Rows = { Row.Of(C4, 1) } });
        score.Voices[0].Order.Clear();
        score.Voices[0].Order.AddRange([new OrderEntry(1, Repeat: 2), new OrderEntry(2, Transpose: 12)]);
        var states = States(score, 7);

        Assert.Equal([Notes[C4], Notes[C4], Notes[C5], Notes[C4]], new[] { 0, 2, 4, 6 }.Select(c => Frequency(states[c])));
    }

    [Fact]
    public void Funk_tempo_alternates_the_two_row_lengths()
    {
        var score = Song(6, Row.Of(C4, 1), Row.Of(E4), Row.Of(G4));
        score.FunkTempo = 3;
        var states = States(score, 10);

        Assert.Equal(Notes[C4], Frequency(states[5]));
        Assert.Equal(Notes[E4], Frequency(states[6]));
        Assert.Equal(Notes[E4], Frequency(states[8]));
        Assert.Equal(Notes[G4], Frequency(states[9]));
    }

    [Fact]
    public void Funk_and_tempo_effects_change_the_row_lengths_from_the_next_row()
    {
        var states = States(Song(6, Row.Of(C4, 1, new Tempo(2)), Row.Of(E4), Row.Of(G4)), 9);

        Assert.Equal(Notes[E4], Frequency(states[6]));
        Assert.Equal(Notes[G4], Frequency(states[8]));
    }

    [Fact]
    public void A_row_can_last_its_own_number_of_calls()
    {
        var states = States(Song(6, Row.Of(C4, 1) with { Calls = 5 }, Row.Of(E4)), 6);

        Assert.Equal(Notes[E4], Frequency(states[5]));
    }

    [Fact]
    public void A_note_delay_starts_the_note_later_in_its_row_and_a_note_cut_ends_it()
    {
        var states = States(Song(6, Row.Of(C4, 1, new NoteDelay(2), new NoteCut(4))), 5);

        Assert.Equal(0, Control(states[1]) & 1);
        Assert.Equal((Notes[C4], 0x41), (Frequency(states[2]), Control(states[2])));
        Assert.Equal(0x40, Control(states[4]));
    }

    [Fact]
    public void Volume_and_filter_type_effects_share_D418()
    {
        var state = States(Song(6, Row.Of(C4, 1, new MasterVolume(5), new FilterMode(0x30))), 1)[0];

        Assert.Equal(0x35, state[0x18]);
    }

    [Fact]
    public void A_new_note_with_the_gate_still_on_turns_it_off_and_on_in_the_same_call()
    {
        var score = Song(3, Row.Of(C4, 1), Row.Of(E4));
        score.Instruments[0].HardRestart = HardRestartKind.None;
        var player = new ScorePlayer(score);
        for (int i = 0; i < 3; i++)
            player.Call();
        var controls = player.Call().Where(w => w.Register == 4).Select(w => w.Value);

        Assert.Equal([0x40, 0x41], controls);
    }

    [Fact]
    public void Calls_are_a_frame_apart_on_PAL_and_on_NTSC_and_a_call_rate_effect_reprograms_the_timer()
    {
        var pal = new ScorePlayer(Song(6, Row.Of(C4, 1, new CallRate(10000))));
        pal.Call();
        Assert.Equal(10000, pal.Cycle);

        var ntsc = Song(6, Row.Of(C4, 1));
        ntsc.Machine = Machine.Ntsc;
        ntsc.CyclesPerCall = Machines.CyclesPerFrame(Machine.Ntsc);
        Assert.InRange(ntsc.CallsPerSecond, 59.8, 59.9);
        Assert.InRange(new Score().CallsPerSecond, 50.1, 50.2);
    }

    [Fact]
    public void A_multispeed_tune_makes_several_calls_a_frame()
    {
        var score = Song(1, Row.Of(C4, 1, new Portamento(1)) with { Calls = 100 });
        score.CyclesPerCall = Machines.CyclesPerFrame(Machine.Pal) / 4;
        var frames = ScoreTrace.Frames(score, 2);

        Assert.Equal(4, frames.Frames[0].Count(w => w.Register == 0));
        Assert.Equal(4, frames.Frames[1].Count(w => w.Register == 0));
    }

    [Fact]
    public void A_digi_streams_its_values_into_the_volume_nibble_between_calls()
    {
        var score = Song(6, Row.Of(C4, 1, new PlaySample(0)));
        score.ModeVolume = 0x1F;
        score.Samples.Add(new Sample { Values = [1, 2, 3, 4], CyclesPerValue = 1000 });
        var digi = new ScorePlayer(score).Call().Where(w => w.Register == 0x18).ToList();

        Assert.Equal([(0L, 0x11), (1000L, 0x12), (2000L, 0x13), (3000L, 0x14)], digi.Select(w => (w.Cycle, w.Value)));
    }

    [Fact]
    public void Raw_steps_make_exactly_the_writes_they_say_so_any_tune_can_be_written()
    {
        var random = new Random(64);
        int[] registers = [0, 1, 2, 3, 4, 5, 6, 0x15, 0x16, 0x17, 0x18];
        var calls = new List<(int Register, int Value)[]>();
        calls.Add([.. registers.Select(r => (r, random.Next(256)))]);
        for (int i = 1; i < 50; i++)
        {
            calls.Add([.. Enumerable.Range(0, random.Next(0, 6))
                .Select(_ => registers[random.Next(registers.Length)])
                .Select(r => (r, random.Next(256)))]);
        }

        var score = Song(6, Row.Of(C4, 1) with { Calls = 1000 });
        score.Instruments[0].HardRestart = HardRestartKind.None;
        score.WaveTable.Clear();
        score.WaveTable.AddRange(calls.Select(c => new RawStep(c)));
        score.WaveTable.Add(new JumpStep(-1));

        var player = new ScorePlayer(score);
        foreach (var expected in calls)
            Assert.Equal(expected, player.Call().Select(w => (w.Register, w.Value)));
    }

    [Fact]
    public void A_raw_call_writes_its_registers_as_given_and_the_music_carries_on_from_them()
    {
        var score = Song(6, Row.Of(C4, 1) with { Calls = 100 });
        score.Raw.Calls[1] = new RawCall(0b11, [new RawWrite(300, 0, 0x34), new RawWrite(310, 1, 0x12)]);
        score.Raw.Calls[2] = new RawCall(1 << 4, []);
        var player = new ScorePlayer(score);
        player.Call();
        var second = player.Call();
        var third = player.Call();

        Assert.Equal([(score.CyclesPerCall + 300L, 0, 0x34), (score.CyclesPerCall + 310L, 1, 0x12)],
                     second.Select(w => (w.Cycle, w.Register, w.Value)));
        Assert.Empty(third);
        Assert.Equal(0x1234, Frequency(States(score, 4)[3]));
    }

    [Fact]
    public void A_filter_program_of_the_scores_own_runs_from_the_first_call()
    {
        var score = new Score { ModeVolume = null, FilterProgram = 0 };
        score.FilterTable.AddRange([new FilterStep(0x100, 0xF1, 0x10, Add: 8, Calls: 3), new JumpStep(-1)]);
        var states = States(score, 4);

        Assert.Empty(new ScorePlayer(score).Init);
        Assert.Equal([0x100, 0x108, 0x110, 0x110], states.Select(s => (s[0x15] & 7) | (s[0x16] << 3)));
        Assert.All(states, s => Assert.Equal((0xF1, 0x10), (s[0x17], s[0x18])));
    }

    [Fact]
    public void Effects_read_as_tracker_commands()
    {
        Assert.Equal("120", new Portamento(0x20).Label);
        Assert.Equal("205", new Portamento(-5).Label);
        Assert.Equal("E85", new FunkTempo(8, 5).Label);
        Assert.Equal("F86", new Tempo(6, ThisVoiceOnly: true).Label);
        Assert.Equal("047", new Arpeggio(4, 7).Label);
    }
}
