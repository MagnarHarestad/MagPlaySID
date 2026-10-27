using MagPlay.Score;
using MagSid.Playback;
using MagSid.Sid;

namespace MagPlay.C64;

/// <summary>The SID chips the C64 plays into: each write, and each read of what a chip makes
/// itself - voice 3's oscillator and envelope - on the cycle the 6510 makes it. Chip 0 is the one
/// at $D400; 1 and 2 are the ones a 2SID or 3SID tune's header names, in its order.</summary>
public interface ISidPort
{
    void Write(long cycle, int chip, int register, int value);

    byte Read(long cycle, int chip, int register);
}

/// <summary>What the 6510 was running when it wrote to the SID.</summary>
public enum WriteContext : byte
{
    /// <summary>No interrupt: a tune's init, or a tune that plays from its main loop.</summary>
    Main,

    /// <summary>An IRQ - the raster's or CIA 1's: the call of a player, as SID players make it.</summary>
    Call,

    /// <summary>An NMI - CIA 2's: where digis are streamed from.</summary>
    Nmi,
}

/// <summary>
/// A Commodore 64 for playing SID tunes on: the 6510, 64 KiB of RAM with BASIC, the KERNAL - a
/// stand-in, see <see cref="Kernal"/> - and the I/O area banked in and out through the processor
/// port at $01, CIA 1 on the IRQ and CIA 2 on the NMI, the VIC's raster interrupt, and the SID.
/// It runs by cycles, not by calls: whatever a tune does with interrupts happens when it would,
/// and every write to the SID is recorded with the cycle it happens on.
///
/// Each access is made on the cycle the 6510 makes it - see <see cref="Mos6502.AccessCycle"/> - so
/// a tune that times itself by reading a timer reads what it would. What it leaves out: the cycles
/// the VIC steals on bad lines and for sprites, and the screen.
/// </summary>
public sealed class C64 : IMemoryBus
{
    private readonly byte[] _ram = new byte[0x10000];
    private readonly byte[] _kernal = Kernal.Build();
    private readonly byte[] _basic = new byte[0x2000];
    private readonly byte[] _colour = new byte[0x400];
    private readonly byte[] _sidRegisters = new byte[0x20];
    private readonly List<TimedWrite> _sidWrites = [];
    private readonly int[] _extraSids;
    private readonly List<TimedWrite>[] _extraSidWrites;
    private readonly List<WriteContext>[] _extraSidWriteContexts;
    private readonly List<WriteContext> _sidWriteContexts = [];
    private readonly List<long> _calls = [];

    /// <summary>The interrupts being run, innermost last, each with the stack pointer it was taken at.</summary>
    private readonly List<(bool Nmi, int Sp)> _interrupts = [];
    private readonly ISidPort? _chip;
    private readonly bool[] _writtenPages = new bool[256];
    private readonly bool _record;
    private long _synced;
    private long _nmiEdge = -1;
    private bool _maskedBefore = true;
    private int _port = 0x37;
    private int _direction = 0x2F;

    /// <summary>A SID model, run only once a tune reads what the chip itself makes - voice 3's
    /// oscillator or envelope - and then kept in step with every write.</summary>
    private SidChip? _sid;
    private long _sidCycle;

    /// <param name="sid">A chip to play into, for a C64 that is listened to; without one the
    /// writes are only recorded.</param>
    /// <param name="record">Keep every write in <see cref="SidWrites"/> - off for a C64 played for
    /// as long as anyone listens, which a digi would fill memory with.</param>
    public C64(Machine machine, IReadOnlyList<int>? extraSids = null, ISidPort? sid = null, bool record = true)
    {
        _chip = sid;
        _record = record;
        _extraSids = [.. extraSids ?? []];
        _extraSidWrites = [.. _extraSids.Select(_ => new List<TimedWrite>())];
        _extraSidWriteContexts = [.. _extraSids.Select(_ => new List<WriteContext>())];
        Machine = machine;
        Vic = machine == Machine.Ntsc ? new Vic(65, 263) : new Vic(63, 312);
        Cpu = new Mos6502(this);
        Kernal.SetVectors(_ram);
        _ram[0] = (byte)_direction;
        _ram[1] = (byte)_port;
    }

    public Machine Machine { get; }

    public Mos6502 Cpu { get; }

    public Cia Cia1 { get; } = new();

    public Cia Cia2 { get; } = new();

    public Vic Vic { get; }

    /// <summary>All RAM, whatever is banked over it.</summary>
    public byte[] Ram => _ram;

    public long Cycle => Cpu.Cycles;

    /// <summary>Every SID write so far, with its cycle, $D400-$D418 as 0-$18 - and the SID's
    /// mirrors up to $D7FF as the same, which they are on a C64 with one SID.</summary>
    public IReadOnlyList<TimedWrite> SidWrites => _sidWrites;

    /// <summary>What each of <see cref="SidWrites"/> was written from.</summary>
    public IReadOnlyList<WriteContext> SidWriteContexts => _sidWriteContexts;

    /// <summary>The cycle each call started on: an IRQ taken while no other IRQ was being run.</summary>
    public IReadOnlyList<long> Calls => _calls;

    /// <summary>A call is starting: the IRQ is about to be taken, nothing of it run yet.</summary>
    public event Action? CallStarting;

    /// <summary>The 6510 has written to RAM in <paramref name="page"/> - under the ROMs and I/O too.</summary>
    public bool PageWritten(int page) => _writtenPages[page & 0xFF];

    /// <summary>What each of <see cref="ExtraSidWrites"/> was written from.</summary>
    public IReadOnlyList<IReadOnlyList<WriteContext>> ExtraSidWriteContexts => _extraSidWriteContexts;

    /// <summary>The writes to each extra SID a tune's header names, at its own address.</summary>
    public IReadOnlyList<IReadOnlyList<TimedWrite>> ExtraSidWrites => _extraSidWrites;

    /// <summary>Runs until <paramref name="cycle"/>: the chips kept in step, an NMI taken when CIA 2
    /// raises its line, an IRQ while the CPU allows one.
    ///
    /// The 6510 looks at its interrupt lines on an instruction's second-last cycle, so a line that
    /// rises on the last waits for the instruction after; and CLI, SEI and PLP change the I flag only
    /// after that look, so the instruction after them runs under the old flag.</summary>
    public void RunUntil(long cycle)
    {
        while (Cpu.Cycles < cycle)
        {
            long now = Cpu.Cycles;
            Sync(now);
            long seen = now - 2;

            // An NMI is taken on the line's falling edge, which the 6510 latches.
            if (Cia2.InterruptSince > _nmiEdge && Cia2.InterruptSince <= seen)
            {
                _nmiEdge = Cia2.InterruptSince;
                Enter(nmi: true);
                Cpu.Interrupt(nmi: true);
                continue;
            }

            bool masked = Cpu.Opcode is 0x58 or 0x78 or 0x28 ? _maskedBefore : Cpu.InterruptDisable;
            if (!masked && ((Cia1.Interrupt && Cia1.InterruptSince <= seen) || (Vic.Interrupt && Vic.InterruptSince <= seen)))
            {
                Enter(nmi: false);
                Cpu.Interrupt(nmi: false);
                _maskedBefore = true;
                continue;
            }

            _maskedBefore = Cpu.InterruptDisable;
            Cpu.Step();
            if (Cpu.Opcode == 0x40)
                Unwind();
        }
        Sync(Cpu.Cycles);
    }

    /// <summary>An interrupt about to be taken: a call when it is an IRQ and none is being run.</summary>
    private void Enter(bool nmi)
    {
        Unwind();
        if (!nmi && !_interrupts.Any(i => !i.Nmi))
        {
            _calls.Add(Cpu.Cycles);
            CallStarting?.Invoke();
        }
        _interrupts.Add((nmi, Cpu.SP & 0xFF));
    }

    /// <summary>The interrupts left - by an RTI, or by a tune that resets the stack - are those whose
    /// stack pointer the stack is back at or above.</summary>
    private void Unwind()
    {
        while (_interrupts.Count > 0 && _interrupts[^1].Sp <= (Cpu.SP & 0xFF))
            _interrupts.RemoveAt(_interrupts.Count - 1);
    }

    private WriteContext Context()
    {
        Unwind();
        return _interrupts.Count == 0 ? WriteContext.Main : _interrupts[^1].Nmi ? WriteContext.Nmi : WriteContext.Call;
    }

    private void Sync(long now)
    {
        if (now <= _synced)
            return;
        Cia1.AdvanceTo(now);
        Cia2.AdvanceTo(now);
        Vic.AdvanceTo(now);
        _synced = now;
    }

    // ------------------------------------------------------------------ the memory map

    /// <summary>LORAM, HIRAM and CHAREN as the port drives them - an input pin floats high.</summary>
    private int Banking => (_port | ~_direction) & 0x07;

    private bool IoVisible => Banking is 5 or 6 or 7;

    public byte Read(int address)
    {
        switch (address)
        {
            case 0x0000: return (byte)_direction;
            case 0x0001: return (byte)((_port & _direction) | (~_direction & 0x17));
            case >= 0xA000 and < 0xC000 when Banking is 3 or 7:
                return _basic[address - 0xA000];
            case >= 0xD000 and < 0xE000 when IoVisible:
                return ReadIo(address);
            case >= 0xE000 when (Banking & 0x02) != 0:
                return _kernal[address - 0xE000];
            default:
                return _ram[address];
        }
    }

    public void Write(int address, int value)
    {
        value &= 0xFF;
        if (address == 0x0000)
            _direction = value;
        else if (address == 0x0001)
            _port = value;

        if (address is >= 0xD000 and < 0xE000 && IoVisible)
        {
            WriteIo(address, value);
            return;
        }

        _ram[address] = (byte)value;
        _writtenPages[address >> 8] = true;
    }

    private byte ReadIo(int address)
    {
        Sync(Cpu.AccessCycle);
        return address switch
        {
            < 0xD400 => Vic.Read(address),
            _ when ExtraSid(address) is >= 0 and var chip => ReadSid(chip + 1, address & 0x1F),
            < 0xD800 => ReadSid(0, address & 0x1F),
            < 0xDC00 => (byte)(_colour[address - 0xD800] | 0xF0),
            < 0xDD00 => Cia1.Read(address),
            < 0xDE00 => Cia2.Read(address),
            _ => 0xFF,
        };
    }

    private void WriteIo(int address, int value)
    {
        Sync(Cpu.AccessCycle);
        switch (address)
        {
            case < 0xD400: Vic.Write(address, value); break;
            case var _ when ExtraSid(address) is >= 0 and var chip:
                if ((address & 0x1F) > 0x18)
                    break;
                _chip?.Write(Cpu.AccessCycle, chip + 1, address & 0x1F, value);
                if (_record)
                {
                    _extraSidWrites[chip].Add(new TimedWrite(Cpu.AccessCycle, address & 0x1F, value));
                    _extraSidWriteContexts[chip].Add(Context());
                }
                break;
            case < 0xD800: WriteSid(address & 0x1F, value); break;
            case < 0xDC00: _colour[address - 0xD800] = (byte)(value & 0x0F); break;
            case < 0xDD00: Cia1.Write(address, value); break;
            case < 0xDE00: Cia2.Write(address, value); break;
        }
    }

    // ------------------------------------------------------------------ the SID

    private int ExtraSid(int address)
    {
        for (int i = 0; i < _extraSids.Length; i++)
        {
            if ((address & ~0x1F) == _extraSids[i])
                return i;
        }
        return -1;
    }

    private void WriteSid(int register, int value)
    {
        if (register > 0x18)
            return;

        _sidRegisters[register] = (byte)value;
        _chip?.Write(Cpu.AccessCycle, 0, register, value);
        if (!_record)
            return;

        _sidWrites.Add(new TimedWrite(Cpu.AccessCycle, register, value));
        _sidWriteContexts.Add(Context());
        if (_sid is not null)
        {
            ClockSidTo(Cpu.AccessCycle);
            _sid.Write(register, value);
        }
    }

    private byte ReadSid(int chip, int register)
    {
        switch (register)
        {
            case 0x19:
            case 0x1A:
                return 0xFF;
            case 0x1B:
            case 0x1C:
                if (_chip is not null)
                    return _chip.Read(Cpu.AccessCycle, chip, register);
                if (chip > 0)
                    return 0;
                EnsureSid();
                ClockSidTo(Cpu.AccessCycle);
                return (byte)_sid!.Read(register);
            default:
                return 0;
        }
    }

    /// <summary>The model, started the first time it is needed and brought up to now by playing it
    /// every write made so far.</summary>
    private void EnsureSid()
    {
        if (_sid is not null)
            return;

        _sid = new SidChip();
        _sidCycle = 0;
        foreach (var write in _sidWrites)
        {
            ClockSidTo(write.Cycle);
            _sid.Write(write.Register, write.Value);
        }
    }

    private void ClockSidTo(long cycle)
    {
        for (; _sidCycle < cycle; _sidCycle++)
            _sid!.Clock();
    }
}
