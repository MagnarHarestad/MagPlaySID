namespace MagSid.Assembler;

/// <summary>Where an instruction gets its operand. Kept as explicit factory calls rather than
/// overloads on the instruction methods so a port reads like the assembly it came from -
/// <c>Lda(AbsX(WtData))</c> against <c>lda wt_data,x</c> - and so the addressing mode can never be
/// inferred wrongly from an integer.</summary>
public enum AddressMode
{
    Implied,
    Accumulator,
    Immediate,
    ZeroPage,
    ZeroPageX,
    ZeroPageY,
    Absolute,
    AbsoluteX,
    AbsoluteY,
    IndirectY,
    Relative,
}

public readonly struct Operand
{
    public AddressMode Mode { get; }

    /// <summary>The literal value, for everything that is not a label reference.</summary>
    public int Value { get; }

    /// <summary>Set instead of <see cref="Value"/> when the target is not known yet.</summary>
    public Label? Target { get; }

    private Operand(AddressMode mode, int value, Label? target = null)
    {
        Mode = mode;
        Value = value;
        Target = target;
    }

    public static readonly Operand None = new(AddressMode.Implied, 0);
    public static readonly Operand A = new(AddressMode.Accumulator, 0);

    public static Operand Imm(int value) => new(AddressMode.Immediate, value & 0xFF);

    /// <summary>
    /// Picks zero page on its own when the address fits in one byte. That is not a nicety: after
    /// the player's per-channel state moved into zero page, <c>lda voice_note,x</c> has to assemble
    /// as $B5 and not $BD, and getting it wrong changes both the byte count and every address
    /// after it.
    ///
    /// A <see cref="Label"/> is always absolute - every label in this player is code or a song
    /// table at $1000 or above, and its address is not known until the end, so there would be
    /// nothing to decide from anyway.
    /// </summary>
    public static Operand Mem(int address) =>
        new(address < 0x100 ? AddressMode.ZeroPage : AddressMode.Absolute, address);

    public static Operand MemX(int address) =>
        new(address < 0x100 ? AddressMode.ZeroPageX : AddressMode.AbsoluteX, address);

    public static Operand MemY(int address) =>
        new(address < 0x100 ? AddressMode.ZeroPageY : AddressMode.AbsoluteY, address);

    public static Operand Mem(Label target) => new(AddressMode.Absolute, 0, target);
    public static Operand MemX(Label target) => new(AddressMode.AbsoluteX, 0, target);
    public static Operand MemY(Label target) => new(AddressMode.AbsoluteY, 0, target);

    /// <summary>(zp),y - the pointer is always a zero-page pair.</summary>
    public static Operand IndY(int zeroPageAddress) =>
        new(AddressMode.IndirectY, zeroPageAddress);

    public static Operand Rel(Label target) => new(AddressMode.Relative, 0, target);

    public int Width => Mode switch
    {
        AddressMode.Implied or AddressMode.Accumulator => 0,
        AddressMode.Absolute or AddressMode.AbsoluteX or AddressMode.AbsoluteY => 2,
        _ => 1,
    };
}

/// <summary>A branch target or data address that may be referenced before it is defined. The
/// emitter records a fixup for every use and patches them once everything has an address.</summary>
public sealed class Label(string name)
{
    public string Name { get; } = name;

    /// <summary>Null until <see cref="Emitter6502.Define"/> places it.</summary>
    public int? Address { get; internal set; }

    public override string ToString() =>
        Address is { } a ? $"{Name} (${a:X4})" : $"{Name} (undefined)";
}
