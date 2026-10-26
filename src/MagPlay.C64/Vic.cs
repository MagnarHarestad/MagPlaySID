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
            case 0x11: _compare = (_compare & 0xFF) | ((value & 0x80) << 1); break;
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
