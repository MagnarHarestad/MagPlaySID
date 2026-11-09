namespace MagPlay.C64;

/// <summary>
/// The VIC-II as SID tunes meet it: the raster beam moving down the screen, a line every 63 cycles
/// on PAL (312 lines a frame) and 65 on NTSC (263), and the raster interrupt - the usual way a tune
/// is played once a frame. The interrupt registers ($D019, $D01A) and the raster registers ($D011
/// bit 7, $D012) work; the rest keep what is written. The VIC takes the CPU's cycles as a C64's
/// does, on bad lines and for sprites - which a tune playing samples from a loop timed to the
/// raster counts on - though its registers start at nothing: the screen off, no sprites, every
/// cycle the CPU's until a tune turns them on.
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
        AdvanceSprites(cycle);
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
    /// line it fetches the screen's characters on, when the display is on - and for the sprites it
    /// fetches. Off, the CPU has every cycle.</summary>
    public bool StealsCycles { get; set; } = true;

    /// <summary>Whether the VIC can keep the CPU waiting at all: the display on now or before, or a
    /// sprite on or fetched - most tunes never turn either on, and have every cycle.</summary>
    public bool MayStall => StealsCycles && (_display || _displayBefore || _spritesOn != 0 || _registers[0x15] != 0);

    /// <summary>Where the line <paramref name="cycle"/> is in starts, when it is a bad line - its
    /// wait is <see cref="BadFrom"/> on - or -1 when it is not.
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
    /// name="start"/> to <paramref name="end"/> whose last <paramref name="writesLast"/> cycles are
    /// writes.</summary>
    public long Stolen(long start, long end, int writesLast)
    {
        int length = (int)(end - start);
        return Stolen(start, end, length - writesLast, length);
    }

    /// <summary>The cycles the VIC kept the CPU waiting for what it ran from <paramref name="start"/>
    /// to <paramref name="end"/>, its cycles <paramref name="writesFrom"/> to <paramref
    /// name="writesTo"/> (from 0) writes: each read waits while the VIC has the bus or has asked for
    /// it - a bad line's 43 cycles, a sprite's five around its two fetches - and a
    /// write goes on, as the 6510 goes on writing when the VIC asks for the bus.</summary>
    public long Stolen(long start, long end, int writesFrom, int writesTo)
    {
        if (!StealsCycles)
            return 0;
        AdvanceSprites(start);
        if (!_display && !_displayBefore && _spritesOn == 0 && _registers[0x15] == 0)
            return 0;

        long at = start;
        int length = (int)(end - start);
        Locate(start);
        if (_spritesOn == 0 && _registers[0x15] == 0 && SpritesFetched(_line - 1) == 0)
        {
            // Bad lines alone: a read in one waits to its end - worked out line by line, not cycle
            // by cycle.
            long lineStart = _lineStart;
            bool bad = _lineBad;
            for (int i = 0; i < length; i++)
            {
                if (at >= lineStart + CyclesPerLine)
                {
                    Locate(at);
                    lineStart = _lineStart;
                    bad = _lineBad;
                }
                long offset = at - lineStart;
                if (bad && (i < writesFrom || i >= writesTo) && offset >= BadFrom && offset < BadFrom + 43)
                    at = lineStart + BadFrom + 43;
                at++;
            }
            return at - end;
        }
        for (int i = 0; i < length; i++)
        {
            if (i < writesFrom || i >= writesTo)
            {
                while (Blocked(at))
                    at++;
            }
            at++;
        }
        return at - end;
    }

    /// <summary>Where a bad line's wait starts: Bauer's cycle 12, when the VIC asks for the bus - a
    /// line here starts at 0 with his cycle 1, where the raster interrupt comes - and it lasts the 43
    /// cycles to his 54, the last of the 40 character fetches.</summary>
    public static int BadFrom { get; set; } = 11;

    /// <summary>The line <see cref="Locate"/> last found, where it starts, and whether it is a bad
    /// line - kept, as asked for cycle after cycle, until a cycle past it or a register written:
    /// a 64-bit division per cycle made a tune with sprites on run a quarter as fast.</summary>
    private long _line, _lineStart;
    private bool _lineBad;
    private int _lineFor = -1;

    /// <summary>Bumped by each register written, which may change what a line is.</summary>
    private int _written;

    /// <summary>The line <paramref name="cycle"/> is in, as <see cref="_line"/>, <see
    /// cref="_lineStart"/> and <see cref="_lineBad"/>.</summary>
    private void Locate(long cycle)
    {
        long offset = cycle - _lineStart;
        if (_lineFor == _written && offset >= 0 && offset < CyclesPerLine)
            return;
        _line = cycle / CyclesPerLine;
        _lineStart = _line * CyclesPerLine;
        _lineBad = BadLineStart(cycle) >= 0;
        _lineFor = _written;
    }

    /// <summary>Whether a read on <paramref name="cycle"/> must wait for the VIC.</summary>
    private bool Blocked(long cycle)
    {
        Locate(cycle);
        int offset = (int)(cycle - _lineStart);
        if (_lineBad && offset >= BadFrom && offset < BadFrom + 43)
            return true;

        // A sprite's line asks for the bus three cycles before its fetches, the last three sprites'
        // in the line after - so this line's sprites and the line before's.
        long line = _line;
        AdvanceSprites(cycle + 1);
        return SpriteBlocks(SpritesFetched(line), offset) || SpriteBlocks(SpritesFetched(line - 1), offset + CyclesPerLine);
    }

    /// <summary>Whether one of the sprites <paramref name="mask"/> keeps the bus <paramref
    /// name="offset"/> cycles into the line they were found on - looked up, as asked for every
    /// cycle a tune with sprites reads on: <see cref="SpriteBlocksWorkedOut"/> for each mask and
    /// offset, made once.</summary>
    private bool SpriteBlocks(int mask, int offset) =>
        mask != 0 && _spriteBlocks[(mask * 2 * CyclesPerLine) + offset];

    private readonly bool[] _spriteBlocks = SpriteBlockTable(cyclesPerLine);

    private static bool[] SpriteBlockTable(int cyclesPerLine)
    {
        var table = new bool[256 * 2 * cyclesPerLine];
        for (int mask = 0; mask < 256; mask++)
            for (int offset = 0; offset < 2 * cyclesPerLine; offset++)
                table[(mask * 2 * cyclesPerLine) + offset] = SpriteBlocksWorkedOut(cyclesPerLine, mask, offset);
        return table;
    }

    private static bool SpriteBlocksWorkedOut(int cyclesPerLine, int mask, int offset)
    {
        if (mask == 0)
            return false;
        // Sprite n is fetched in the two cycles from Bauer's cycle 58 + 2n on PAL, the bus asked
        // for 3 before: here, where a line starts at 0, from CyclesPerLine - 9 + 2n - which
        // matches VICE's samples in Comaland's sprite part write for write.
        int first = offset - (cyclesPerLine - 9);
        if (first < 0)
            return false;
        for (int sprite = 0; sprite < 8; sprite++)
        {
            if ((mask & (1 << sprite)) != 0 && first >= 2 * sprite && first <= 2 * sprite + 4)
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ sprites

    /// <summary>As Christian Bauer's VIC article has it: each sprite's DMA, the count of its data
    /// (MCBASE) and its Y expansion flip-flop. Sprites are fetched on every line their DMA is on,
    /// and only the cycles they take matter here - what they show does not.</summary>
    private int _spritesOn;
    private readonly int[] _spriteCount = new int[8];
    private int _spriteExpand;
    private long _spritesAt;

    /// <summary>Each of the last four lines' sprites fetched, by line.</summary>
    private readonly long[] _fetchedLine = [-1, -1, -1, -1];
    private readonly int[] _fetched = new int[4];

    /// <summary>Where on a line the data counts move on (cycles 15 and 16), and where the flip-flops
    /// turn and the DMA starts (55 and 56).</summary>
    private const int CountAt = 16;
    private const int CheckAt = 55;

    private int SpritesFetched(long line) => _fetchedLine[line & 3] == line ? _fetched[line & 3] : 0;

    /// <summary>The sprites run on to <paramref name="cycle"/>: what happens on the cycles before it.</summary>
    private void AdvanceSprites(long cycle)
    {
        // Nothing happens before the next count or check: only the time moves on.
        if (cycle <= _spriteEvent)
        {
            if (_spritesAt < cycle)
                _spritesAt = cycle;
            return;
        }
        while (_spritesAt < cycle)
        {
            if (_spritesOn == 0 && _registers[0x15] == 0)
            {
                _spritesAt = cycle;
                break;
            }
            long line = _spritesAt / CyclesPerLine;
            int offset = (int)(_spritesAt % CyclesPerLine);
            if (offset <= CountAt)
            {
                long count = line * CyclesPerLine + CountAt;
                if (count >= cycle)
                    break;
                CountSprites();
                _spritesAt = count + 1;
            }
            else if (offset <= CheckAt)
            {
                long check = line * CyclesPerLine + CheckAt;
                if (check >= cycle)
                    break;
                CheckSprites(line);
                _spritesAt = check + 1;
            }
            else
                _spritesAt = (line + 1) * CyclesPerLine;
        }
        _spritesAt = Math.Max(_spritesAt, cycle);
        _spriteEvent = NextSpriteEvent(_spritesAt);
    }

    /// <summary>The cycle of the first count or check at or after <see cref="_spritesAt"/>, which
    /// happens once the sprites are run past it; -1 before the sprites have been run at all.</summary>
    private long _spriteEvent = -1;

    private long NextSpriteEvent(long at)
    {
        long line = at / CyclesPerLine;
        int offset = (int)(at % CyclesPerLine);
        return line * CyclesPerLine + (offset <= CountAt ? CountAt : offset <= CheckAt ? CheckAt : CyclesPerLine + CountAt);
    }

    /// <summary>Cycles 15 and 16: a sprite whose flip-flop is set has had its three bytes for the
    /// line; its DMA stops when all 63 are.</summary>
    private void CountSprites()
    {
        int expandY = _registers[0x17];
        for (int sprite = 0; sprite < 8; sprite++)
        {
            int bit = 1 << sprite;
            if ((_spritesOn & bit) == 0)
                continue;
            if ((expandY & bit) == 0)
                _spriteExpand |= bit;
            if ((_spriteExpand & bit) != 0)
                _spriteCount[sprite] += 3;
            if (_spriteCount[sprite] >= 63)
                _spritesOn &= ~bit;
        }
    }

    /// <summary>Cycles 55 and 56: the flip-flops of Y-expanded sprites turn, and a sprite switched
    /// on whose Y is the line starts its DMA; the sprites fetched at the end of the line are noted.</summary>
    private void CheckSprites(long line)
    {
        int enabled = _registers[0x15], expandY = _registers[0x17];
        int raster = (int)(line % Lines) & 0xFF;
        for (int sprite = 0; sprite < 8; sprite++)
        {
            int bit = 1 << sprite;
            if ((expandY & bit) != 0)
                _spriteExpand ^= bit;
            else
                _spriteExpand |= bit;
            if ((_spritesOn & bit) == 0 && (enabled & bit) != 0 && _registers[1 + 2 * sprite] == raster)
            {
                _spritesOn |= bit;
                _spriteCount[sprite] = 0;
                if ((expandY & bit) != 0)
                    _spriteExpand &= ~bit;
            }
        }
        _fetchedLine[line & 3] = line;
        _fetched[line & 3] = _spritesOn;
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
        _written++;
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
