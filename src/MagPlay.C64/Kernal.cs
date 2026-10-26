namespace MagPlay.C64;

/// <summary>
/// A stand-in for the C64's KERNAL ROM - the real one is Commodore's and is not ours to ship - with
/// what SID tunes reach into it for: the interrupt entry points and their RAM vectors, the routines
/// a tune's own interrupt handler ends by jumping to, and a return for every jump-table call, so an
/// RSID's init that clears the screen or resets the I/O carries on. What the real routines would
/// do - print, scan the keyboard - does not happen.
///
/// Laid out at the real addresses, since tunes jump to them by number:
/// $FF48 saves A, X, Y and goes through $0314 (IRQ) or $0316 (BRK); $EA31 is the IRQ's default
/// end and $EA7E/$EA81 acknowledge CIA 1 and restore the registers; $FE43 is the NMI entry, through
/// $0318, and $FE47/$FEBC its default and its end.
/// </summary>
internal static class Kernal
{
    public const int Irq = 0xFF48;
    public const int IrqDefault = 0xEA31;
    public const int IrqEnd = 0xEA81;
    public const int Nmi = 0xFE43;
    public const int NmiDefault = 0xFE47;
    public const int NmiEnd = 0xFEBC;
    public const int Break = 0xFE66;

    public static byte[] Build()
    {
        var rom = new byte[0x2000];
        void Put(int address, params int[] code)
        {
            for (int i = 0; i < code.Length; i++)
                rom[address - 0xE000 + i] = (byte)code[i];
        }

        // Every jump-table entry, and the routines RSID inits call most, returns at once.
        for (int entry = 0xFF81; entry <= 0xFFF3; entry += 3)
            Put(entry, 0x60);
        foreach (int routine in new[] { 0xE544, 0xE5A0, 0xFD15, 0xFDA3, 0xFF5B, 0xE3BF, 0xE453 })
            Put(routine, 0x60);

        // IRQ: pha / txa / pha / tya / pha / tsx / lda $0104,x / and #$10 / beq irq / jmp ($0316) / irq: jmp ($0314)
        Put(Irq, 0x48, 0x8A, 0x48, 0x98, 0x48, 0xBA, 0xBD, 0x04, 0x01, 0x29, 0x10, 0xF0, 0x03, 0x6C, 0x16, 0x03, 0x6C, 0x14, 0x03);

        // The default IRQ's end: acknowledge CIA 1, restore the registers, return.
        Put(IrqDefault, 0x4C, 0x7E, 0xEA);
        Put(0xEA7E, 0xAD, 0x0D, 0xDC);
        Put(IrqEnd, 0x68, 0xA8, 0x68, 0xAA, 0x68, 0x40);

        // NMI: sei / jmp ($0318); its default saves the registers, acknowledges CIA 2 and ends.
        Put(Nmi, 0x78, 0x6C, 0x18, 0x03);
        Put(NmiDefault, 0x48, 0x8A, 0x48, 0x98, 0x48, 0xAD, 0x0D, 0xDD, 0x4C, 0xBC, 0xFE);
        Put(NmiEnd, 0x68, 0xA8, 0x68, 0xAA, 0x68, 0x40);

        // BRK ends as an IRQ does.
        Put(Break, 0x4C, 0x81, 0xEA);

        Put(0xFFFA, Nmi & 0xFF, Nmi >> 8, 0xE2, 0xFC, Irq & 0xFF, Irq >> 8);
        return rom;
    }

    /// <summary>The RAM vectors as the KERNAL leaves them after a reset.</summary>
    public static void SetVectors(byte[] ram)
    {
        void Word(int address, int value)
        {
            ram[address] = (byte)value;
            ram[address + 1] = (byte)(value >> 8);
        }

        Word(0x0314, IrqDefault);
        Word(0x0316, Break);
        Word(0x0318, NmiDefault);
    }
}
