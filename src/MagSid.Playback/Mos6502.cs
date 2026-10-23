namespace MagSid.Playback;

/// <summary>
/// A 6502 interpreter, enough of one to run a SID tune's init and play routines.
///
/// This exists so the editor can play an exported <c>.sid</c> by actually executing its code, rather
/// than by trusting the C# port of the player. That distinction is the whole point: SongSequencer
/// reproduces what musicplayer.s <em>means</em>, while this runs the bytes that were written to the
/// file. The two agreeing is evidence; one of them alone is not.
///
/// Scope is the documented instruction set and the undocumented opcodes an NMOS 6510 runs - the
/// stable ones exactly (SLO, RLA, SRE, RRA, SAX, LAX, DCP, ISC, ANC, ALR, ARR, SBX, the extra SBC and
/// NOPs), the unstable ones (SHA, SHX, SHY, TAS, ANE, LXA, LAS) as a C64 usually behaves, the way
/// VICE takes them. Music players reach for them to save a byte or a cycle; LFT's Lunatico uses SBX.
/// Only the JAM opcodes, which halt the CPU, throw - by name, rather than being quietly treated as a
/// NOP: a SID player that silently mis-executes produces a tune that sounds plausible and wrong,
/// which is the worst outcome available here.
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

            // --- undocumented: read-modify-write, then an operation on A ---
            // ISC: increment memory, then subtract the result from A - this project's own player
            // uses it in place of an inc + cmp pair.
            case 0xE7: Isc(ZeroPage()); break;
            case 0xF7: Isc(ZeroPageX()); break;
            case 0xEF: Isc(Absolute()); break;
            case 0xFF: Isc(AbsoluteX()); break;
            case 0xFB: Isc(AbsoluteY()); break;
            case 0xE3: Isc(IndirectX()); break;
            case 0xF3: Isc(IndirectY()); break;

            // SLO: shift left, then OR into A.
            case 0x07: Slo(ZeroPage()); break;
            case 0x17: Slo(ZeroPageX()); break;
            case 0x0F: Slo(Absolute()); break;
            case 0x1F: Slo(AbsoluteX()); break;
            case 0x1B: Slo(AbsoluteY()); break;
            case 0x03: Slo(IndirectX()); break;
            case 0x13: Slo(IndirectY()); break;

            // RLA: rotate left, then AND into A.
            case 0x27: Rla(ZeroPage()); break;
            case 0x37: Rla(ZeroPageX()); break;
            case 0x2F: Rla(Absolute()); break;
            case 0x3F: Rla(AbsoluteX()); break;
            case 0x3B: Rla(AbsoluteY()); break;
            case 0x23: Rla(IndirectX()); break;
            case 0x33: Rla(IndirectY()); break;

            // SRE: shift right, then EOR into A.
            case 0x47: Sre(ZeroPage()); break;
            case 0x57: Sre(ZeroPageX()); break;
            case 0x4F: Sre(Absolute()); break;
            case 0x5F: Sre(AbsoluteX()); break;
            case 0x5B: Sre(AbsoluteY()); break;
            case 0x43: Sre(IndirectX()); break;
            case 0x53: Sre(IndirectY()); break;

            // RRA: rotate right, then add to A.
            case 0x67: Rra(ZeroPage()); break;
            case 0x77: Rra(ZeroPageX()); break;
            case 0x6F: Rra(Absolute()); break;
            case 0x7F: Rra(AbsoluteX()); break;
            case 0x7B: Rra(AbsoluteY()); break;
            case 0x63: Rra(IndirectX()); break;
            case 0x73: Rra(IndirectY()); break;

            // DCP: decrement, then compare with A.
            case 0xC7: Dcp(ZeroPage()); break;
            case 0xD7: Dcp(ZeroPageX()); break;
            case 0xCF: Dcp(Absolute()); break;
            case 0xDF: Dcp(AbsoluteX()); break;
            case 0xDB: Dcp(AbsoluteY()); break;
            case 0xC3: Dcp(IndirectX()); break;
            case 0xD3: Dcp(IndirectY()); break;

            // --- undocumented: loads and stores ---
            // LAX: load A and X together. SAX: store A AND X.
            case 0xA7: Lax(Read(ZeroPage())); break;
            case 0xB7: Lax(Read(ZeroPageY())); break;
            case 0xAF: Lax(Read(Absolute())); break;
            case 0xBF: Lax(Read(AbsoluteY())); break;
            case 0xA3: Lax(Read(IndirectX())); break;
            case 0xB3: Lax(Read(IndirectY())); break;

            case 0x87: Write(ZeroPage(), A & X); break;
            case 0x97: Write(ZeroPageY(), A & X); break;
            case 0x8F: Write(Absolute(), A & X); break;
            case 0x83: Write(IndirectX(), A & X); break;

            // --- undocumented: immediate operations ---
            case 0x0B: case 0x2B: A &= Immediate(); SetZn(A); Carry = Negative; break;    // ANC
            case 0x4B: A = Lsr(A & Immediate()); break;                                      // ALR
            case 0x6B: Arr(Immediate()); break;                                              // ARR
            case 0xCB: Sbx(Immediate()); break;                                              // SBX (AXS)
            case 0xEB: Sbc(Immediate()); break;                                              // SBC, again

            // --- undocumented, unstable: as a C64 usually behaves. ANE and LXA's "magic constant" is
            // chip and temperature dependent; $EE is the common one, and the safe uses (immediate 0,
            // or A = $FF) do not depend on it. See "No More Secrets" (groepaz, 2025). ---
            case 0x8B: A = (A | 0xEE) & X & Immediate(); SetZn(A); break;                   // ANE
            case 0xAB: Lax((A | 0xEE) & Immediate()); break;                                 // LXA
            case 0xBB: Las(Read(AbsoluteY())); break;                                        // LAS
            case 0x9F: StoreHigh(FetchWord(), Y, A & X); break;                              // SHA abs,y
            case 0x93: StoreHigh(IndirectBase(), Y, A & X); break;                           // SHA (zp),y
            case 0x9E: StoreHigh(FetchWord(), Y, X); break;                                  // SHX
            case 0x9C: StoreHigh(FetchWord(), X, Y); break;                                  // SHY
            case 0x9B: SP = A & X; StoreHigh(FetchWord(), Y, SP); break;                     // TAS

            // --- undocumented NOPs: they still take their operand bytes ---
            case 0x1A: case 0x3A: case 0x5A: case 0x7A: case 0xDA: case 0xFA: break;
            case 0x80: case 0x82: case 0x89: case 0xC2: case 0xE2: Fetch(); break;
            case 0x04: case 0x44: case 0x64: Fetch(); break;
            case 0x14: case 0x34: case 0x54: case 0x74: case 0xD4: case 0xF4: Fetch(); break;
            case 0x0C: FetchWord(); break;
            case 0x1C: case 0x3C: case 0x5C: case 0x7C: case 0xDC: case 0xFC: FetchWord(); break;

            // JAM: the CPU halts and only a reset brings it back.
            case 0x02: case 0x12: case 0x22: case 0x32: case 0x42: case 0x52:
            case 0x62: case 0x72: case 0x92: case 0xB2: case 0xD2: case 0xF2:
                throw new SidPlaybackException(
                    $"Opcode ${opcode:X2} at ${PC - 1:X4} halts the 6510 (JAM) - the tune has run into " +
                    "something that is not code.");

            default:
                throw new SidPlaybackException($"Opcode ${opcode:X2} at ${PC - 1:X4} is not implemented.");
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

    private void Slo(int address)
    {
        int value = Asl(Read(address));
        Write(address, value);
        A |= value;
        SetZn(A);
    }

    private void Rla(int address)
    {
        int value = Rol(Read(address));
        Write(address, value);
        A &= value;
        SetZn(A);
    }

    private void Sre(int address)
    {
        int value = Lsr(Read(address));
        Write(address, value);
        A ^= value;
        SetZn(A);
    }

    private void Rra(int address)
    {
        int value = Ror(Read(address));
        Write(address, value);
        Adc(value);
    }

    private void Dcp(int address)
    {
        int value = (Read(address) - 1) & 0xFF;
        Write(address, value);
        Compare(A, value);
    }

    private void Lax(int value)
    {
        A = X = value & 0xFF;
        SetZn(A);
    }

    private void Las(int value)
    {
        A = X = SP = value & SP;
        SetZn(A);
    }

    /// <summary>SBX: X = (A AND X) - operand, without borrow; the carry as a compare sets it.</summary>
    private void Sbx(int value)
    {
        int and = A & X;
        Carry = and >= value;
        X = (and - value) & 0xFF;
        SetZn(X);
    }

    /// <summary>
    /// ARR: AND, then rotate right. In binary mode the carry is bit 6 of the result and the overflow
    /// bit 6 xor bit 5. In decimal mode N is the carry going in, V whether bit 6 changed, and each
    /// nibble gets a BCD fixup - the high one setting the carry - as "No More Secrets" gives it.
    /// </summary>
    private void Arr(int value)
    {
        int and = A & value;
        int rotated = ((and >> 1) | (Carry ? 0x80 : 0)) & 0xFF;

        if (!Decimal)
        {
            A = rotated;
            SetZn(A);
            Carry = (A & 0x40) != 0;
            Overflow = (((A >> 6) ^ (A >> 5)) & 1) != 0;
            return;
        }

        Negative = Carry;
        Zero = rotated == 0;
        Overflow = ((rotated ^ and) & 0x40) != 0;
        if ((and & 0x0F) + (and & 0x01) > 0x05)
            rotated = (rotated & 0xF0) | ((rotated + 0x06) & 0x0F);
        if ((and & 0xF0) + (and & 0x10) > 0x50)
        {
            rotated = (rotated & 0x0F) | ((rotated + 0x60) & 0xF0);
            Carry = true;
        }
        else
        {
            Carry = false;
        }

        A = rotated & 0xFF;
    }

    /// <summary>SHA, SHX, SHY, TAS: store a value ANDed with the high byte of the base address plus
    /// one - and if indexing crossed a page, that value is the high byte the store goes to.</summary>
    private void StoreHigh(int baseAddress, int index, int value)
    {
        int address = (baseAddress + index) & 0xFFFF;
        int stored = value & ((baseAddress >> 8) + 1) & 0xFF;
        if ((baseAddress & 0xFF00) != (address & 0xFF00))
            address = (stored << 8) | (address & 0xFF);
        Write(address, stored);
    }

    /// <summary>The (zp) pointer an (indirect),Y store adds Y to, without adding it.</summary>
    private int IndirectBase()
    {
        int pointer = Fetch();
        return _memory[pointer] | (_memory[(pointer + 1) & 0xFF] << 8);
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
