namespace MagSid.Assembler.Tests;

using static Operand;

/// <summary>
/// The emitter's own behaviour: label resolution, the zero-page/absolute choice, and the errors it
/// raises rather than quietly emitting something wrong. OpcodeTableTests covers which byte each
/// instruction is; this covers everything around them.
/// </summary>
public class EmitterTests
{
    [Fact]
    public void Resolves_a_label_referenced_before_it_is_defined()
    {
        var emitter = new Emitter6502(0x1000);
        var target = emitter.NewLabel("target");
        emitter.Jmp(target);            // $1000: 4C .. ..
        emitter.Rts();                  // $1003: 60
        emitter.Define(target);         // $1004

        Assert.Equal(new byte[] { 0x4C, 0x04, 0x10, 0x60 }, emitter.Assemble());
    }

    [Fact]
    public void Measures_a_backward_branch_from_the_following_instruction()
    {
        var emitter = new Emitter6502(0x1000);
        var loop = emitter.DefineHere("loop");   // $1000
        emitter.Dex();                           // $1000: CA
        emitter.Bne(loop);                       // $1001: D0 FD  (target $1000, next $1003)

        Assert.Equal(new byte[] { 0xCA, 0xD0, 0xFD }, emitter.Assemble());
    }

    [Fact]
    public void Measures_a_forward_branch_from_the_following_instruction()
    {
        var emitter = new Emitter6502(0x1000);
        var skip = emitter.NewLabel("skip");
        emitter.Beq(skip);              // $1000: F0 01  (next $1002, target $1003)
        emitter.Rts();                  // $1002: 60
        emitter.Define(skip);           // $1003
        emitter.Clc();

        Assert.Equal(new byte[] { 0xF0, 0x01, 0x60, 0x18 }, emitter.Assemble());
    }

    /// <summary>The assembly this is ported from reaches for a long-branch macro when a branch
    /// will not fit, so an out-of-range branch means the port dropped one - which has to be an
    /// error, not a wrapped-around offset that jumps somewhere arbitrary.</summary>
    [Fact]
    public void Refuses_a_branch_that_cannot_reach()
    {
        var emitter = new Emitter6502(0x1000);
        var far = emitter.NewLabel("far");
        emitter.Bne(far);
        for (int i = 0; i < 130; i++)
            emitter.Clc();
        emitter.Define(far);

        var exception = Assert.Throws<InvalidOperationException>(() => emitter.Assemble());
        Assert.Contains("far", exception.Message);
        Assert.Contains("long-branch", exception.Message);
    }

    [Fact]
    public void Refuses_a_label_that_was_never_defined()
    {
        var emitter = new Emitter6502(0x1000);
        emitter.Jmp(emitter.NewLabel("nowhere"));

        var exception = Assert.Throws<InvalidOperationException>(() => emitter.Assemble());
        Assert.Contains("nowhere", exception.Message);
    }

    [Fact]
    public void Refuses_to_define_a_label_twice()
    {
        var emitter = new Emitter6502(0x1000);
        var label = emitter.DefineHere("once");
        emitter.Clc();

        Assert.Throws<InvalidOperationException>(() => emitter.Define(label));
    }

    /// <summary>The boundary that decides instruction length, and the reason the player's
    /// zero-page migration saved bytes at all.</summary>
    [Theory]
    [InlineData(0x00, new byte[] { 0xB5, 0x00 })]
    [InlineData(0x48, new byte[] { 0xB5, 0x48 })]
    [InlineData(0xFF, new byte[] { 0xB5, 0xFF })]
    [InlineData(0x100, new byte[] { 0xBD, 0x00, 0x01 })]
    [InlineData(0xD400, new byte[] { 0xBD, 0x00, 0xD4 })]
    public void Chooses_zero_page_by_the_operand_value(int address, byte[] expected)
    {
        var emitter = new Emitter6502(0x1000);
        emitter.Lda(MemX(address));

        Assert.Equal(expected, emitter.Assemble());
    }

    [Fact]
    public void Pc_tracks_what_has_been_emitted()
    {
        var emitter = new Emitter6502(0x2000);
        Assert.Equal(0x2000, emitter.Pc);

        emitter.Lda(Imm(0));            // 2 bytes
        Assert.Equal(0x2002, emitter.Pc);

        emitter.Sta(Mem(0xD418));       // 3 bytes
        Assert.Equal(0x2005, emitter.Pc);

        emitter.Rts();                  // 1 byte
        Assert.Equal(0x2006, emitter.Pc);
    }

    [Fact]
    public void AssemblePrg_puts_the_load_address_in_front()
    {
        var emitter = new Emitter6502(0x4400);
        emitter.Rts();

        Assert.Equal(new byte[] { 0x00, 0x44, 0x60 }, emitter.AssemblePrg());
    }

    [Fact]
    public void Assembling_twice_gives_the_same_image()
    {
        var emitter = new Emitter6502(0x1000);
        var loop = emitter.DefineHere("loop");
        emitter.Dex();
        emitter.Bne(loop);

        Assert.Equal(emitter.Assemble(), emitter.Assemble());
    }

    /// <summary>The property the whole relocatable export rests on: the same program at another
    /// origin is the same bytes except where an address is actually stored. Proven for the real
    /// player by comparing SID writes; pinned here for the emitter itself.</summary>
    [Fact]
    public void Relocating_changes_only_the_addresses()
    {
        static byte[] Build(int origin)
        {
            var emitter = new Emitter6502(origin);
            var loop = emitter.NewLabel("loop");
            var table = emitter.NewLabel("table");

            emitter.Ldx(Imm(2));
            emitter.Define(loop);
            emitter.Lda(MemX(table));       // absolute - moves with the origin
            emitter.Sta(MemX(0x20));        // zero page - does not
            emitter.Dex();
            emitter.Bpl(loop);              // relative - does not
            emitter.Rts();
            emitter.Define(table);
            emitter.Bytes(0x11, 0x22, 0x33);

            return emitter.Assemble();
        }

        byte[] low = Build(0x1000);
        byte[] high = Build(0x4400);

        Assert.Equal(low.Length, high.Length);
        var differing = Enumerable.Range(0, low.Length).Where(i => low[i] != high[i]).ToArray();

        // Only the high byte of the one absolute operand: $10 against $44. The low byte of the
        // table address is the same in both because the offset within the image is identical.
        Assert.Equal(new[] { 4 }, differing);
        Assert.Equal(0x10, low[4]);
        Assert.Equal(0x44, high[4]);
    }

    /// <summary>A small routine checked end to end against c6510, so label arithmetic and operand
    /// widths are confirmed together rather than one instruction at a time.</summary>
    [Fact]
    public void A_whole_routine_matches_c6510()
    {
        const int origin = 0x1000;

        var emitter = new Emitter6502(origin);
        var loop = emitter.NewLabel("loop");
        var done = emitter.NewLabel("done");
        var table = emitter.NewLabel("table");

        emitter.Ldx(Imm(0x02));
        emitter.Define(loop);
        emitter.Lda(MemX(table));
        emitter.Beq(done);
        emitter.Sta(MemX(0x20));
        emitter.Isc(MemX(0x48));
        emitter.Dex();
        emitter.Bpl(loop);
        emitter.Define(done);
        emitter.Sta(Mem(0xD418));
        emitter.Rts();
        emitter.Define(table);
        emitter.Bytes(0x11, 0x22, 0x33);

        byte[] reference = C6510.Assemble("""
            	include "6510.s"
            	include "6510illegal.s"
            	@ = $1000
            	ldx #$02
            loop:
            	lda table,x
            	beq done
            	sta $20,x
            	isc $48,x
            	dex
            	bpl loop
            done:
            	sta $d418
            	rts
            table:
            	byte $11, $22, $33
            """, origin);

        Assert.Equal(reference, emitter.Assemble());
    }
}
