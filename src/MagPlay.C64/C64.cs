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
/// Data read from one address after another - a digi's sample, read a byte or a nibble at a time -
/// by an NMI or a tune's main loop: when it started, where, and how far it went.
/// </summary>
/// <param name="Cycle">When the first byte was read.</param>
/// <param name="Start">Where.</param>
/// <param name="Address">The last address read.</param>
/// <param name="Reads">How many reads, a byte read twice for its two nibbles counted twice.</param>
/// <param name="LastCycle">When the last was.</param>
public readonly record struct DataRun(long Cycle, int Start, int Address, int Reads, long LastCycle, WriteContext Context);

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

    /// <summary>What each chip - the first, then the extra ones - is heard to be doing.</summary>
    private readonly Quiet[] _quiet;

    /// <summary>The data runs ended, and those an NMI or the main loop may still be reading on.</summary>
    private readonly List<DataRun> _runs = [];
    private readonly List<DataRun> _reading = [];

    /// <summary>The instruction being run - its own bytes are not data - and what it is run from;
    /// -1 while an interrupt is being taken.</summary>
    private int _stepAt = -1;
    private WriteContext _stepContext;

    /// <summary>Cycles without the next address read that end a run: a few of a digi's samples.</summary>
    private const int RunGap = 4000;

    /// <summary>The fewest reads a run takes to be a digi's sample rather than a table looked up.</summary>
    private const int ShortestRun = 64;

    /// <summary>Runs followed at once: a digi's sample and the tables its player looks up beside it.</summary>
    private const int RunsFollowed = 12;

    private long _synced;

    /// <summary>The first cycle a timer or the beam can change an interrupt line on; before it,
    /// they are not brought up to date - 0 to look again.</summary>
    private long _nextEvent;
    private long _nmiEdge = -1;
    private bool _maskedBefore = true;
    private int _port = 0x37;
    private int _direction = 0x2F;

    /// <summary>A model of what the chip itself makes - voice 3's oscillator and envelope, see
    /// VoiceThree - started only once a tune reads one of them, and then kept in step with every write.</summary>
    private VoiceThree? _sid;
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
        _quiet = [.. Enumerable.Range(0, 1 + _extraSids.Length).Select(_ => new Quiet())];
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

    /// <summary>The cycle from which no chip has been heard, by its envelopes' own times; null
    /// while any voice sounds.</summary>
    public long? QuietSince()
    {
        long since = 0;
        foreach (var chip in _quiet)
        {
            if (chip.Since(Cpu.Cycles, Machines.ClockHz(Machine)) is not { } quiet)
                return null;
            since = Math.Max(since, quiet);
        }
        return since;
    }

    /// <summary>The writes to the first chip's volume register made outside the calls - an NMI's or the
    /// main loop's, a digi's - counted whether or not they are kept.</summary>
    public int VolumeWritesOutsideCalls { get; private set; }

    /// <summary>The data NMIs and the main loop read from one address after another, run by run:
    /// a digi's samples, each where it starts - kept while the writes are recorded. Runs of fewer
    /// than <see cref="ShortestRun"/> reads, a player's tables, are left out.</summary>
    public IReadOnlyList<DataRun> DataRuns =>
        [.. _runs.Concat(_reading.Where(r => r.Reads >= ShortestRun)).OrderBy(r => r.Cycle)];

    /// <summary>Which writes are recorded, by what they were written from; all when null. A tune
    /// transcribed for a view keeps its player's writes only - not a digi's many thousands. The
    /// chip model a read of voice 3 starts is then played only the writes kept before it.</summary>
    public Func<WriteContext, bool>? Keep { get; set; }

    /// <summary>Whether a JMP to itself waiting for an interrupt is jumped over - see Idling. Off
    /// only to check that it changes nothing.</summary>
    public bool SkipsIdle { get; set; } = true;

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
            // The timers and the beam only change the interrupt lines when one of them is due, or
            // when the CPU touches their registers - which brings them up to now itself.
            if (now >= _nextEvent)
            {
                Sync(now);
                _nextEvent = Math.Min(Math.Min(Cia1.NextInterrupt(), Cia2.NextInterrupt()), Vic.NextInterrupt(now));
            }
            long seen = now - 2;

            // An NMI is taken on the line's falling edge, which the 6510 latches.
            if (Cia2.InterruptSince > _nmiEdge && Cia2.InterruptSince <= seen)
            {
                _nmiEdge = Cia2.InterruptSince;
                Enter(nmi: true);
                _stepAt = -1;
                Cpu.Interrupt(nmi: true);
                continue;
            }

            bool masked = Cpu.Opcode is 0x58 or 0x78 or 0x28 ? _maskedBefore : Cpu.InterruptDisable;
            if (!masked && ((Cia1.Interrupt && Cia1.InterruptSince <= seen) || (Vic.Interrupt && Vic.InterruptSince <= seen)))
            {
                Enter(nmi: false);
                _stepAt = -1;
                Cpu.Interrupt(nmi: false);
                _maskedBefore = true;
                continue;
            }

            _maskedBefore = Cpu.InterruptDisable;
            if (Idling(cycle))
                continue;
            if (_record)
            {
                _stepAt = Cpu.PC;
                _stepContext = Context();
            }
            long started = Cpu.Cycles;
            Cpu.Step();
            if (Vic.StealsCycles)
                Cpu.Idle(Vic.Stolen(started, Cpu.Cycles, WritesLast[Cpu.Opcode]));
            if (Cpu.Opcode == 0x40)
                Unwind();
        }
        Sync(Cpu.Cycles);
    }

    /// <summary>
    /// Waiting for an interrupt in a JMP to itself - the PSID driver's main loop, and many a tune's -
    /// jumped over to just before the next one can come: the timers and the beam keep their own
    /// time, so nothing of the run changes but how long it takes. A tune spends most of each frame
    /// there; skipped, a song's length is measured several times as fast.
    /// </summary>
    private bool Idling(long until)
    {
        if (!SkipsIdle)
            return false;
        int pc = Cpu.PC;
        if (Code(pc) != 0x4C || (Code(pc + 1) | (Code(pc + 2) << 8)) != pc)
            return false;
        long now = Cpu.Cycles;
        // An interrupt the CPU would take now is taken now.
        if (Cia2.InterruptSince > _nmiEdge || (!Cpu.InterruptDisable && (Cia1.Interrupt || Vic.Interrupt)))
            return false;
        long next = Math.Min(Cia2.NextInterrupt(), Cpu.InterruptDisable ? long.MaxValue : Math.Min(Cia1.NextInterrupt(), Vic.NextInterrupt(now)));
        // Up to the last JMP that ends before the line would be seen, and no further than asked.
        long limit = Math.Min(until, next == long.MaxValue ? until : next + 1);
        long jumps = (limit - now) / 3 - 1;
        if (jumps <= 0)
            return false;
        Cpu.Idle(3 * jumps);
        return true;
    }

    /// <summary>A byte of code as the CPU would fetch it - but none of the I/O, which is not code here.</summary>
    private int Code(int address)
    {
        address &= 0xFFFF;
        return address switch
        {
            >= 0xA000 and < 0xC000 when Banking is 3 or 7 => _basic[address - 0xA000],
            >= 0xD000 and < 0xE000 when IoVisible => -1,
            >= 0xE000 when (Banking & 0x02) != 0 => _kernal[address - 0xE000],
            _ => _ram[address],
        };
    }

    /// <summary>How many of each instruction's last cycles are writes, which a bad line does not stop:
    /// a store's one, a read-modify-write's two, a push's one.</summary>
    private static readonly int[] WritesLast = BuildWritesLast();

    private static int[] BuildWritesLast()
    {
        var w = new int[256];
        foreach (int op in new[] { 0x81, 0x84, 0x85, 0x86, 0x8C, 0x8D, 0x8E, 0x91, 0x94, 0x95, 0x96, 0x99, 0x9D, 0x48, 0x08 })
            w[op] = 1;
        foreach (int op in new[] { 0x06, 0x0E, 0x16, 0x1E, 0x26, 0x2E, 0x36, 0x3E, 0x46, 0x4E, 0x56, 0x5E, 0x66, 0x6E, 0x76, 0x7E,
                                   0xC6, 0xCE, 0xD6, 0xDE, 0xE6, 0xEE, 0xF6, 0xFE })
            w[op] = 2;
        return w;
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
        if (_stepAt >= 0 && _stepContext != WriteContext.Call && address >= 0x0200
            && (address < _stepAt || address > _stepAt + 2))
            Follow(address);

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

    /// <summary>A byte of data read: the next of a run being read - the same address again, for a
    /// byte's other nibble, or the one after - or the first of a new one.</summary>
    private void Follow(int address)
    {
        if (address is >= 0xD000 and < 0xE000 && IoVisible)
            return;
        long now = Cpu.Cycles;
        for (int i = 0; i < _reading.Count; i++)
        {
            var run = _reading[i];
            if ((address == run.Address || address == run.Address + 1) && run.Context == _stepContext)
            {
                _reading[i] = run with { Address = address, Reads = run.Reads + 1, LastCycle = now };
                return;
            }
        }

        // A run not read on for a while is over; with too many followed, the least read goes.
        for (int i = _reading.Count - 1; i >= 0; i--)
        {
            if (now - _reading[i].LastCycle > RunGap)
            {
                if (_reading[i].Reads >= ShortestRun)
                    _runs.Add(_reading[i]);
                _reading.RemoveAt(i);
            }
        }
        if (_reading.Count >= RunsFollowed)
        {
            int least = 0;
            for (int i = 1; i < _reading.Count; i++)
                if (_reading[i].Reads < _reading[least].Reads)
                    least = i;
            _reading.RemoveAt(least);
        }
        _reading.Add(new DataRun(now, address, address, 1, now, _stepContext));
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
        _nextEvent = 0;
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
        _nextEvent = 0;
        switch (address)
        {
            case < 0xD400: Vic.Write(address, value); break;
            case var _ when ExtraSid(address) is >= 0 and var chip:
                if ((address & 0x1F) > 0x18)
                    break;
                _chip?.Write(Cpu.AccessCycle, chip + 1, address & 0x1F, value);
                _quiet[chip + 1].Write(Cpu.AccessCycle, address & 0x1F, value);
                if (_record && Context() is var extra && Keep?.Invoke(extra) != false)
                {
                    _extraSidWrites[chip].Add(new TimedWrite(Cpu.AccessCycle, address & 0x1F, value));
                    _extraSidWriteContexts[chip].Add(extra);
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
        _quiet[0].Write(Cpu.AccessCycle, register, value);
        if (!_record)
            return;

        if (_sid is not null)
        {
            ClockSidTo(Cpu.AccessCycle);
            _sid.Write(register, value);
        }

        // A write not kept is lost to a chip model started later - see Keep.
        var context = Context();
        if (register == 0x18 && context != WriteContext.Call)
            VolumeWritesOutsideCalls++;
        if (Keep?.Invoke(context) == false)
            return;
        _sidWrites.Add(new TimedWrite(Cpu.AccessCycle, register, value));
        _sidWriteContexts.Add(context);
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

        _sid = new VoiceThree();
        _sidCycle = 0;
        foreach (var write in _sidWrites)
        {
            ClockSidTo(write.Cycle);
            _sid.Write(write.Register, write.Value);
        }
    }

    private void ClockSidTo(long cycle)
    {
        if (cycle <= _sidCycle)
            return;
        _sid!.Clock(cycle - _sidCycle);
        _sidCycle = cycle;
    }
}
