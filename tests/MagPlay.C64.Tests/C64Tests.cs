using MagPlay.Score;
using MagSid.Playback;

namespace MagPlay.C64.Tests;

/// <summary>
/// The C64 a tune is played on, one piece at a time, with small tunes written for it: the CPU's
/// cycles, the timers and the raster interrupt, the PAL and NTSC frame, the driver that starts
/// PSID and RSID tunes, an NMI digi, and the SID read back.
/// </summary>
public class C64Tests
{
    private const int Pal = 985248;

    /// <summary>A .sid around <paramref name="code"/>, which loads at $1000.</summary>
    private static SidFile Tune(byte[] code, int init = 0x1000, int play = 0, bool rsid = false,
                                bool ciaTimed = false, bool ntsc = false, int secondSid = 0)
    {
        var bytes = new byte[0x7C + code.Length];
        "PSID"u8.CopyTo(bytes);
        if (rsid)
            "RSID"u8.CopyTo(bytes);
        void Word(int at, int value)
        {
            bytes[at] = (byte)(value >> 8);
            bytes[at + 1] = (byte)value;
        }

        Word(0x04, secondSid != 0 ? 3 : 2);
        Word(0x06, 0x7C);
        Word(0x08, 0x1000);
        Word(0x0A, init);
        Word(0x0C, play);
        Word(0x0E, 1);
        Word(0x10, 1);
        bytes[0x15] = (byte)(ciaTimed ? 1 : 0);
        Word(0x76, (ntsc ? 0x08 : 0x04) | 0x10);
        bytes[0x7A] = (byte)(secondSid >> 4);
        code.CopyTo(bytes, 0x7C);
        return SidFile.Parse(bytes);
    }

    private static List<long> Gaps(IEnumerable<TimedWrite> writes)
    {
        var cycles = writes.Select(w => w.Cycle).ToList();
        return [.. cycles.Zip(cycles.Skip(1), (a, b) => b - a)];
    }

    /// <summary>Calls the given number of cycles apart: exactly, on average, and each within the
    /// few cycles an interrupt waits for the instruction it arrives in - as on a real C64.</summary>
    private static void Spaced(IReadOnlyList<long> gaps, int cycles)
    {
        Assert.InRange(gaps.Average(), cycles - 0.5, cycles + 0.5);
        Assert.All(gaps, gap => Assert.InRange(gap, cycles - 3, cycles + 3));
    }

    [Fact]
    public void The_cpu_counts_the_6510s_cycles_with_a_page_crossed_and_a_branch_taken()
    {
        var machine = new C64(Machine.Pal);
        byte[] code =
        [
            0xA9, 0x01,         // lda #$01         2
            0x8D, 0x00, 0x20,   // sta $2000        4
            0xA2, 0x01,         // ldx #$01         2
            0xBD, 0xFF, 0x20,   // lda $20ff,x      4 + 1, crossing into $2100
            0xA0, 0x00,         // ldy #$00         2
            0xF0, 0x00,         // beq +0           2 + 1, taken
        ];
        code.CopyTo(machine.Ram, 0x1000);
        machine.Cpu.PC = 0x1000;
        for (int i = 0; i < 6; i++)
            machine.Cpu.Step();

        Assert.Equal(18, machine.Cpu.Cycles);
    }

    [Fact]
    public void A_tune_that_installs_its_own_irq_runs_on_the_kernals_60_hz_timer()
    {
        // init: point $0314 at the handler and return; handler: inc $c000 / jmp $ea31
        byte[] code = [0xA9, 0x08, 0x8D, 0x14, 0x03, 0xA9, 0x10, 0x8D, 0x15, 0x03, 0x60, 0, 0, 0, 0, 0,
                       0xEE, 0x00, 0xC0, 0x4C, 0x31, 0xEA];
        // the handler is at $1010
        code[1] = 0x10;
        var tune = SidTune.Load(Tune(code, rsid: true), 1);
        tune.Run(Pal);

        Assert.InRange(tune.Computer.Ram[0xC000], 59, 60);
    }

    [Fact]
    public void A_psid_is_played_once_a_pal_frame_from_the_raster_interrupt()
    {
        // play: inc $c000 / lda $c000 / sta $d418 / rts
        byte[] code = [0x60, 0xEE, 0x00, 0xC0, 0xAD, 0x00, 0xC0, 0x8D, 0x18, 0xD4, 0x60];
        var writes = SidTune.Record(Tune(code, play: 0x1001), 1, 1.0).Where(w => w.Register == 0x18).ToList();

        Assert.InRange(writes.Count, 49, 51);
        Spaced(Gaps(writes), 312 * 63);
    }

    [Fact]
    public void A_psid_timed_by_the_cia_is_played_at_60_hz()
    {
        byte[] code = [0x60, 0xEE, 0x00, 0xC0, 0xAD, 0x00, 0xC0, 0x8D, 0x18, 0xD4, 0x60];
        var writes = SidTune.Record(Tune(code, play: 0x1001, ciaTimed: true), 1, 1.0).Where(w => w.Register == 0x18).ToList();

        Assert.InRange(writes.Count, 59, 61);
        Spaced(Gaps(writes), 0x4025 + 1);
    }

    [Fact]
    public void An_ntsc_psid_is_played_once_an_ntsc_frame()
    {
        byte[] code = [0x60, 0xEE, 0x00, 0xC0, 0xAD, 0x00, 0xC0, 0x8D, 0x18, 0xD4, 0x60];
        var file = Tune(code, play: 0x1001, ntsc: true);
        var writes = SidTune.Record(file, 1, 1.0).Where(w => w.Register == 0x18).ToList();

        Assert.True(file.IsNtsc);
        Spaced(Gaps(writes), 263 * 65);
    }

    [Fact]
    public void Inc_d019_acknowledges_the_raster_interrupt_by_writing_the_old_value_first()
    {
        // init: sei / handler into $0314 / raster line $80 / raster irq on / cia 1 quiet / cli / rts
        // handler: inc $c000 / inc $d019 / jmp $ea81
        byte[] code =
        [
            0x78, 0xA9, 0x20, 0x8D, 0x14, 0x03, 0xA9, 0x10, 0x8D, 0x15, 0x03,
            0xA9, 0x80, 0x8D, 0x12, 0xD0, 0xA9, 0x01, 0x8D, 0x1A, 0xD0,
            0xA9, 0x7F, 0x8D, 0x0D, 0xDC, 0x58, 0x60, 0, 0, 0, 0,
            0xEE, 0x00, 0xC0, 0xEE, 0x19, 0xD0, 0x4C, 0x81, 0xEA,
        ];
        var tune = SidTune.Load(Tune(code, rsid: true), 1);
        tune.Run(Pal);

        Assert.InRange(tune.Computer.Ram[0xC000], 49, 51);
    }

    [Fact]
    public void A_digi_streamed_from_cia_2s_nmi_writes_at_the_timers_rate()
    {
        // init: nmi vector $0318 -> $1020 / cia 2 timer a 123 / its interrupt on / started / rts
        // nmi: pha / lda $dd0d / inc $c001 / lda $c001 / and #$0f / sta $d418 / pla / rti
        var code = new byte[0x40];
        byte[] init =
        [
            0xA9, 0x20, 0x8D, 0x18, 0x03, 0xA9, 0x10, 0x8D, 0x19, 0x03,
            0xA9, 123, 0x8D, 0x04, 0xDD, 0xA9, 0x00, 0x8D, 0x05, 0xDD,
            0xA9, 0x81, 0x8D, 0x0D, 0xDD, 0xA9, 0x11, 0x8D, 0x0E, 0xDD, 0x60,
        ];
        byte[] nmi = [0x48, 0xAD, 0x0D, 0xDD, 0xEE, 0x01, 0xC0, 0xAD, 0x01, 0xC0, 0x29, 0x0F, 0x8D, 0x18, 0xD4, 0x68, 0x40];
        init.CopyTo(code, 0);
        nmi.CopyTo(code, 0x20);
        var writes = SidTune.Record(Tune(code, rsid: true), 1, 0.05).Where(w => w.Register == 0x18).ToList();
        var gaps = Gaps(writes);

        Assert.InRange(writes.Count, 390, 400);
        Assert.InRange(gaps.Average(), 123.5, 124.5);
        Assert.All(gaps, gap => Assert.InRange(gap, 118, 130));
    }

    [Fact]
    public void Voice_3s_oscillator_reads_back_as_the_sid_makes_it()
    {
        // init: voice 3 noise, high frequency, gate on / rts; play: lda $d41b / sta $d400 / rts
        byte[] code =
        [
            0xA9, 0xFF, 0x8D, 0x0F, 0xD4, 0xA9, 0x81, 0x8D, 0x12, 0xD4, 0x60,
            0xAD, 0x1B, 0xD4, 0x8D, 0x00, 0xD4, 0x60,
        ];
        var writes = SidTune.Record(Tune(code, play: 0x100B), 1, 0.5).Where(w => w.Register == 0x00).ToList();

        Assert.True(writes.Select(w => w.Value).Distinct().Count() > 5);
    }

    [Fact]
    public void A_register_is_written_and_read_on_the_cycle_the_6510_makes_the_access()
    {
        // Timer A started by a store whose write is its last cycle of six, then read on the fourth
        // cycle of an absolute load: four cycles apart, whatever the instructions' lengths.
        var machine = new C64(Machine.Pal);
        byte[] code =
        [
            0xA9, 0xFF, 0x8D, 0x04, 0xDC, 0x8D, 0x05, 0xDC,   // lda #$ff / sta $dc04 / sta $dc05
            0xA9, 0x11, 0xA0, 0x00, 0x91, 0xFB,               // lda #$11 / ldy #0 / sta ($fb),y
            0xAD, 0x04, 0xDC,                                 // lda $dc04
        ];
        code.CopyTo(machine.Ram, 0x1000);
        machine.Ram[0xFB] = 0x0E;
        machine.Ram[0xFC] = 0xDC;
        machine.Cpu.PC = 0x1000;
        for (int i = 0; i < 7; i++)
            machine.Cpu.Step();

        Assert.Equal(0xFF - 4, machine.Cpu.A);
    }

    [Fact]
    public void An_irq_waiting_at_cli_is_taken_after_the_instruction_that_follows_it()
    {
        // init: raise CIA 1's interrupt, then cli / inc $c000 / sei / rts; the IRQ handler keeps
        // in $c001 what $c000 was when it ran.
        var code = new byte[0x40];
        byte[] init =
        [
            0x78, 0xA9, 0x30, 0x8D, 0x14, 0x03, 0xA9, 0x10, 0x8D, 0x15, 0x03,  // sei / $0314 = $1030
            0xA9, 0x00, 0x8D, 0x05, 0xDC, 0x8D, 0x04, 0xDC,                    // timer a latch 0
            0xA9, 0x81, 0x8D, 0x0D, 0xDC, 0xA9, 0x19, 0x8D, 0x0E, 0xDC,        // irq on, one-shot start
            0xEA, 0xEA, 0xEA, 0xEA,                                            // the underflow is waiting
            0x58, 0xEE, 0x00, 0xC0, 0x78, 0x60,                                // cli / inc $c000 / sei / rts
        ];
        byte[] irq = [0xAD, 0x00, 0xC0, 0x8D, 0x01, 0xC0, 0xAD, 0x0D, 0xDC, 0x4C, 0x81, 0xEA];
        init.CopyTo(code, 0);
        irq.CopyTo(code, 0x30);
        var tune = SidTune.Load(Tune(code, rsid: true), 1);
        tune.Run(2000);

        Assert.Equal(1, tune.Computer.Ram[0xC001]);
    }

    [Fact]
    public void An_rti_run_from_cia_2s_serial_register_acknowledges_the_nmi_with_its_dummy_read()
    {
        // init: CIA 1 quiet, $dd0c = rti, the NMI vector in RAM -> $1030, the KERNAL out, CIA 2's
        // timer A every 200 cycles; nmi: inc $c000 / jmp $dd0c - never reading $dd0d itself.
        var code = new byte[0x40];
        byte[] init =
        [
            0x78, 0xA9, 0x7F, 0x8D, 0x0D, 0xDC, 0xA9, 0x35, 0x85, 0x01, 0xA9, 0x40, 0x8D, 0x0C, 0xDD,
            0xA9, 0x30, 0x8D, 0xFA, 0xFF, 0xA9, 0x10, 0x8D, 0xFB, 0xFF,
            0xA9, 199, 0x8D, 0x04, 0xDD, 0xA9, 0x00, 0x8D, 0x05, 0xDD,
            0xA9, 0x81, 0x8D, 0x0D, 0xDD, 0xA9, 0x11, 0x8D, 0x0E, 0xDD, 0x60,
        ];
        byte[] nmi = [0xEE, 0x00, 0xC0, 0x4C, 0x0C, 0xDD];
        init.CopyTo(code, 0);
        nmi.CopyTo(code, 0x30);
        var tune = SidTune.Load(Tune(code, rsid: true), 1);
        tune.Run(200 * 100 + 1000);

        Assert.InRange(tune.Computer.Ram[0xC000], 100, 105);
    }

    [Fact]
    public void A_tune_that_clears_the_cassette_buffer_in_init_keeps_its_driver()
    {
        // init: clear $0334-$03ff / rts; play: inc $c000 / rts
        byte[] code = [0xA2, 0x00, 0x8A, 0x9D, 0x34, 0x03, 0xE8, 0xE0, 0xCC, 0xD0, 0xF8, 0x60, 0xEE, 0x00, 0xC0, 0x60];
        var tune = SidTune.Load(Tune(code, play: 0x100C), 1);
        tune.Run(Pal);

        Assert.InRange(tune.Computer.Ram[0xC000], 49, 51);
    }

    [Fact]
    public void A_second_sid_named_by_the_header_is_its_own_chip_and_otherwise_a_mirror()
    {
        // init: sta $d418 / sta $d438 with different values / rts
        byte[] code = [0xA9, 0x0F, 0x8D, 0x18, 0xD4, 0xA9, 0x05, 0x8D, 0x38, 0xD4, 0x60];
        var mirrored = SidTune.Load(Tune(code, rsid: true), 1);
        mirrored.Run(5000);
        var split = SidTune.Load(Tune(code, rsid: true, secondSid: 0xD420), 1);
        split.Run(5000);

        Assert.Equal([0x0F, 0x05], mirrored.Computer.SidWrites.Where(w => w.Register == 0x18).Select(w => w.Value));
        Assert.Equal([0x0F], split.Computer.SidWrites.Where(w => w.Register == 0x18).Select(w => w.Value));
        Assert.Equal([0x05], split.Computer.ExtraSidWrites[0].Where(w => w.Register == 0x18).Select(w => w.Value));
    }

    [Fact]
    public void A_basic_rsid_is_refused_with_a_reason()
    {
        var bytes = new byte[0x7E];
        "RSID"u8.CopyTo(bytes);
        bytes[5] = 2;
        bytes[7] = 0x7C;
        bytes[8] = 0x08;
        bytes[9] = 0x01;
        bytes[0x0F] = 1;
        bytes[0x11] = 1;
        bytes[0x77] = 0x02;

        Assert.Contains("BASIC", SidTune.Unsupported(SidFile.Parse(bytes)));
    }

    private sealed class Chip : ISidPort
    {
        public List<TimedWrite> Writes { get; } = [];

        public List<TimedWrite> Second { get; } = [];

        public void Write(long cycle, int chip, int register, int value) =>
            (chip == 0 ? Writes : Second).Add(new TimedWrite(cycle, register, value));

        public byte Read(long cycle, int chip, int register) => 0;
    }

    [Fact]
    public void A_chip_played_into_gets_every_write_on_its_cycle_as_a_recording_has_it()
    {
        byte[] code = [0x60, 0xEE, 0x00, 0xC0, 0xAD, 0x00, 0xC0, 0x8D, 0x18, 0xD4, 0x60];
        var file = Tune(code, play: 0x1001);
        var chip = new Chip();
        var played = SidTune.Load(file, 1, chip, record: false);
        played.Run(Pal / 2);

        Assert.Equal(SidTune.Record(file, 1, 0.5), chip.Writes);
        Assert.Empty(played.Computer.SidWrites);
    }

    /// <summary>The RMS of 16-bit mono samples.</summary>
    private static double Loudness(byte[] buffer) =>
        Math.Sqrt(Enumerable.Range(0, buffer.Length / 2).Average(i => Math.Pow(BitConverter.ToInt16(buffer, i * 2), 2)));

    [Fact]
    public void The_engine_plays_a_note_and_an_nmi_digi_out_loud_and_silence_while_paused()
    {
        // init: freq $1000, AD $00, SR $F0, triangle with the gate, volume 15 / rts
        byte[] note =
        [
            0xA9, 0x10, 0x8D, 0x01, 0xD4, 0xA9, 0xF0, 0x8D, 0x06, 0xD4,
            0xA9, 0x11, 0x8D, 0x04, 0xD4, 0xA9, 0x0F, 0x8D, 0x18, 0xD4, 0x60, 0x60,
        ];
        var tone = new C64PlaybackEngine(Tune(note, play: 0x1015), 1);
        var buffer = new byte[C64PlaybackEngine.SampleRate];
        tone.Read(buffer, 0, buffer.Length);
        Assert.True(Loudness(buffer) > 1000);

        // Frequency $1000 on PAL is 240.5 Hz, nearest B-3 - key 47 from C-0.
        var (frequency, control, envelope) = tone.VoiceAt(0, buffer.Length / 2 - 1);
        Assert.Equal((0x1000, 0x11), (frequency, control));
        Assert.True(envelope > 200);
        Assert.Equal(47, (int)Math.Round(tone.NoteOf(frequency)));

        tone.Paused = true;
        tone.Read(buffer, 0, buffer.Length);
        Assert.Equal(0, Loudness(buffer));

        // The tune's own sample stands still through the pause and goes on after it.
        tone.Paused = false;
        tone.Read(buffer, 0, buffer.Length);
        int half = buffer.Length / 2;
        Assert.Equal(half / 2, tone.TuneSampleAt(half / 2));
        Assert.Equal(half, tone.TuneSampleAt(half + half / 2));
        Assert.Equal(half + half / 2, tone.TuneSampleAt(2 * half + half / 2));

        // The digi of the NMI test above: $D418 stepped 0-15 from CIA 2's NMI.
        var code = new byte[0x40];
        byte[] init =
        [
            0xA9, 0x20, 0x8D, 0x18, 0x03, 0xA9, 0x10, 0x8D, 0x19, 0x03,
            0xA9, 123, 0x8D, 0x04, 0xDD, 0xA9, 0x00, 0x8D, 0x05, 0xDD,
            0xA9, 0x81, 0x8D, 0x0D, 0xDD, 0xA9, 0x11, 0x8D, 0x0E, 0xDD, 0x60,
        ];
        byte[] nmi = [0x48, 0xAD, 0x0D, 0xDD, 0xEE, 0x01, 0xC0, 0xAD, 0x01, 0xC0, 0x29, 0x0F, 0x8D, 0x18, 0xD4, 0x68, 0x40];
        init.CopyTo(code, 0);
        nmi.CopyTo(code, 0x20);
        var digi = new C64PlaybackEngine(Tune(code, rsid: true), 1);
        digi.Read(buffer, 0, buffer.Length);

        Assert.Null(digi.Failure);
        Assert.True(Loudness(buffer) > 1000);
    }

    [Fact]
    public void A_tunes_length_is_measured_on_the_c64_by_its_memory_coming_round_again()
    {
        // play: inc $c000 / lda $c000 / sta $d418 / rts - a page outside the tune, round every 256 calls.
        byte[] code = [0x60, 0xEE, 0x00, 0xC0, 0xAD, 0x00, 0xC0, 0x8D, 0x18, 0xD4, 0x60];
        var length = C64SongLength.Measure(Tune(code, play: 0x1001), 1);

        Assert.NotNull(length);
        Assert.Equal(256, length.LoopFrames);
        Assert.InRange(length.CallsPerSecond, 50.1, 50.15);

        // A 16-bit counter - inc $c000 / bne / inc $c001 - comes round only after 22 minutes.
        byte[] counter = [0x60, 0xEE, 0x00, 0xC0, 0xD0, 0x03, 0xEE, 0x01, 0xC0, 0x60];
        Assert.Null(C64SongLength.Measure(Tune(counter, play: 0x1001), 1, maxSeconds: 30));
    }

    [Fact]
    public void A_2sid_tune_plays_its_second_chip_through_the_port_and_out_loud()
    {
        // init: freq hi $10, SR $F0, triangle and gate, volume 15 - all on the SID at $D420 / rts
        byte[] code =
        [
            0xA9, 0x10, 0x8D, 0x21, 0xD4, 0xA9, 0xF0, 0x8D, 0x26, 0xD4,
            0xA9, 0x11, 0x8D, 0x24, 0xD4, 0xA9, 0x0F, 0x8D, 0x38, 0xD4, 0x60, 0x60,
        ];
        var file = Tune(code, play: 0x1015, secondSid: 0xD420);
        var chip = new Chip();
        SidTune.Load(file, 1, chip, record: false).Run(10000);
        Assert.Empty(chip.Writes);
        Assert.Equal([(0x01, 0x10), (0x06, 0xF0), (0x04, 0x11), (0x18, 0x0F)], chip.Second.Select(w => (w.Register, w.Value)));

        var engine = new C64PlaybackEngine(file, 1);
        var buffer = new byte[C64PlaybackEngine.SampleRate];
        engine.Read(buffer, 0, buffer.Length);
        Assert.Equal(2, engine.Chips);
        Assert.True(Loudness(buffer) > 1000);
        var (frequency, control, _) = engine.VoiceAt(3, buffer.Length / 2 - 1);
        Assert.Equal((0x1000, 0x11), (frequency, control));

        // Its own scope traces it; muting the first chip's voice 1 leaves it sounding, its own silences it.
        var scope = new float[SidFilePlaybackEngine.ScopeSamples];
        engine.CaptureVoiceScope(3, scope, buffer.Length / 2);
        Assert.True(scope.Max() - scope.Min() > 0.5f);
        engine.SetVoiceMuted(0, true);
        engine.Read(buffer, 0, buffer.Length);
        Assert.True(Loudness(buffer) > 1000);
        engine.SetVoiceMuted(3, true);

        // The step the muted voice's DC level leaves takes the output filter a moment to settle.
        engine.Read(buffer, 0, buffer.Length);
        engine.Read(buffer, 0, buffer.Length);
        Assert.True(Loudness(buffer) < 200, $"loudness {Loudness(buffer)}");
    }

    [Fact]
    public void The_header_says_each_extra_sids_model_and_an_unknown_one_is_the_first_chips()
    {
        var bytes = new byte[0x7E];
        "PSID"u8.CopyTo(bytes);
        bytes[5] = 4;
        bytes[7] = 0x7C;
        bytes[8] = 0x10;
        bytes[0x0F] = 1;
        bytes[0x11] = 1;
        bytes[0x76] = 0x00;   // third SID: unknown
        bytes[0x77] = 0x60;   // first 8580, second 6581
        bytes[0x7A] = 0x42;
        bytes[0x7B] = 0x44;
        var file = SidFile.Parse(bytes);

        Assert.Equal([0xD420, 0xD440], file.ExtraSids);
        Assert.Equal([false, true], file.ExtraSids8580);
    }
}
