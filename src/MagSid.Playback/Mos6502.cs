namespace MagSid.Playback;

/// <summary>
/// A 6502 interpreter, enough of one to run a SID tune's init and play routines.
///
/// This exists so the editor can play an exported <c>.sid</c> by actually executing its code, rather
/// than by trusting the C# port of the player. That distinction is the whole point: SongSequencer
/// reproduces what musicplayer.s <em>means</em>, while this runs the bytes that were written to the
/// file. The two agreeing is evidence; one of them alone is not.
///
/// Scope is the documented instruction set plus ISC, the one illegal opcode this project's player
/// uses. Anything else throws by name rather than being quietly treated as a NOP - a SID player that
/// silently mis-executes produces a tune that sounds plausible and wrong, which is the worst outcome
/// available here.
/// </summary>
public sealed class Mos6502
{
    /// <summary>A tune that has not returned from play() in this many instructions is not going to.
    /// Generous: a frame of a heavy player is a few thousand.</summary>
    private const int DefaultStepLimit = 2_000_000;

    private readonly byte[] _memory;
    private readonly Action<int, int>? _onWrite;

    public Mos6502(byte[] memory, Action<int, int>? onWrite = null)
    {
        if (memory.Length != 0x10000)
            throw new ArgumentException("A 6502 addresses exactly 64 KiB.", nameof(memory));

        _memory = memory;
        _onWrite = onWrite;
        SP = 0xFD;
    }

    public byte[] Memory => _memory;

    public int A { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int SP { get; set; }
    public int PC { get; set; }

    public bool Carry { get; set; }
    public bool Zero { get; set; }
    public bool Negative { get; set; }
    public bool Overflow { get; set; }
    public bool Decimal { get; set; }
    public bool InterruptDisable { get; set; }

    /// <summary>Instructions executed since construction, for diagnostics.</summary>
    public long Steps { get; private set; }

    /// <summary>
    /// Where a called routine's closing <c>rts</c> lands. $0000 because no tune's code lives there -
    /// a PSID never loads that low, and the two bytes below the stack are the CPU's own port
    /// registers. Execution is stopped on reaching it, so nothing is ever fetched from it.
    /// </summary>
    private const int ReturnTrap = 0x0000;

    /// <summary>
    /// Runs a subroutine to its <c>rts</c> and returns.
    ///
    /// A return address is pushed first and the return is detected by the program counter reaching
    /// it. The obvious alternative - push nothing and watch for the stack pointer rising above where
    /// it started - looks simpler and is wrong: each call then leaks the two bytes its rts pops, so
    /// after enough calls the pointer wraps past $FF and the comparison silently stops holding. The
    /// stack pointer is also reset here, which is what a real PSID driver effectively does by owning
    /// the stack between calls.
    /// </summary>
    public void Call(int address, int stepLimit = DefaultStepLimit)
    {
        SP = 0xFD;
        int ret = (ReturnTrap - 1) & 0xFFFF;    // rts adds one to what it pops
        Push(ret >> 8);
        Push(ret & 0xFF);
        PC = address & 0xFFFF;

        for (int i = 0; i < stepLimit; i++)
        {
            if (PC == ReturnTrap)
                return;

            Step();
        }

        throw new SidPlaybackException(
            $"The routine at ${address:X4} ran {stepLimit} instructions without returning. " +
            "Either it loops forever by design, or the tune is not a PSID-style init/play player.");
    }

    public byte Read(int address) => _memory[address & 0xFFFF];

    public void Write(int address, int value)
    {
        address &= 0xFFFF;
        value &= 0xFF;
        _memory[address] = (byte)value;
        _onWrite?.Invoke(address, value);
    }

    public void Step()
    {
        Steps++;
        byte opcode = Fetch();

        switch (opcode)
        {
            // --- load / store ---
            case 0xA9: Lda(Immediate()); break;
            case 0xA5: Lda(Read(ZeroPage())); break;
            case 0xB5: Lda(Read(ZeroPageX())); break;
            case 0xAD: Lda(Read(Absolute())); break;
            case 0xBD: Lda(Read(AbsoluteX())); break;
            case 0xB9: Lda(Read(AbsoluteY())); break;
            case 0xA1: Lda(Read(IndirectX())); break;
            case 0xB1: Lda(Read(IndirectY())); break;

            case 0xA2: Ldx(Immediate()); break;
            case 0xA6: Ldx(Read(ZeroPage())); break;
            case 0xB6: Ldx(Read(ZeroPageY())); break;
            case 0xAE: Ldx(Read(Absolute())); break;
            case 0xBE: Ldx(Read(AbsoluteY())); break;

            case 0xA0: Ldy(Immediate()); break;
            case 0xA4: Ldy(Read(ZeroPage())); break;
            case 0xB4: Ldy(Read(ZeroPageX())); break;
            case 0xAC: Ldy(Read(Absolute())); break;
            case 0xBC: Ldy(Read(AbsoluteX())); break;

            case 0x85: Write(ZeroPage(), A); break;
            case 0x95: Write(ZeroPageX(), A); break;
            case 0x8D: Write(Absolute(), A); break;
            case 0x9D: Write(AbsoluteX(), A); break;
            case 0x99: Write(AbsoluteY(), A); break;
            case 0x81: Write(IndirectX(), A); break;
            case 0x91: Write(IndirectY(), A); break;

            case 0x86: Write(ZeroPage(), X); break;
            case 0x96: Write(ZeroPageY(), X); break;
            case 0x8E: Write(Absolute(), X); break;

            case 0x84: Write(ZeroPage(), Y); break;
            case 0x94: Write(ZeroPageX(), Y); break;
            case 0x8C: Write(Absolute(), Y); break;

            // --- transfers ---
            case 0xAA: X = A; SetZn(X); break;
            case 0xA8: Y = A; SetZn(Y); break;
            case 0x8A: A = X; SetZn(A); break;
            case 0x98: A = Y; SetZn(A); break;
            case 0xBA: X = SP; SetZn(X); break;
            case 0x9A: SP = A; break;

            // --- stack ---
            case 0x48: Push(A); break;
            case 0x68: A = Pop(); SetZn(A); break;
            // PHP pushes with bit 4 (B) and bit 5 set, which is what RTI/PLP expect to see back.
            case 0x08: Push(StatusByte() | 0x10); break;
            case 0x28: SetStatus(Pop()); break;

            // --- logic ---
            case 0x29: A &= Immediate(); SetZn(A); break;
            case 0x25: A &= Read(ZeroPage()); SetZn(A); break;
            case 0x35: A &= Read(ZeroPageX()); SetZn(A); break;
            case 0x2D: A &= Read(Absolute()); SetZn(A); break;
            case 0x3D: A &= Read(AbsoluteX()); SetZn(A); break;
            case 0x39: A &= Read(AbsoluteY()); SetZn(A); break;
            case 0x21: A &= Read(IndirectX()); SetZn(A); break;
            case 0x31: A &= Read(IndirectY()); SetZn(A); break;

            case 0x09: A |= Immediate(); SetZn(A); break;
            case 0x05: A |= Read(ZeroPage()); SetZn(A); break;
            case 0x15: A |= Read(ZeroPageX()); SetZn(A); break;
            case 0x0D: A |= Read(Absolute()); SetZn(A); break;
            case 0x1D: A |= Read(AbsoluteX()); SetZn(A); break;
            case 0x19: A |= Read(AbsoluteY()); SetZn(A); break;
            case 0x01: A |= Read(IndirectX()); SetZn(A); break;
            case 0x11: A |= Read(IndirectY()); SetZn(A); break;

            case 0x49: A ^= Immediate(); SetZn(A); break;
            case 0x45: A ^= Read(ZeroPage()); SetZn(A); break;
            case 0x55: A ^= Read(ZeroPageX()); SetZn(A); break;
            case 0x4D: A ^= Read(Absolute()); SetZn(A); break;
            case 0x5D: A ^= Read(AbsoluteX()); SetZn(A); break;
            case 0x59: A ^= Read(AbsoluteY()); SetZn(A); break;
            case 0x41: A ^= Read(IndirectX()); SetZn(A); break;
            case 0x51: A ^= Read(IndirectY()); SetZn(A); break;

            case 0x24: Bit(Read(ZeroPage())); break;
            case 0x2C: Bit(Read(Absolute())); break;

            // --- arithmetic ---
            case 0x69: Adc(Immediate()); break;
            case 0x65: Adc(Read(ZeroPage())); break;
            case 0x75: Adc(Read(ZeroPageX())); break;
            case 0x6D: Adc(Read(Absolute())); break;
            case 0x7D: Adc(Read(AbsoluteX())); break;
            case 0x79: Adc(Read(AbsoluteY())); break;
            case 0x61: Adc(Read(IndirectX())); break;
            case 0x71: Adc(Read(IndirectY())); break;

            case 0xE9: Sbc(Immediate()); break;
            case 0xE5: Sbc(Read(ZeroPage())); break;
            case 0xF5: Sbc(Read(ZeroPageX())); break;
            case 0xED: Sbc(Read(Absolute())); break;
            case 0xFD: Sbc(Read(AbsoluteX())); break;
            case 0xF9: Sbc(Read(AbsoluteY())); break;
            case 0xE1: Sbc(Read(IndirectX())); break;
            case 0xF1: Sbc(Read(IndirectY())); break;

            case 0xC9: Compare(A, Immediate()); break;
            case 0xC5: Compare(A, Read(ZeroPage())); break;
            case 0xD5: Compare(A, Read(ZeroPageX())); break;
            case 0xCD: Compare(A, Read(Absolute())); break;
            case 0xDD: Compare(A, Read(AbsoluteX())); break;
            case 0xD9: Compare(A, Read(AbsoluteY())); break;
            case 0xC1: Compare(A, Read(IndirectX())); break;
            case 0xD1: Compare(A, Read(IndirectY())); break;

            case 0xE0: Compare(X, Immediate()); break;
            case 0xE4: Compare(X, Read(ZeroPage())); break;
            case 0xEC: Compare(X, Read(Absolute())); break;

            case 0xC0: Compare(Y, Immediate()); break;
            case 0xC4: Compare(Y, Read(ZeroPage())); break;
            case 0xCC: Compare(Y, Read(Absolute())); break;

            // --- increment / decrement ---
            case 0xE6: Modify(ZeroPage(), v => v + 1); break;
            case 0xF6: Modify(ZeroPageX(), v => v + 1); break;
            case 0xEE: Modify(Absolute(), v => v + 1); break;
            case 0xFE: Modify(AbsoluteX(), v => v + 1); break;

            case 0xC6: Modify(ZeroPage(), v => v - 1); break;
            case 0xD6: Modify(ZeroPageX(), v => v - 1); break;
            case 0xCE: Modify(Absolute(), v => v - 1); break;
            case 0xDE: Modify(AbsoluteX(), v => v - 1); break;

            case 0xE8: X = (X + 1) & 0xFF; SetZn(X); break;
            case 0xCA: X = (X - 1) & 0xFF; SetZn(X); break;
            case 0xC8: Y = (Y + 1) & 0xFF; SetZn(Y); break;
            case 0x88: Y = (Y - 1) & 0xFF; SetZn(Y); break;

            // --- shifts and rotates ---
            case 0x0A: A = Asl(A); break;
            case 0x06: Modify(ZeroPage(), Asl); break;
            case 0x16: Modify(ZeroPageX(), Asl); break;
            case 0x0E: Modify(Absolute(), Asl); break;
            case 0x1E: Modify(AbsoluteX(), Asl); break;

            case 0x4A: A = Lsr(A); break;
            case 0x46: Modify(ZeroPage(), Lsr); break;
            case 0x56: Modify(ZeroPageX(), Lsr); break;
            case 0x4E: Modify(Absolute(), Lsr); break;
            case 0x5E: Modify(AbsoluteX(), Lsr); break;

            case 0x2A: A = Rol(A); break;
            case 0x26: Modify(ZeroPage(), Rol); break;
            case 0x36: Modify(ZeroPageX(), Rol); break;
            case 0x2E: Modify(Absolute(), Rol); break;
            case 0x3E: Modify(AbsoluteX(), Rol); break;

            case 0x6A: A = Ror(A); break;
            case 0x66: Modify(ZeroPage(), Ror); break;
            case 0x76: Modify(ZeroPageX(), Ror); break;
            case 0x6E: Modify(Absolute(), Ror); break;
            case 0x7E: Modify(AbsoluteX(), Ror); break;

            // --- jumps ---
            case 0x4C: PC = Absolute(); break;
            case 0x6C: PC = IndirectJump(); break;
            case 0x20: Jsr(); break;
            case 0x60: PC = (Pop() | (Pop() << 8)) + 1 & 0xFFFF; break;
            case 0x40: SetStatus(Pop()); PC = Pop() | (Pop() << 8); break;

            // --- branches ---
            case 0x10: Branch(!Negative); break;
            case 0x30: Branch(Negative); break;
            case 0x50: Branch(!Overflow); break;
            case 0x70: Branch(Overflow); break;
            case 0x90: Branch(!Carry); break;
            case 0xB0: Branch(Carry); break;
            case 0xD0: Branch(!Zero); break;
            case 0xF0: Branch(Zero); break;

            // --- flags ---
            case 0x18: Carry = false; break;
            case 0x38: Carry = true; break;
            case 0x58: InterruptDisable = false; break;
            case 0x78: InterruptDisable = true; break;
            case 0xD8: Decimal = false; break;
            case 0xF8: Decimal = true; break;
            case 0xB8: Overflow = false; break;

            case 0xEA: break;   // NOP

            // --- the one illegal opcode this project's player uses ---
            // ISC: increment memory, then subtract the result from A. Replaces an inc + cmp pair.
            case 0xE7: Isc(ZeroPage()); break;
            case 0xF7: Isc(ZeroPageX()); break;
            case 0xEF: Isc(Absolute()); break;
            case 0xFF: Isc(AbsoluteX()); break;
            case 0xFB: Isc(AbsoluteY()); break;
            case 0xE3: Isc(IndirectX()); break;
            case 0xF3: Isc(IndirectY()); break;

            default:
                throw new SidPlaybackException(
                    $"Opcode ${opcode:X2} at ${PC - 1:X4} is not implemented. This player covers the " +
                    "documented instruction set plus ISC; a tune using other undocumented opcodes " +
                    "needs them added rather than guessed at.");
        }
    }

    // --- addressing modes ---

    private byte Fetch() => _memory[PC++ & 0xFFFF];

    private int FetchWord()
    {
        int low = Fetch();
        return low | (Fetch() << 8);
    }

    private byte Immediate() => Fetch();

    private int ZeroPage() => Fetch();

    private int ZeroPageX() => (Fetch() + X) & 0xFF;

    private int ZeroPageY() => (Fetch() + Y) & 0xFF;

    private int Absolute() => FetchWord();

    private int AbsoluteX() => (FetchWord() + X) & 0xFFFF;

    private int AbsoluteY() => (FetchWord() + Y) & 0xFFFF;

    /// <summary>(zp,x) - the pointer is read from zero page and the index is applied before the
    /// read, wrapping inside the page.</summary>
    private int IndirectX()
    {
        int pointer = (Fetch() + X) & 0xFF;
        return Read(pointer) | (Read((pointer + 1) & 0xFF) << 8);
    }

    /// <summary>(zp),y - the pointer wraps inside zero page, the index does not.</summary>
    private int IndirectY()
    {
        int pointer = Fetch();
        int address = Read(pointer) | (Read((pointer + 1) & 0xFF) << 8);
        return (address + Y) & 0xFFFF;
    }

    /// <summary>JMP (abs), including the page-boundary bug: the high byte is fetched from the same
    /// page as the low one, so <c>jmp ($10FF)</c> reads $10FF and $1000. Real code relies on
    /// this.</summary>
    private int IndirectJump()
    {
        int pointer = FetchWord();
        int high = (pointer & 0xFF00) | ((pointer + 1) & 0x00FF);
        return Read(pointer) | (Read(high) << 8);
    }

    // --- operations ---

    private void Lda(int value)
    {
        A = value & 0xFF;
        SetZn(A);
    }

    private void Ldx(int value)
    {
        X = value & 0xFF;
        SetZn(X);
    }

    private void Ldy(int value)
    {
        Y = value & 0xFF;
        SetZn(Y);
    }

    private void Modify(int address, Func<int, int> operation)
    {
        int result = operation(Read(address)) & 0xFF;
        Write(address, result);
        SetZn(result);
    }

    private int Asl(int value)
    {
        Carry = (value & 0x80) != 0;
        int result = (value << 1) & 0xFF;
        SetZn(result);
        return result;
    }

    private int Lsr(int value)
    {
        Carry = (value & 0x01) != 0;
        int result = (value >> 1) & 0xFF;
        SetZn(result);
        return result;
    }

    private int Rol(int value)
    {
        int result = ((value << 1) | (Carry ? 1 : 0)) & 0xFF;
        Carry = (value & 0x80) != 0;
        SetZn(result);
        return result;
    }

    private int Ror(int value)
    {
        int result = ((value >> 1) | (Carry ? 0x80 : 0)) & 0xFF;
        Carry = (value & 0x01) != 0;
        SetZn(result);
        return result;
    }

    /// <summary>BIT sets N and V from the memory operand's own top two bits, not from the result -
    /// the only instruction that does.</summary>
    private void Bit(int value)
    {
        Zero = (A & value) == 0;
        Negative = (value & 0x80) != 0;
        Overflow = (value & 0x40) != 0;
    }

    private void Adc(int value)
    {
        int carryIn = Carry ? 1 : 0;

        if (Decimal)
        {
            // BCD. Rarely used by SID players, but a player that does use it would otherwise drift
            // silently, so it is here rather than approximated.
            int low = (A & 0x0F) + (value & 0x0F) + carryIn;
            int high = (A >> 4) + (value >> 4);
            if (low > 9)
            {
                low -= 10;
                high++;
            }

            Zero = ((A + value + carryIn) & 0xFF) == 0;
            Negative = ((high << 4) & 0x80) != 0;
            Overflow = ((A ^ value) & 0x80) == 0 && ((A ^ (high << 4)) & 0x80) != 0;
            Carry = high > 9;
            if (Carry)
                high -= 10;

            A = ((high << 4) | (low & 0x0F)) & 0xFF;
            return;
        }

        int sum = A + value + carryIn;
        Carry = sum > 0xFF;
        Overflow = ((A ^ sum) & (value ^ sum) & 0x80) != 0;
        A = sum & 0xFF;
        SetZn(A);
    }

    private void Sbc(int value)
    {
        int carryIn = Carry ? 1 : 0;

        if (Decimal)
        {
            int low = (A & 0x0F) - (value & 0x0F) - (1 - carryIn);
            int high = (A >> 4) - (value >> 4);
            if ((low & 0x10) != 0)
            {
                low -= 6;
                high--;
            }

            if ((high & 0x10) != 0)
                high -= 6;

            int binary = A - value - (1 - carryIn);
            Carry = (binary & 0x100) == 0;
            Overflow = ((A ^ binary) & (~value ^ binary) & 0x80) != 0;
            Zero = (binary & 0xFF) == 0;
            Negative = (binary & 0x80) != 0;
            A = ((high << 4) | (low & 0x0F)) & 0xFF;
            return;
        }

        int result = A - value - (1 - carryIn);
        Carry = (result & 0x100) == 0;
        Overflow = ((A ^ result) & (~value ^ result) & 0x80) != 0;
        A = result & 0xFF;
        SetZn(A);
    }

    private void Compare(int register, int value)
    {
        int result = register - value;
        Carry = result >= 0;
        SetZn(result & 0xFF);
    }

    /// <summary>ISC, the illegal one: increment memory, then subtract the incremented value from A.
    /// The carry flag on entry is part of the subtraction, which is why musicplayer.s takes care to
    /// reach it only through a path that leaves carry set.</summary>
    private void Isc(int address)
    {
        int value = (Read(address) + 1) & 0xFF;
        Write(address, value);
        Sbc(value);
    }

    private void Jsr()
    {
        int target = FetchWord();
        int ret = (PC - 1) & 0xFFFF;
        Push(ret >> 8);
        Push(ret & 0xFF);
        PC = target;
    }

    private void Branch(bool taken)
    {
        sbyte offset = (sbyte)Fetch();
        if (taken)
            PC = (PC + offset) & 0xFFFF;
    }

    // --- state helpers ---

    private void SetZn(int value)
    {
        Zero = (value & 0xFF) == 0;
        Negative = (value & 0x80) != 0;
    }

    private void Push(int value)
    {
        _memory[0x0100 + (SP & 0xFF)] = (byte)value;
        SP = (SP - 1) & 0xFF;
    }

    private byte Pop()
    {
        SP = (SP + 1) & 0xFF;
        return _memory[0x0100 + (SP & 0xFF)];
    }

    private int StatusByte() =>
        (Carry ? 0x01 : 0) | (Zero ? 0x02 : 0) | (InterruptDisable ? 0x04 : 0) |
        (Decimal ? 0x08 : 0) | 0x20 | (Overflow ? 0x40 : 0) | (Negative ? 0x80 : 0);

    private void SetStatus(int value)
    {
        Carry = (value & 0x01) != 0;
        Zero = (value & 0x02) != 0;
        InterruptDisable = (value & 0x04) != 0;
        Decimal = (value & 0x08) != 0;
        Overflow = (value & 0x40) != 0;
        Negative = (value & 0x80) != 0;
    }
}

/// <summary>Thrown when a tune cannot be loaded or run - a malformed header, an opcode this
/// interpreter does not cover, a play routine that never returns.</summary>
public sealed class SidPlaybackException(string message) : Exception(message);
