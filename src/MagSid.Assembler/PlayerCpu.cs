namespace MagSid.Assembler;

/// <summary>
/// A cycle-exact NMOS 6502 for exactly the opcodes <see cref="Opcodes"/> can emit - enough to run
/// the player image and count what each call costs, nothing more. An opcode outside that table
/// stops it loudly rather than being guessed at.
///
/// Cycles are the documented ones: an indexed read that crosses a page pays one more, a taken
/// branch one more and a second if it lands on another page; stores and read-modify-writes are
/// fixed. Decimal mode is not modelled - the player never sets it.
/// </summary>
internal sealed class PlayerCpu
{
    private static readonly (string Mnemonic, AddressMode Mode)?[] Decode = BuildDecode();

    public readonly byte[] Memory = new byte[0x10000];

    private readonly Action<int, int>? _sidWrite;
    private int _a, _x, _y, _sp = 0xFF, _pc;
    private bool _c, _z, _n, _v;
    private long _cycles;

    /// <param name="sidWrite">Told of every write to $D400-$D41C, as (register, value).</param>
    public PlayerCpu(Action<int, int>? sidWrite = null) => _sidWrite = sidWrite;

    private static (string, AddressMode)?[] BuildDecode()
    {
        var decode = new (string, AddressMode)?[256];
        foreach (var (mnemonic, mode, opcode) in Opcodes.All)
            decode[opcode] = (mnemonic, mode);
        return decode;
    }

    /// <summary>What "jsr address" costs a caller, the JSR itself and the final RTS included.
    /// <paramref name="a"/> is loaded into A first when given.</summary>
    public long Call(int address, int a = -1)
    {
        const int Sentinel = 0xFFFF;
        if (a >= 0)
            _a = a & 0xFF;

        _cycles = 6;
        Push((Sentinel - 1) >> 8);
        Push((Sentinel - 1) & 0xFF);
        _pc = address;

        // Bounded, so a broken image fails rather than hangs: a whole frame is ~19700 cycles.
        long limit = 1_000_000;
        while (_pc != Sentinel)
        {
            Step();
            if (_cycles > limit)
                throw new InvalidOperationException($"call to ${address:X4} ran past {limit} cycles");
        }

        return _cycles;
    }

    private int Read(int address) => Memory[address & 0xFFFF];

    private void Write(int address, int value)
    {
        address &= 0xFFFF;
        Memory[address] = (byte)value;
        if (address is >= 0xD400 and <= 0xD41C)
            _sidWrite?.Invoke(address - 0xD400, value & 0xFF);
    }

    private void Push(int value)
    {
        Memory[0x100 + _sp] = (byte)value;
        _sp = (_sp - 1) & 0xFF;
    }

    private int Pop()
    {
        _sp = (_sp + 1) & 0xFF;
        return Memory[0x100 + _sp];
    }

    private void SetNz(int value)
    {
        _z = (value & 0xFF) == 0;
        _n = (value & 0x80) != 0;
    }

    private void Step()
    {
        int at = _pc;
        int opcode = Read(_pc++);
        var (mnemonic, mode) = Decode[opcode]
            ?? throw new InvalidOperationException($"opcode ${opcode:X2} at ${at:X4} is not one the emitter produces");

        int ea = 0;
        bool crossed = false;
        switch (mode)
        {
            case AddressMode.Immediate:
                ea = _pc++;
                break;
            case AddressMode.ZeroPage:
                ea = Read(_pc++);
                break;
            case AddressMode.ZeroPageX:
                ea = (Read(_pc++) + _x) & 0xFF;
                break;
            case AddressMode.ZeroPageY:
                ea = (Read(_pc++) + _y) & 0xFF;
                break;
            case AddressMode.Absolute:
                ea = Read(_pc) | (Read(_pc + 1) << 8);
                _pc += 2;
                break;
            case AddressMode.AbsoluteX:
            case AddressMode.AbsoluteY:
            {
                int b = Read(_pc) | (Read(_pc + 1) << 8);
                _pc += 2;
                ea = (b + (mode == AddressMode.AbsoluteX ? _x : _y)) & 0xFFFF;
                crossed = (b & 0xFF00) != (ea & 0xFF00);
                break;
            }
            case AddressMode.IndirectY:
            {
                int zp = Read(_pc++);
                int b = Read(zp) | (Read((zp + 1) & 0xFF) << 8);
                ea = (b + _y) & 0xFFFF;
                crossed = (b & 0xFF00) != (ea & 0xFF00);
                break;
            }
            case AddressMode.Relative:
            {
                int offset = (sbyte)Read(_pc++);
                ea = (_pc + offset) & 0xFFFF;
                break;
            }
        }

        bool store = mnemonic is "sta" or "stx" or "sty";
        bool rmw = mnemonic is "asl" or "lsr" or "inc" or "dec" or "isc" && mode != AddressMode.Accumulator;
        _cycles += BaseCycles(mnemonic, mode, store, rmw) + (crossed && !store && !rmw ? 1 : 0);

        switch (mnemonic)
        {
            case "lda": _a = Read(ea); SetNz(_a); break;
            case "ldx": _x = Read(ea); SetNz(_x); break;
            case "ldy": _y = Read(ea); SetNz(_y); break;
            case "sta": Write(ea, _a); break;
            case "stx": Write(ea, _x); break;
            case "sty": Write(ea, _y); break;
            case "adc": Add(Read(ea)); break;
            case "and": _a &= Read(ea); SetNz(_a); break;
            case "ora": _a |= Read(ea); SetNz(_a); break;
            case "eor": _a ^= Read(ea); SetNz(_a); break;
            case "cmp":
            {
                int r = _a - Read(ea);
                _c = r >= 0;
                SetNz(r);
                break;
            }
            case "asl":
                if (mode == AddressMode.Accumulator) { _c = (_a & 0x80) != 0; _a = (_a << 1) & 0xFF; SetNz(_a); }
                else { int v = Read(ea); _c = (v & 0x80) != 0; v = (v << 1) & 0xFF; Write(ea, v); SetNz(v); }
                break;
            case "lsr":
                if (mode == AddressMode.Accumulator) { _c = (_a & 1) != 0; _a >>= 1; SetNz(_a); }
                else { int v = Read(ea); _c = (v & 1) != 0; v >>= 1; Write(ea, v); SetNz(v); }
                break;
            case "inc": { int v = (Read(ea) + 1) & 0xFF; Write(ea, v); SetNz(v); break; }
            case "dec": { int v = (Read(ea) - 1) & 0xFF; Write(ea, v); SetNz(v); break; }
            case "isc": { int v = (Read(ea) + 1) & 0xFF; Write(ea, v); Add(v ^ 0xFF); break; }
            case "asr": _a &= Read(ea); _c = (_a & 1) != 0; _a >>= 1; SetNz(_a); break;   // ALR
            case "jmp": _pc = ea; break;
            case "jsr":
            {
                int ret = _pc - 1;
                Push(ret >> 8);
                Push(ret & 0xFF);
                _pc = ea;
                break;
            }
            case "rts":
            {
                int lo = Pop();
                int hi = Pop();
                _pc = (((hi << 8) | lo) + 1) & 0xFFFF;
                break;
            }
            case "bpl": Branch(!_n, ea); break;
            case "bmi": Branch(_n, ea); break;
            case "bcc": Branch(!_c, ea); break;
            case "bcs": Branch(_c, ea); break;
            case "bne": Branch(!_z, ea); break;
            case "beq": Branch(_z, ea); break;
            case "clc": _c = false; break;
            case "dex": _x = (_x - 1) & 0xFF; SetNz(_x); break;
            case "inx": _x = (_x + 1) & 0xFF; SetNz(_x); break;
            case "iny": _y = (_y + 1) & 0xFF; SetNz(_y); break;
            case "pha": Push(_a); break;
            case "pla": _a = Pop(); SetNz(_a); break;
            case "tax": _x = _a; SetNz(_x); break;
            case "tay": _y = _a; SetNz(_y); break;
            case "tya": _a = _y; SetNz(_a); break;
            default: throw new InvalidOperationException($"no semantics for {mnemonic}");
        }
    }

    private void Add(int value)
    {
        int sum = _a + value + (_c ? 1 : 0);
        _v = (~(_a ^ value) & (_a ^ sum) & 0x80) != 0;
        _c = sum > 0xFF;
        _a = sum & 0xFF;
        SetNz(_a);
    }

    private void Branch(bool taken, int target)
    {
        if (!taken)
            return;

        _cycles += (_pc & 0xFF00) == (target & 0xFF00) ? 1 : 2;
        _pc = target;
    }

    /// <summary>NMOS 6502 cycle counts, before an indexed read's page-crossing extra.</summary>
    private static int BaseCycles(string mnemonic, AddressMode mode, bool store, bool rmw)
    {
        switch (mnemonic)
        {
            case "jmp": return 3;
            case "jsr": return 6;
            case "rts": return 6;
            case "pha": return 3;
            case "pla": return 4;
        }

        if (mode is AddressMode.Implied or AddressMode.Accumulator or AddressMode.Immediate or AddressMode.Relative)
            return 2;

        if (rmw)
        {
            return mode switch
            {
                AddressMode.ZeroPage => 5,
                AddressMode.ZeroPageX => 6,
                AddressMode.Absolute => 6,
                AddressMode.AbsoluteX or AddressMode.AbsoluteY => 7,
                AddressMode.IndirectY => 8,
                _ => throw new InvalidOperationException($"{mnemonic} has no {mode} form"),
            };
        }

        return mode switch
        {
            AddressMode.ZeroPage => 3,
            AddressMode.ZeroPageX or AddressMode.ZeroPageY => 4,
            AddressMode.Absolute => 4,
            AddressMode.AbsoluteX or AddressMode.AbsoluteY => store ? 5 : 4,
            AddressMode.IndirectY => store ? 6 : 5,
            _ => throw new InvalidOperationException($"{mnemonic} has no {mode} form"),
        };
    }
}
