namespace MagSid.Assembler;

using static Operand;

/// <summary>
/// Emits a 6502 memory image, so the editor can build a .sid without shelling out to c6510.
///
/// Deliberately not an assembler: it reads no text and has no expression parser, because the
/// sources it would have to read are not plain 6502 - Startup\Tables.s is a Lua program and the
/// macro library is written in c6510's own dialect. What is needed instead is a way to lay down
/// the bytes that source produces, with labels resolved, and that is all this does.
///
/// Forward references are handled with fixups rather than two passes, which works here because
/// the width of every operand is known the moment it is emitted: an integer address picks zero
/// page by its value, and a <see cref="Label"/> is always absolute. So nothing that comes later
/// can change how long an earlier instruction is - the thing that forces a real assembler into
/// multiple passes.
/// </summary>
public sealed class Emitter6502(int origin)
{
    private enum FixupKind
    {
        /// <summary>Two little-endian bytes holding the target address.</summary>
        Absolute,

        /// <summary>One byte holding the signed distance from the following instruction.</summary>
        Relative,
    }

    private readonly record struct Fixup(int Offset, Label Target, FixupKind Kind);

    private readonly List<byte> _bytes = [];
    private readonly List<Fixup> _fixups = [];
    private readonly List<Label> _defined = [];

    public int Origin { get; } = origin;

    /// <summary>Every label placed so far, in the order they were placed. Exposed so a differential
    /// test can say which routine a byte mismatch falls in rather than only its offset.</summary>
    public IReadOnlyList<Label> DefinedLabels => _defined;

    /// <summary>The address the next byte will land at - the assembler's <c>@</c>.</summary>
    public int Pc => Origin + _bytes.Count;

    public Label NewLabel(string name) => new(name);

    /// <summary>Places <paramref name="label"/> at the current address. A label may be referenced
    /// before this is called; it may not be placed twice.</summary>
    public Label Define(Label label)
    {
        if (label.Address is { } existing)
        {
            throw new InvalidOperationException(
                $"Label '{label.Name}' is already defined at ${existing:X4}; tried to redefine it at ${Pc:X4}.");
        }

        label.Address = Pc;
        _defined.Add(label);
        return label;
    }

    /// <summary>Declares and places a label in one step, for the common case.</summary>
    public Label DefineHere(string name) => Define(NewLabel(name));

    // --- raw data ------------------------------------------------------------------------------

    public void Byte(int value) => _bytes.Add((byte)value);

    public void Bytes(params int[] values)
    {
        foreach (int value in values)
            Byte(value);
    }

    public void Bytes(IEnumerable<byte> values) => _bytes.AddRange(values);

    /// <summary>Little-endian, as the 6502 stores addresses.</summary>
    public void Word(int value)
    {
        Byte(value & 0xFF);
        Byte((value >> 8) & 0xFF);
    }

    public void Word(Label target)
    {
        _fixups.Add(new Fixup(_bytes.Count, target, FixupKind.Absolute));
        Byte(0);
        Byte(0);
    }

    // --- instructions --------------------------------------------------------------------------

    public void Lda(Operand operand) => Emit("lda", operand);
    public void Ldx(Operand operand) => Emit("ldx", operand);
    public void Ldy(Operand operand) => Emit("ldy", operand);
    public void Sta(Operand operand) => Emit("sta", operand);
    public void Stx(Operand operand) => Emit("stx", operand);
    public void Sty(Operand operand) => Emit("sty", operand);

    public void Adc(Operand operand) => Emit("adc", operand);
    public void And(Operand operand) => Emit("and", operand);
    public void Ora(Operand operand) => Emit("ora", operand);
    public void Eor(Operand operand) => Emit("eor", operand);
    public void Cmp(Operand operand) => Emit("cmp", operand);

    /// <summary>No operand means accumulator mode, exactly as a bare <c>asl</c> does in the
    /// source.</summary>
    public void Asl(Operand? operand = null) => Emit("asl", operand ?? A);
    public void Lsr(Operand? operand = null) => Emit("lsr", operand ?? A);

    public void Inc(Operand operand) => Emit("inc", operand);
    public void Dec(Operand operand) => Emit("dec", operand);

    /// <summary>Illegal opcode: increment memory, then subtract the result from A.</summary>
    public void Isc(Operand operand) => Emit("isc", operand);

    /// <summary>Illegal opcode ALR - "asr" in c6510: AND with the operand, then shift A right, with
    /// the flags LSR would leave.</summary>
    public void Asr(Operand operand) => Emit("asr", operand);

    public void Jmp(Label target) => Emit("jmp", Mem(target));
    public void Jsr(Label target) => Emit("jsr", Mem(target));

    public void Bpl(Label target) => Emit("bpl", Rel(target));
    public void Bmi(Label target) => Emit("bmi", Rel(target));
    public void Bcc(Label target) => Emit("bcc", Rel(target));
    public void Bcs(Label target) => Emit("bcs", Rel(target));
    public void Bne(Label target) => Emit("bne", Rel(target));
    public void Beq(Label target) => Emit("beq", Rel(target));

    public void Clc() => Emit("clc", None);
    public void Dex() => Emit("dex", None);
    public void Inx() => Emit("inx", None);
    public void Iny() => Emit("iny", None);
    public void Pha() => Emit("pha", None);
    public void Pla() => Emit("pla", None);
    public void Rts() => Emit("rts", None);
    public void Tax() => Emit("tax", None);
    public void Tay() => Emit("tay", None);
    public void Tya() => Emit("tya", None);

    // --- composed instructions -----------------------------------------------------------------
    //
    // The library macros the player leans on, spelled out here so the port can keep using the
    // names it reads with. Each is exactly the two instructions the macro expands to - see
    // include\word.s - and no cleverness beyond that.

    /// <summary><c>move8 src, dst</c> - a load followed by a store.</summary>
    public void Move8(Operand source, Operand destination)
    {
        Lda(source);
        Sta(destination);
    }

    /// <summary><c>m_setptr_x lo, hi</c> - loads a 16-bit pointer out of a pair of indexed tables
    /// into the zero-page pair the player reads patterns through.</summary>
    public void SetPtrX(Operand lowTable, Operand highTable, int zpLow, int zpHigh)
    {
        Move8(lowTable, Mem(zpLow));
        Move8(highTable, Mem(zpHigh));
    }

    // --- assembly ------------------------------------------------------------------------------

    /// <summary>Internal rather than private so OpcodeTableTests can drive one arbitrary
    /// (mnemonic, mode) pair at a time and check it against c6510.</summary>
    internal void Emit(string mnemonic, Operand operand)
    {
        AddressMode mode = operand.Mode;

        // Some mnemonics have no zero-page form in a given index mode - JMP is absolute-only, and
        // STX/STY lack one of the two indexed forms. Promoting is what a real assembler does and
        // keeps a low target from looking like an unsupported mode. Never the other way round:
        // demoting absolute to zero page would change the instruction's length.
        if (Opcodes.Find(mnemonic, mode) is null && Promote(mode) is { } wider
            && Opcodes.Find(mnemonic, wider) is not null)
        {
            mode = wider;
        }

        if (Opcodes.Find(mnemonic, mode) is not { } opcode)
        {
            throw new InvalidOperationException(
                $"{mnemonic.ToUpperInvariant()} has no {mode} form on the 6502 " +
                $"(emitting at ${Pc:X4}).");
        }

        Byte(opcode);

        switch (mode)
        {
            case AddressMode.Implied:
            case AddressMode.Accumulator:
                break;

            case AddressMode.Relative:
                _fixups.Add(new Fixup(_bytes.Count, operand.Target!, FixupKind.Relative));
                Byte(0);
                break;

            case AddressMode.Absolute:
            case AddressMode.AbsoluteX:
            case AddressMode.AbsoluteY:
                if (operand.Target is { } target)
                    Word(target);
                else
                    Word(operand.Value);
                break;

            default:
                if (operand.Value > 0xFF)
                {
                    throw new InvalidOperationException(
                        $"{mnemonic.ToUpperInvariant()} {mode} operand ${operand.Value:X4} does not " +
                        $"fit in one byte (emitting at ${Pc:X4}).");
                }

                Byte(operand.Value);
                break;
        }
    }

    private static AddressMode? Promote(AddressMode mode) => mode switch
    {
        AddressMode.ZeroPage => AddressMode.Absolute,
        AddressMode.ZeroPageX => AddressMode.AbsoluteX,
        AddressMode.ZeroPageY => AddressMode.AbsoluteY,
        _ => null,
    };

    /// <summary>Resolves every reference and returns the finished image, starting at
    /// <see cref="Origin"/>. Safe to call more than once.</summary>
    public byte[] Assemble()
    {
        byte[] image = _bytes.ToArray();

        foreach (var (offset, target, kind) in _fixups)
        {
            if (target.Address is not { } address)
            {
                throw new InvalidOperationException(
                    $"Label '{target.Name}' is referenced at ${Origin + offset:X4} but never defined.");
            }

            if (kind == FixupKind.Absolute)
            {
                image[offset] = (byte)(address & 0xFF);
                image[offset + 1] = (byte)(address >> 8);
                continue;
            }

            // A branch counts from the instruction after it, which is one byte past the offset.
            int distance = address - (Origin + offset + 1);
            if (distance is < -128 or > 127)
            {
                throw new InvalidOperationException(
                    $"Branch at ${Origin + offset - 1:X4} to '{target.Name}' (${address:X4}) is " +
                    $"{distance} bytes away; a 6502 branch reaches -128 to +127. The assembly this " +
                    "was ported from would have needed a long-branch macro here.");
            }

            image[offset] = (byte)(distance & 0xFF);
        }

        return image;
    }

    /// <summary>The image with its two-byte little-endian load address in front, the C64's .prg
    /// layout - which is what the existing export path reads back from c6510.</summary>
    public byte[] AssemblePrg()
    {
        byte[] image = Assemble();
        var prg = new byte[image.Length + 2];
        prg[0] = (byte)(Origin & 0xFF);
        prg[1] = (byte)(Origin >> 8);
        image.CopyTo(prg, 2);
        return prg;
    }
}
