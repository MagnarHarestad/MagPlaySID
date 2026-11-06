namespace MagPlay.C64;

/// <summary>
/// The VIC-II as SID tunes meet it: the raster beam moving down the screen, a line every 63 cycles
/// on PAL (312 lines a frame) and 65 on NTSC (263), and the raster interrupt - the usual way a tune
/// is played once a frame. The interrupt registers ($D019, $D01A) and the raster registers ($D011
/// bit 7, $D012) work; the rest keep what is written. The cycles the VIC takes from the CPU on bad
/// lines and for sprites are not taken.
/// </summary>
public sealed class Vic(int cyclesPerLine, int lines)
{
    private readonly byte[] _registers = new byte[0x40];
    private long _cycle;
    private int _compare;
    private int _flags;
    private int _mask;

    public int CyclesPerLine { get; } = cyclesPerLine;

    public int Lines { get; } = lines;

    public int CyclesPerFrame => CyclesPerLine * Lines;

    public bool Interrupt => (_flags & _mask & 0x0F) != 0;

    /// <summary>The cycle the interrupt line went active on.</summary>
    public long InterruptSince { get; private set; }

    /// <summary>The line the beam is on.</summary>
    public int Raster => (int)(_cycle / CyclesPerLine % Lines);

    /// <summary>The beam moves on to <paramref name="cycle"/>, raising the raster interrupt at the
    /// start of the line it is told to.</summary>
    public void AdvanceTo(long cycle)
    {
        long nextLine = (_cycle / CyclesPerLine + 1) * CyclesPerLine;
        while (nextLine <= cycle)
        {
            if ((int)(nextLine / CyclesPerLine % Lines) == _compare)
                Flag(0x01, nextLine);
            nextLine += CyclesPerLine;
        }
        _cycle = cycle;
    }

    /// <summary>The first cycle after <paramref name="now"/> the raster interrupt can go active on -
    /// the start of the line it is set to - or long.MaxValue when it is not let through.</summary>
    public long NextInterrupt(long now)
    {
        if ((_mask & 0x01) == 0 || _compare >= Lines)
            return long.MaxValue;
        long line = now / CyclesPerLine + 1;
        long ahead = ((_compare - (line % Lines)) % Lines + Lines) % Lines;
        return (line + ahead) * CyclesPerLine;
    }

    // ------------------------------------------------------------------ bad lines

    private bool _display;
    private bool _displayBefore;
    private long _displayChanged = long.MinValue;

    /// <summary>Whether the VIC takes the CPU's cycles on bad lines - the 40 or so of every eighth
    /// line it fetches the screen's characters on, when the display is on. Off, the CPU has every
    /// cycle, as it has with the screen blanked.</summary>
    public bool StealsCycles { get; set; }

    /// <summary>Where the VIC has the bus on the line <paramref name="cycle"/> is in, as the CPU
    /// meets it: from cycle 12, when it asks for it, to cycle 55 - or -1 when the line is not bad.
    /// A line is bad when its low three bits are the vertical scroll's, between lines $30 and $F7,
    /// in a frame whose line $30 had the display on.</summary>
    private long BadLineStart(long cycle)
    {
        long lineStart = cycle - cycle % CyclesPerLine;
        int line = (int)(cycle / CyclesPerLine % Lines);
        if (line < 0x30 || line > 0xF7 || (line & 7) != (_registers[0x11] & 7))
            return -1;
        long line30 = lineStart - (long)(line - 0x30) * CyclesPerLine;
        bool display = line30 >= _displayChanged ? _display : _displayBefore;
        return display ? lineStart : -1;
    }

    /// <summary>The cycles the VIC kept the CPU waiting for an instruction run from <paramref
    /// name="start"/> to <paramref name="end"/>: all of a bad line's from where it started, when it
    /// started in one; when it ran into one, the 43 from cycle 12 on - unless all it did there was
    /// write, which the CPU goes on doing (<paramref name="writesLast"/> cycles at its end).</summary>
    public long Stolen(long start, long end, int writesLast)
    {
        if (!StealsCycles)
            return 0;
        long bad = BadLineStart(start);
        if (bad >= 0 && start >= bad + 12 && start < bad + 55)
            return bad + 55 - start;
        bad = BadLineStart(end - 1);
        if (bad >= 0 && start < bad + 12 && end > bad + 12 && end - (bad + 12) > writesLast)
            return 43;
        return 0;
    }

    private void Flag(int bits, long at)
    {
        bool was = Interrupt;
        _flags |= bits;
        if (Interrupt && !was)
            InterruptSince = at;
    }

    public byte Read(int register)
    {
        register &= 0x3F;
        return register switch
        {
            0x11 => (byte)((_registers[0x11] & 0x7F) | ((Raster >> 1) & 0x80)),
            0x12 => (byte)Raster,
            0x19 => (byte)(_flags | 0x70 | (Interrupt ? 0x80 : 0)),
            0x1A => (byte)(_mask | 0xF0),
            >= 0x2F => 0xFF,
            _ => _registers[register],
        };
    }

    public void Write(int register, int value)
    {
        register &= 0x3F;
        value &= 0xFF;
        _registers[register] = (byte)value;
        switch (register)
        {
            case 0x11:
                _compare = (_compare & 0xFF) | ((value & 0x80) << 1);
                _displayBefore = _display;
                _display = (value & 0x10) != 0;
                _displayChanged = _cycle;
                break;
            case 0x12: _compare = (_compare & 0x100) | value; break;
            case 0x19: _flags &= ~value; break;
            case 0x1A:
                bool was = Interrupt;
                _mask = value & 0x0F;
                if (Interrupt && !was)
                    InterruptSince = _cycle;
                break;
        }
    }
}
