using MagSid.Core.Serialization;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// The rastertime figure rests on two things: the CPU charging each instruction what a real 6502
/// does, and the CPU running the player correctly at all. The first is pinned on small programs
/// whose cost is known by hand; the second by playing every real song on the emulated 6502 and
/// comparing what reaches the SID with what the C# sequencer - which the player is kept in
/// lockstep with - writes on the same frames.
/// </summary>
public class RasterTimeTests
{
    /// <summary>Puts <paramref name="code"/> at <paramref name="at"/> and returns what a JSR to it
    /// costs, the JSR (6) and the RTS (6) at the end of each program included.</summary>
    private static long Cost(int at, params byte[] code)
    {
        var cpu = new PlayerCpu();
        Array.Copy(code, 0, cpu.Memory, at, code.Length);
        return cpu.Call(at);
    }

    private const byte Rts = 0x60;

    [Fact]
    public void An_indexed_read_pays_one_cycle_for_crossing_a_page()
    {
        // ldx #n (2), lda $10FF,x (4, or 5 across the page)
        Assert.Equal(6 + 2 + 4 + 6, Cost(0x1000, 0xA2, 0x00, 0xBD, 0xFF, 0x10, Rts));
        Assert.Equal(6 + 2 + 5 + 6, Cost(0x1000, 0xA2, 0x01, 0xBD, 0xFF, 0x10, Rts));
    }

    [Fact]
    public void An_indexed_store_costs_the_same_either_way()
    {
        // ldx #n (2), sta $10FF,x (5 always)
        Assert.Equal(6 + 2 + 5 + 6, Cost(0x1000, 0xA2, 0x00, 0x9D, 0xFF, 0x10, Rts));
        Assert.Equal(6 + 2 + 5 + 6, Cost(0x1000, 0xA2, 0x01, 0x9D, 0xFF, 0x10, Rts));
    }

    [Fact]
    public void A_branch_costs_two_three_taken_and_four_taken_across_a_page()
    {
        // lda #imm (2), beq (2 / 3 / 4)
        Assert.Equal(6 + 2 + 2 + 6, Cost(0x1000, 0xA9, 0x01, 0xF0, 0x01, Rts));
        Assert.Equal(6 + 2 + 3 + 6, Cost(0x1000, 0xA9, 0x00, 0xF0, 0x01, 0x00, Rts));

        // At $10FA the branch's next instruction is $10FE, and +5 lands on $1103.
        var cpu = new PlayerCpu();
        Array.Copy(new byte[] { 0xA9, 0x00, 0xF0, 0x05 }, 0, cpu.Memory, 0x10FA, 4);
        cpu.Memory[0x1103] = Rts;
        Assert.Equal(6 + 2 + 4 + 6, cpu.Call(0x10FA));
    }

    [Fact]
    public void Indirect_indexed_reads_cross_pages_too()
    {
        // $20/$21 point at $10FF; ldy #n (2), lda ($20),y (5, or 6 across the page)
        var cpu = new PlayerCpu();
        cpu.Memory[0x20] = 0xFF;
        cpu.Memory[0x21] = 0x10;
        Array.Copy(new byte[] { 0xA0, 0x01, 0xB1, 0x20, Rts }, 0, cpu.Memory, 0x1000, 5);
        Assert.Equal(6 + 2 + 6 + 6, cpu.Call(0x1000));
    }

    [Fact]
    public void The_illegal_isc_costs_a_read_modify_write()
    {
        // ldx #0 (2), isc $10,x (6)
        Assert.Equal(6 + 2 + 6 + 6, Cost(0x1000, 0xA2, 0x00, 0xF7, 0x10, Rts));
    }

    [Fact]
    public void An_opcode_the_emitter_never_produces_stops_it()
    {
        // $EA, NOP, is not in the table - the player never needs one.
        Assert.Throws<InvalidOperationException>(() => Cost(0x1000, 0xEA, Rts));
    }

    private static string SongPath(string file) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SongData", file));

    [Theory]
    [InlineData("Airstrike.s")]
    [InlineData("BellaCiao.s")]
    [InlineData("Breakdance.s")]
    [InlineData("Lightforce.s")]
    [InlineData("Magnificent.s")]
    [InlineData("CityBomber4k(Megastyle).s")]
    [InlineData("SOS.s")]
    [InlineData("MysData2026 - Clarity.s")]
    [InlineData("Outlaws.s")]
    public void The_emulated_player_writes_what_the_sequencer_writes(string file)
    {
        string path = SongPath(file);
        Assert.True(File.Exists(path), $"song file missing: {path}");
        var song = PatternsFileReader.Read(path);

        var fromCpu = new Dictionary<int, int>();
        var cpu = new PlayerCpu((register, value) => fromCpu[register] = value);
        var image = SongImageBuilder.Build(song);
        Array.Copy(image, 0, cpu.Memory, 0x1000, image.Length);
        cpu.Call(0x1000, a: 0);

        var fromSequencer = new Dictionary<int, int>();
        var sequencer = new SongSequencer(song, (register, value) => fromSequencer[register] = value & 0xFF);

        // What the chip holds at the end of each frame. The order of writes inside a frame can
        // differ between the two without the SID ever seeing it.
        int frames = SongCoverageAnalyzer.PassFrames(song) + 200;
        for (int frame = 0; frame < frames; frame++)
        {
            fromCpu.Clear();
            fromSequencer.Clear();
            cpu.Call(0x1003);
            sequencer.Frame();

            Assert.True(fromCpu.Count == fromSequencer.Count
                        && fromCpu.All(w => fromSequencer.TryGetValue(w.Key, out int v) && v == w.Value),
                $"{file}, frame {frame}: the 6502 wrote {Format(fromCpu)}, the sequencer {Format(fromSequencer)}");
        }
    }

    private static string Format(Dictionary<int, int> writes) =>
        string.Join(" ", writes.OrderBy(w => w.Key).Select(w => $"D4{w.Key:X2}=${w.Value:X2}"));

    [Fact]
    public void A_measurement_reports_a_worst_call_no_cheaper_than_the_average()
    {
        var song = PatternsFileReader.Read(SongPath("BellaCiao.s"));
        var result = RasterTime.Measure(song, 3000);

        Assert.Equal(3000, result.Frames);
        Assert.True(result.InitCycles > 0);
        Assert.True(result.AverageCycles > 0);
        Assert.True(result.MaxCycles >= result.AverageCycles);
        Assert.Equal((int)Math.Ceiling(result.MaxCycles / 63.0), result.MaxLines);
    }
}
