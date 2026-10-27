using MagPlay.Score;
using MagSid.Playback;

namespace MagPlay.C64;

/// <summary>
/// A <c>.sid</c> loaded into a <see cref="C64"/> and started the way a SID player starts it, then
/// left running: its SID writes, with their cycles, are the tune exactly as it plays.
///
/// A PSID with a play routine gets a small driver: init is called with the song number, then play
/// from an interrupt - the raster's once a frame, or CIA 1's timer when the header says the song
/// is timed by it, at the KERNAL's 60 Hz until the tune sets its own rate. An RSID, or a PSID
/// without a play routine, finds the machine as the KERNAL leaves it - CIA 1's timer interrupting
/// through $0314 - and its init installs whatever interrupts it plays from. Either way the driver
/// waits in a loop after init, and the tune's interrupts do the rest.
/// </summary>
public sealed class SidTune
{
    private SidTune(C64 computer, SidFile file, int song, int driver)
    {
        Computer = computer;
        File = file;
        Song = song;
        Driver = driver;
    }

    public C64 Computer { get; }

    public SidFile File { get; }

    public int Song { get; }

    /// <summary>Where the driver was put.</summary>
    public int Driver { get; }

    /// <summary>The KERNAL's CIA 1 timer: 60 interrupts a second on either machine.</summary>
    public static int KernalTimer(Machine machine) => machine == Machine.Ntsc ? 0x4295 : 0x4025;

    /// <summary>Why this tune cannot be started here; null when it can.</summary>
    public static string? Unsupported(SidFile file)
    {
        if (file.IsBasic)
            return "This RSID is a BASIC program, and there is no BASIC interpreter here to RUN it.";
        if (file.LoadAddress + file.Data.Length > 0x10000)
            return "The tune's data would run past the end of memory.";
        if (file.LoadAddress < 0x0200)
            return "The tune loads over the zero page and the stack.";
        return null;
    }

    /// <param name="sid">The chip it plays into, for a tune that is listened to.</param>
    /// <param name="record">Keep every SID write, for a tune that is transcribed.</param>
    public static SidTune Load(SidFile file, int song, ISidPort? sid = null, bool record = true)
    {
        if (Unsupported(file) is { } reason)
            throw new SidPlaybackException(reason);

        var machine = new C64(file.IsNtsc ? Machine.Ntsc : Machine.Pal, file.ExtraSids, sid, record);
        file.Data.CopyTo(machine.Ram, file.LoadAddress);

        int driver = DriverAddress(file);
        var (code, ramEntry) = DriverCode(file, song, driver, machine.Machine);
        code.CopyTo(machine.Ram, driver);

        // The hardware IRQ vector in the RAM under the KERNAL, for a tune that banks it out -
        // unless the tune's own data is there.
        if (ramEntry >= 0 && file.LoadAddress + file.Data.Length <= 0xFFFE)
        {
            machine.Ram[0xFFFE] = (byte)ramEntry;
            machine.Ram[0xFFFF] = (byte)(ramEntry >> 8);
        }
        machine.Cpu.SP = 0xFF;
        machine.Cpu.InterruptDisable = true;
        machine.Cpu.PC = driver;
        return new SidTune(machine, file, song, driver);
    }

    /// <summary>Runs the tune on for <paramref name="cycles"/> cycles.</summary>
    public void Run(long cycles) => Computer.RunUntil(Computer.Cycle + cycles);

    /// <summary>The tune's SID writes over <paramref name="seconds"/> seconds of playing.</summary>
    public static IReadOnlyList<TimedWrite> Record(SidFile file, int song, double seconds)
    {
        var tune = Load(file, song);
        tune.Run((long)(seconds * Machines.ClockHz(tune.Computer.Machine)));
        return tune.Computer.SidWrites;
    }

    // ------------------------------------------------------------------ the driver

    private const int DriverLength = 0x80;

    /// <summary>A page the tune leaves alone: the one its header offers, or else the first page from
    /// $0400 it does not load into, past BASIC's - where libsidplayfp puts its driver. Not the
    /// cassette buffer at $0334: tunes keep their variables there and clear it in init.</summary>
    private static int DriverAddress(SidFile file)
    {
        int start = file.LoadAddress, end = file.LoadAddress + file.Data.Length;
        bool Free(int at) => at + DriverLength <= start || at >= end;

        if (file.FreePage is >= 0x04 and < 0xD0 && file.FreePages > 0 && Free(file.FreePage << 8))
            return file.FreePage << 8;
        for (int page = 0x04; page < 0xD0; page++)
        {
            if (page is < 0xA0 or > 0xBF && Free(page << 8))
                return page << 8;
        }
        throw new SidPlaybackException("The tune fills memory - there is no page left for a driver.");
    }

    /// <summary>The banking a PSID's code is called with: BASIC out of the way of a tune that loads
    /// under it, the KERNAL too for one under that, I/O always in.</summary>
    private static int Banking(SidFile file)
    {
        int end = file.LoadAddress + file.Data.Length;
        if (end > 0xE000 || file.LoadAddress >= 0xD000)
            return 0x35;
        if (end > 0xA000 && file.LoadAddress < 0xC000)
            return 0x36;
        return 0x37;
    }

    /// <summary>The banking a routine at <paramref name="address"/> is called with: the tune's, or
    /// all RAM for one in the I/O area's - as libsidplayfp calls it - which banks I/O in itself
    /// to reach the SID.</summary>
    private static int Banking(SidFile file, int address) => address is >= 0xD000 and < 0xE000 ? 0x34 : Banking(file);

    /// <summary>The driver's code, and where an interrupt coming straight through $FFFE enters it
    /// (-1 for a tune that plays from interrupts of its own).</summary>
    private static (byte[] Code, int RamEntry) DriverCode(SidFile file, int song, int at, Machine machine)
    {
        var code = new List<byte>();
        void Emit(params int[] bytes) => code.AddRange(bytes.Select(b => (byte)b));
        int Here() => at + code.Count;

        bool psid = !file.IsRsid && file.PlayAddress != 0;
        int bank = file.IsRsid ? 0x37 : Banking(file);
        int timer = KernalTimer(machine);

        // sei / lda #bank / sta $01
        Emit(0x78, 0xA9, bank, 0x85, 0x01);

        // CIA 1's timer A as the KERNAL sets it: its latch, interrupts on, started continuous.
        Emit(0xA9, timer & 0xFF, 0x8D, 0x04, 0xDC, 0xA9, timer >> 8, 0x8D, 0x05, 0xDC);
        Emit(0xA9, 0x81, 0x8D, 0x0D, 0xDC, 0xA9, 0x11, 0x8D, 0x0E, 0xDC);

        int ramEntry = -1;
        if (psid && !file.UsesCiaTimer(song))
        {
            // Played once a frame: CIA 1 quiet, the raster interrupt on line 0.
            Emit(0xA9, 0x7F, 0x8D, 0x0D, 0xDC, 0xAD, 0x0D, 0xDC);
            Emit(0xA9, 0x00, 0x8D, 0x12, 0xD0, 0xAD, 0x11, 0xD0, 0x29, 0x7F, 0x8D, 0x11, 0xD0);
            Emit(0xA9, 0x01, 0x8D, 0x1A, 0xD0);
        }

        // The IRQ vectors - through the KERNAL's $0314 and straight through $FFFE, for a tune that
        // banks the KERNAL out - are patched in below, once the handler's place is known.
        int vectorPatch = code.Count;
        if (psid)
            Emit(0xA9, 0, 0x8D, 0x14, 0x03, 0xA9, 0, 0x8D, 0x15, 0x03);

        // lda #song-1 / jsr init / cli / wait: jmp wait - init banked as its address asks, and the
        // tune's banking back for the driver's own interrupt.
        int initBank = file.IsRsid ? bank : Banking(file, file.InitAddress);
        if (initBank != bank)
            Emit(0xA9, initBank, 0x85, 0x01);
        Emit(0xA9, Math.Max(0, song - 1), 0x20, file.InitAddress & 0xFF, file.InitAddress >> 8);
        if (initBank != bank)
            Emit(0xA9, bank, 0x85, 0x01);
        Emit(0x58);
        int wait = Here();
        Emit(0x4C, wait & 0xFF, wait >> 8);

        if (psid)
        {
            // From $FFFE with the KERNAL out: save the registers as the KERNAL would have.
            ramEntry = Here();
            Emit(0x48, 0x8A, 0x48, 0x98, 0x48);

            // From $0314: acknowledge the VIC and CIA 1, call play with the tune's banking, restore.
            int irqAt = Here();
            Emit(0xA9, 0xFF, 0x8D, 0x19, 0xD0, 0xAD, 0x0D, 0xDC);
            Emit(0xA5, 0x01, 0x48, 0xA9, Banking(file, file.PlayAddress), 0x85, 0x01);
            Emit(0x20, file.PlayAddress & 0xFF, file.PlayAddress >> 8);
            Emit(0x68, 0x85, 0x01, 0x68, 0xA8, 0x68, 0xAA, 0x68, 0x40);

            code[vectorPatch + 1] = (byte)irqAt;
            code[vectorPatch + 6] = (byte)(irqAt >> 8);
        }

        if (code.Count > DriverLength)
            throw new InvalidOperationException("The driver outgrew its space.");
        return ([.. code], ramEntry);
    }
}
