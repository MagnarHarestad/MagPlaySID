using System.Text;

namespace MagSid.Assembler.Tests;

/// <summary>
/// Checks every entry in the emitter's opcode table against what c6510 assembles for the same
/// instruction. This exists because the table is the one part of the emitter that cannot be
/// reasoned about - it is 140-odd magic numbers, and the asymmetric ones (LDX indexing by Y, STY
/// having no abs,x) are exactly where a remembered table goes wrong. A single wrong byte here
/// would produce a .sid that plays and sounds wrong, so the table is verified mechanically rather
/// than read over.
/// </summary>
public class OpcodeTableTests
{
    /// <summary>Each instruction gets its own four-byte slot, which is one more than the longest
    /// 6502 instruction. Slicing is then exact no matter what length either side produced, so a
    /// disagreement about one instruction's length cannot cascade into every later comparison.</summary>
    private const int SlotSize = 4;

    private const int Origin = 0x1000;

    private const int ZeroPageOperand = 0x34;
    private const int AbsoluteOperand = 0x1234;
    private const int ImmediateOperand = 0x12;

    [Fact]
    public void Every_opcode_matches_what_c6510_assembles()
    {
        var cases = Opcodes.All.OrderBy(c => c.Mnemonic).ThenBy(c => c.Mode).ToList();
        Assert.NotEmpty(cases);

        byte[] reference = C6510.Assemble(BuildSource(cases), Origin);
        var failures = new List<string>();

        for (int i = 0; i < cases.Count; i++)
        {
            var (mnemonic, mode, _) = cases[i];
            int slot = Origin + i * SlotSize;

            var emitter = new Emitter6502(slot);
            // Relative mode needs its target to exist; branching to the instruction itself gives a
            // known -2, which also checks the offset is measured from the following instruction.
            var self = emitter.DefineHere("self");
            emitter.Emit(mnemonic, OperandFor(mode, self));
            byte[] mine = emitter.Assemble();

            byte[] theirs = reference[(i * SlotSize)..(i * SlotSize + mine.Length)];
            if (!mine.SequenceEqual(theirs))
            {
                failures.Add($"{mnemonic} {mode}: emitter {Hex(mine)}, c6510 {Hex(theirs)}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} of {cases.Count} opcodes disagree with c6510:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>The emitter must refuse a mode the 6502 does not have rather than emit some
    /// near-miss opcode - STY has no abs,x form, and a table lookup that quietly fell back to
    /// another one would be silent corruption.</summary>
    [Fact]
    public void Refuses_a_mode_the_instruction_does_not_have()
    {
        var emitter = new Emitter6502(Origin);
        var exception = Assert.Throws<InvalidOperationException>(
            () => emitter.Sty(Operand.MemX(AbsoluteOperand)));
        Assert.Contains("STY", exception.Message);
    }

    /// <summary>JMP has no zero-page form, so a low target has to widen to absolute instead of
    /// failing. The reverse must never happen: demoting absolute to zero page would shorten an
    /// instruction and move everything after it.</summary>
    [Fact]
    public void Widens_to_absolute_when_there_is_no_zero_page_form()
    {
        var emitter = new Emitter6502(Origin);
        var target = emitter.NewLabel("low");
        emitter.Jmp(target);
        target.Address = 0x0050;

        Assert.Equal(new byte[] { 0x4C, 0x50, 0x00 }, emitter.Assemble());
    }

    private static string BuildSource(List<(string Mnemonic, AddressMode Mode, byte Opcode)> cases)
    {
        var source = new StringBuilder();

        // c6510 has no built-in mnemonics at all - "lda" is a macro from include\6510.s and "isc"
        // from 6510illegal.s, which is why a snippet without these lines fails on its first
        // instruction. Neither include emits bytes, so slot 0 still starts exactly at Origin.
        source.AppendLine("\tinclude \"6510.s\"");
        source.AppendLine("\tinclude \"6510illegal.s\"");

        for (int i = 0; i < cases.Count; i++)
        {
            var (mnemonic, mode, _) = cases[i];
            // "@ =" and not "org": org is a macro from include\utils.s, and this snippet stays
            // free of includes so it tests the assembler's own syntax and nothing else.
            source.AppendLine($"\t@ = ${Origin + i * SlotSize:X4}");
            // A label per slot, so a branch has somewhere to go that is its own address.
            source.AppendLine($"slot{i}:");
            source.AppendLine($"\t{mnemonic} {SourceOperand(mode, $"slot{i}")}".TrimEnd());
        }

        // No trailing "end": that is a macro too, and all it does is print a memory map.
        return source.ToString();
    }

    private static string SourceOperand(AddressMode mode, string label) => mode switch
    {
        AddressMode.Implied or AddressMode.Accumulator => string.Empty,
        AddressMode.Immediate => $"#${ImmediateOperand:X2}",
        AddressMode.ZeroPage => $"${ZeroPageOperand:X2}",
        AddressMode.ZeroPageX => $"${ZeroPageOperand:X2},x",
        AddressMode.ZeroPageY => $"${ZeroPageOperand:X2},y",
        AddressMode.Absolute => $"${AbsoluteOperand:X4}",
        AddressMode.AbsoluteX => $"${AbsoluteOperand:X4},x",
        AddressMode.AbsoluteY => $"${AbsoluteOperand:X4},y",
        AddressMode.IndirectY => $"(${ZeroPageOperand:X2}),y",
        AddressMode.Relative => label,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    private static Operand OperandFor(AddressMode mode, Label self) => mode switch
    {
        AddressMode.Implied => Operand.None,
        AddressMode.Accumulator => Operand.A,
        AddressMode.Immediate => Operand.Imm(ImmediateOperand),
        AddressMode.ZeroPage => Operand.Mem(ZeroPageOperand),
        AddressMode.ZeroPageX => Operand.MemX(ZeroPageOperand),
        AddressMode.ZeroPageY => Operand.MemY(ZeroPageOperand),
        AddressMode.Absolute => Operand.Mem(AbsoluteOperand),
        AddressMode.AbsoluteX => Operand.MemX(AbsoluteOperand),
        AddressMode.AbsoluteY => Operand.MemY(AbsoluteOperand),
        AddressMode.IndirectY => Operand.IndY(ZeroPageOperand),
        AddressMode.Relative => Operand.Rel(self),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    private static string Hex(IEnumerable<byte> bytes) =>
        string.Join(" ", bytes.Select(b => b.ToString("X2")));
}
