namespace MagPlay.C64;

/// <summary>
/// A 6526 CIA, as far as SID tunes use one: its two timers - each counting down a cycle at a time,
/// or timer B counting timer A's underflows - and the interrupt control register that turns their
/// underflows into an interrupt. CIA 1's goes to the CPU's IRQ, CIA 2's to its NMI: a tune that
/// plays on the timer, or streams a digi from an NMI, runs on these.
///
/// A timer counts its latch down to zero and underflows on the cycle after, so it underflows every
/// latch + 1 cycles; the interrupt line follows the underflow a cycle later, as on the 6526. Tunes
/// read the counters to time themselves to the cycle, so the chip keeps the cycle it is on. The
/// ports read as nothing pressed; the time-of-day clock keeps what is written.
/// </summary>
public sealed class Cia
{
    private long _cycle;
    private int _counterA = 0xFFFF;
    private int _latchA = 0xFFFF;
    private int _counterB = 0xFFFF;
    private int _latchB = 0xFFFF;
    private int _controlA;
    private int _controlB;
    private int _mask;
    private int _flags;
    private readonly byte[] _registers = new byte[16];

    /// <summary>The chip's interrupt line: an underflow it is told to report is waiting.</summary>
    public bool Interrupt { get; private set; }

    /// <summary>The cycle the interrupt line last went active on - kept after it is cleared, since
    /// the 6510 latches the edge of an NMI even when the line falls before the NMI is taken.</summary>
    public long InterruptSince { get; private set; } = -1;

    /// <summary>Underflows of timer A so far, for whoever counts them.</summary>
    public long UnderflowsA { get; private set; }

    private bool RunningA => (_controlA & 0x01) != 0;

    private bool RunningB => (_controlB & 0x01) != 0;

    /// <summary>Timer B counts timer A's underflows rather than cycles.</summary>
    private bool BCountsA => (_controlB & 0x60) is 0x40 or 0x60;

    /// <summary>The timers run on to <paramref name="cycle"/>.</summary>
    public void AdvanceTo(long cycle)
    {
        long cycles = cycle - _cycle;
        if (cycles <= 0)
            return;

        if (!RunningA)
        {
            AdvanceB(cycles);
            _cycle = cycle;
            return;
        }

        while (cycles > 0)
        {
            long untilUnderflow = (long)_counterA + 1;
            if (untilUnderflow > cycles)
            {
                _counterA -= (int)cycles;
                if (!BCountsA)
                    AdvanceB(cycles);
                break;
            }

            cycles -= untilUnderflow;
            if (!BCountsA)
                AdvanceB(untilUnderflow);
            _cycle += untilUnderflow;

            _counterA = _latchA;
            UnderflowsA++;
            Flag(0x01);
            if (BCountsA && RunningB)
                CountB(1);

            if ((_controlA & 0x08) != 0)
            {
                _controlA &= ~0x01;
                AdvanceB(BCountsA ? 0 : cycles);
                break;
            }
        }
        _cycle = cycle;
    }

    /// <summary>Timer B runs on by <paramref name="cycles"/> from <see cref="_cycle"/>.</summary>
    private void AdvanceB(long cycles)
    {
        if (!RunningB || BCountsA || cycles <= 0)
            return;
        CountB(cycles);
    }

    /// <summary>Timer B counts down by <paramref name="counts"/>: cycles, or A's underflows.</summary>
    private void CountB(long counts)
    {
        long at = _cycle;
        while (counts > 0 && RunningB)
        {
            long untilUnderflow = (long)_counterB + 1;
            if (untilUnderflow > counts)
            {
                _counterB -= (int)counts;
                return;
            }

            counts -= untilUnderflow;
            at += BCountsA ? 0 : untilUnderflow;
            _counterB = _latchB;
            Flag(0x02, at);
            if ((_controlB & 0x08) != 0)
                _controlB &= ~0x01;
        }
    }

    private void Flag(int bit) => Flag(bit, _cycle);

    /// <summary>An underflow on <paramref name="at"/>: its flag set, and the line raised the cycle
    /// after when it is to be reported.</summary>
    private void Flag(int bit, long at)
    {
        _flags |= bit;
        if ((_mask & bit) != 0)
            Raise(at + 1);
    }

    private void Raise(long at)
    {
        if (Interrupt)
            return;
        Interrupt = true;
        InterruptSince = at;
    }

    public byte Read(int register)
    {
        switch (register & 0x0F)
        {
            case 0x00:
            case 0x01:
                return 0xFF;
            case 0x04: return (byte)_counterA;
            case 0x05: return (byte)(_counterA >> 8);
            case 0x06: return (byte)_counterB;
            case 0x07: return (byte)(_counterB >> 8);
            case 0x0D:
                // Reading the interrupt control register tells what happened and clears it.
                int value = _flags | (Interrupt ? 0x80 : 0);
                _flags = 0;
                Interrupt = false;
                return (byte)value;
            case 0x0E: return (byte)_controlA;
            case 0x0F: return (byte)_controlB;
            default: return _registers[register & 0x0F];
        }
    }

    public void Write(int register, int value)
    {
        value &= 0xFF;
        register &= 0x0F;
        _registers[register] = (byte)value;
        switch (register)
        {
            case 0x04:
                _latchA = (_latchA & 0xFF00) | value;
                break;
            case 0x05:
                _latchA = (_latchA & 0x00FF) | (value << 8);
                if (!RunningA)
                    _counterA = _latchA;
                break;
            case 0x06:
                _latchB = (_latchB & 0xFF00) | value;
                break;
            case 0x07:
                _latchB = (_latchB & 0x00FF) | (value << 8);
                if (!RunningB)
                    _counterB = _latchB;
                break;
            case 0x0D:
                // Bit 7 sets the bits written, clear clears them; an underflow already waiting
                // that becomes reported raises the line.
                if ((value & 0x80) != 0)
                    _mask |= value & 0x1F;
                else
                    _mask &= ~(value & 0x1F);
                if ((_flags & _mask) != 0)
                    Raise(_cycle + 1);
                break;
            case 0x0E:
                if ((value & 0x10) != 0)
                    _counterA = _latchA;
                _controlA = value & ~0x10;
                break;
            case 0x0F:
                if ((value & 0x10) != 0)
                    _counterB = _latchB;
                _controlB = value & ~0x10;
                break;
        }
    }
}
