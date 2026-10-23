using MagSid.Playback;

namespace MagSid.Playback.Tests;

/// <summary>
/// The undocumented opcodes a 6510 runs and music players use, each against what an NMOS 6510 does.
/// LFT's Lunatico would not play at all for want of SBX.
/// </summary>
public class Mos6502UndocumentedTests
{
    /// <summary>Runs <paramref name="code"/> at $1000 (an RTS is added) with zero page byte $10 set to
    /// <paramref name="zeroPage"/>.</summary>
    private static Mos6502 Run(byte zeroPage, params byte[] code)
    {
        var memory = new byte[0x10000];
        memory[0x10] = zeroPage;
        code.CopyTo(memory, 0x1000);
        memory[0x1000 + code.Length] = 0x60;
        var cpu = new Mos6502(memory);
        cpu.Call(0x1000);
        return cpu;
    }

    [Fact]
    public void Slo_shifts_memory_left_and_ors_it_into_a()
    {
        var cpu = Run(0x81, 0xA9, 0x01, 0x07, 0x10);            // LDA #$01, SLO $10
        Assert.Equal(0x02, cpu.Memory[0x10]);
        Assert.Equal(0x03, cpu.A);
        Assert.True(cpu.Carry);
    }

    [Fact]
    public void Rla_rotates_memory_left_and_ands_it_into_a()
    {
        var cpu = Run(0x81, 0x18, 0xA9, 0xFF, 0x27, 0x10);      // CLC, LDA #$FF, RLA $10
        Assert.Equal(0x02, cpu.Memory[0x10]);
        Assert.Equal(0x02, cpu.A);
        Assert.True(cpu.Carry);
    }

    [Fact]
    public void Sre_shifts_memory_right_and_eors_it_into_a()
    {
        var cpu = Run(0x03, 0xA9, 0x01, 0x47, 0x10);            // LDA #$01, SRE $10
        Assert.Equal(0x01, cpu.Memory[0x10]);
        Assert.Equal(0x00, cpu.A);
        Assert.True(cpu.Zero);
        Assert.True(cpu.Carry);
    }

    [Fact]
    public void Rra_rotates_memory_right_and_adds_it_to_a()
    {
        var cpu = Run(0x02, 0x18, 0xA9, 0x01, 0x67, 0x10);      // CLC, LDA #$01, RRA $10
        Assert.Equal(0x01, cpu.Memory[0x10]);
        Assert.Equal(0x02, cpu.A);
        Assert.False(cpu.Carry);
    }

    [Fact]
    public void Dcp_decrements_memory_and_compares_it_with_a()
    {
        var cpu = Run(0x05, 0xA9, 0x04, 0xC7, 0x10);            // LDA #$04, DCP $10
        Assert.Equal(0x04, cpu.Memory[0x10]);
        Assert.True(cpu.Zero);
        Assert.True(cpu.Carry);
    }

    [Fact]
    public void Lax_loads_a_and_x_and_sax_stores_a_and_x()
    {
        var lax = Run(0x80, 0xA7, 0x10);                         // LAX $10
        Assert.Equal((0x80, 0x80), (lax.A, lax.X));
        Assert.True(lax.Negative);

        var sax = Run(0x00, 0xA9, 0xF0, 0xA2, 0x3C, 0x87, 0x10); // LDA #$F0, LDX #$3C, SAX $10
        Assert.Equal(0x30, sax.Memory[0x10]);
    }

    [Fact]
    public void The_immediate_ones_anc_alr_arr_sbx_and_the_second_sbc()
    {
        var anc = Run(0, 0xA9, 0xFF, 0x0B, 0x80);                // LDA #$FF, ANC #$80
        Assert.Equal(0x80, anc.A);
        Assert.True(anc.Carry);

        var alr = Run(0, 0xA9, 0x07, 0x4B, 0x03);                // LDA #$07, ALR #$03
        Assert.Equal(0x01, alr.A);
        Assert.True(alr.Carry);

        var arr = Run(0, 0x18, 0xA9, 0xC0, 0x6B, 0xFF);          // CLC, LDA #$C0, ARR #$FF
        Assert.Equal(0x60, arr.A);
        Assert.True(arr.Carry);
        Assert.False(arr.Overflow);

        var sbx = Run(0, 0xA9, 0x0F, 0xA2, 0x07, 0xCB, 0x02);    // LDA #$0F, LDX #$07, SBX #$02
        Assert.Equal(0x05, sbx.X);
        Assert.True(sbx.Carry);

        var sbc = Run(0, 0x38, 0xA9, 0x05, 0xEB, 0x03);          // SEC, LDA #$05, SBC #$03 ($EB)
        Assert.Equal(0x02, sbc.A);
    }

    [Fact]
    public void Arr_in_decimal_mode_fixes_up_both_nibbles()
    {
        // "No More Secrets", ARR in decimal mode: AND $FF with $FF gives $FF; ROR with carry clear
        // gives $7F; both nibbles of $FF are over 5, so each gets +6 and the carry is set.
        var cpu = Run(0, 0xF8, 0x18, 0xA9, 0xFF, 0x6B, 0xFF, 0xD8);  // SED, CLC, LDA #$FF, ARR #$FF, CLD
        Assert.Equal(0xD5, cpu.A);       // $7F -> low nibble $F+6 = $5, high $7+6 = $D
        Assert.True(cpu.Carry);
        Assert.False(cpu.Negative);      // N is the carry going in
        Assert.False(cpu.Overflow);      // bit 6 is set in $FF and in $7F alike

        // $40 AND $40 rotated is $20: bit 6 changed, so V is set; neither nibble needs a fixup.
        var changed = Run(0, 0xF8, 0x18, 0xA9, 0x40, 0x6B, 0x40, 0xD8);
        Assert.Equal(0x20, changed.A);
        Assert.True(changed.Overflow);
        Assert.False(changed.Carry);
    }

    [Fact]
    public void An_undocumented_nop_skips_its_operand_bytes()
    {
        // NOP abs,x then NOP #imm then NOP: the LDA after them must still be read as LDA.
        var cpu = Run(0, 0x1C, 0x34, 0x12, 0x80, 0x55, 0x1A, 0xA9, 0x42);
        Assert.Equal(0x42, cpu.A);
    }

    [Fact]
    public void Jam_says_the_cpu_halted()
    {
        var exception = Assert.Throws<SidPlaybackException>(() => Run(0, 0x02));
        Assert.Contains("JAM", exception.Message);
    }
}
