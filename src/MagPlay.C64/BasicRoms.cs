using System.Reflection;

namespace MagPlay.C64;

/// <summary>
/// BASIC for the RSIDs that are BASIC programs: Microsoft's 6502 BASIC - the source Microsoft
/// published under the MIT license, of which the C64's BASIC V2 is a version - with the C64's
/// changes, each routine at the address the C64 has it, so that a program costs the cycles it does
/// on a C64 and its tempo is the C64's. With it, KERNAL routines a BASIC program uses - the
/// interrupt's clock and keyboard, the screen editor, the stop key, GET - taking the C64's cycles
/// too. Built from Basic/ by build_roms.py; see Basic/README.md.
///
/// Only a BASIC tune's machine has these; every other tune keeps the stand-in KERNAL alone.
/// </summary>
internal static class BasicRoms
{
    /// <summary>Where the KERNAL routines start a BASIC tune: the chips, the vectors and the screen
    /// as a reset leaves them, BASIC's cold start, the program - in memory already, its end at
    /// $AE - back in after NEW, and RUN.</summary>
    public const int Start = 0xF72C;

    /// <summary>NEWSTT, where BASIC goes on to its next statement - $A7AE, as on a C64.</summary>
    public const int NextStatement = 0xA7AE;

    private static readonly byte[] Basic = Load("MagPlay.C64.Basic.bin");
    private static readonly byte[] KernalRuns = Load("MagPlay.C64.BasicKernal.bin");

    private static byte[] Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is missing from MagPlay.C64.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>BASIC into <paramref name="computer"/>'s BASIC ROM, and its last part and the KERNAL
    /// routines over the stand-in KERNAL.</summary>
    public static void Install(C64 computer)
    {
        Basic.CopyTo(computer.BasicRom, 0);
        var kernal = computer.KernalRom;
        for (int at = 0; at + 4 <= KernalRuns.Length;)
        {
            int address = KernalRuns[at] | (KernalRuns[at + 1] << 8);
            int length = KernalRuns[at + 2] | (KernalRuns[at + 3] << 8);
            Array.Copy(KernalRuns, at + 4, kernal, address - 0xE000, length);
            at += 4 + length;
        }
    }
}
