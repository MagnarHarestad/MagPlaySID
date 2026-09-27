using System.Diagnostics;
using System.Text;
using MagSid.Assembler;
using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Sid;

namespace MagSid.Editor.App.Export;

public sealed class SidExportException : Exception
{
    public SidExportException(string message) : base(message)
    {
    }
}

/// <summary>
/// Builds a playable PSID (.sid) file out of the song currently open in the editor.
///
/// The image is built in C# by <see cref="SongImageBuilder"/>, so a copy of the editor exports on
/// its own - it no longer needs c6510.exe, the include library or the player sources beside it,
/// which a distributed .exe does not have. That is only defensible because the C# build is compared
/// byte for byte against what c6510 produces from the real Code\musicplayer.s and Startup\Tables.s,
/// for every song and at more than one load address, in MagSid.Assembler.Tests.
///
/// The old path is still here behind <c>useExternalAssembler</c>, as the cross-check that keeps the
/// two honest. Play in VSID and the demo build are unaffected either way.
///
/// What comes out is one self-contained memory segment with music.init/music.play at its head, which
/// is exactly the PSID convention: entry points an external driver calls, no on-chip IRQ setup. The
/// segment is relocatable, so the load address is the caller's choice - the labels are resolved
/// against it rather than patched afterwards, which is what keeps the player's absolute references
/// into its own tables correct.
/// </summary>
public static class SidExporter
{
    /// <summary>music.play sits three bytes past music.init, which is the load address itself -
    /// the two JMPs at the top of the player, in that order.</summary>
    private const int PlayEntryOffset = 0x3;

    public sealed record Metadata(string Name, string Author, string Released);

    /// <summary>Locates VICE's dedicated SID player, for auditioning an exported file
    /// without leaving the editor.</summary>
    public static string LocateVsidExecutable()
    {
        string playerRoot = LocatePlayerProjectRoot();
        string vsidPath = Path.GetFullPath(Path.Combine(playerRoot, "..", "..", "..", "bin", "win_x86", "vsid.exe"));
        if (!File.Exists(vsidPath))
            throw new SidExportException($"VICE's vsid.exe not found: {vsidPath}");

        return vsidPath;
    }

    /// <param name="loadAddress">Where the tune loads, and therefore where init/play are. The
    /// player is relocated by reassembling at this origin rather than by patching, so any address
    /// <see cref="SidLoadAddress.Validate"/> accepts produces a byte-identical tune bar the
    /// addresses themselves.</param>
    /// <param name="useExternalAssembler">Assembles through c6510 instead of building the image in
    /// C#. The two produce identical bytes for every song in SongData, so this exists to settle an
    /// argument rather than because either is the real one - but it needs the assembler and the
    /// player sources beside the editor, which a distributed copy does not have.</param>
    public static byte[] Export(
        Song song, Metadata metadata, ChipModel chipModel,
        int loadAddress = SidLoadAddress.Default, bool useExternalAssembler = false)
    {
        // Patterns no channel plays any more are pruned on the way out: dropping a song-order entry
        // deliberately leaves its pattern in the pool so typing the number again brings the data
        // back, but carrying that dead weight into the C64 binary would cost bytes for something
        // that can never sound. WithUnusedPatternsRemoved returns a copy, so the open song is
        // untouched.
        var exported = song.WithUnusedPatternsRemoved();

        byte[] memoryImage;
        try
        {
            memoryImage = useExternalAssembler
                ? AssembleWithC6510(exported, loadAddress)
                : SongImageBuilder.Build(exported, loadAddress);
        }
        catch (SongTooLargeException ex)
        {
            // A real "this song does not fit the format", not a bug - the same limits Tables.s
            // asserts. Worth showing in the user's own terms rather than as a crash.
            throw new SidExportException(ex.Message);
        }

        // Re-checked here and not only in the dialog: Export is callable on its own, and a tune that
        // runs into I/O or off the top of memory is worth refusing rather than writing a .sid that
        // cannot play.
        if (SidLoadAddress.Validate(loadAddress, memoryImage.Length) is { } problem)
            throw new SidExportException(problem);

        return BuildPsid(memoryImage, metadata, chipModel, loadAddress);
    }

    /// <summary>The original path: overwrite SongData\Patterns.s, run c6510 over export_sid.s, read
    /// the .prg back. Kept because it is what proves the C# build right - but it mutates checked-in
    /// source and needs the whole build tree, which is why it is no longer the default.</summary>
    private static byte[] AssembleWithC6510(Song song, int loadAddress)
    {
        string playerRoot = LocatePlayerProjectRoot();
        string patternsPath = Path.Combine(playerRoot, "SongData", "Patterns.s");
        string driverPath = Path.Combine(playerRoot, "export_sid.s");
        string assemblerPath = Path.GetFullPath(Path.Combine(playerRoot, "..", "..", "..", "bin", "win_x86", "c6510.exe"));

        if (!File.Exists(driverPath))
            throw new SidExportException($"Missing export driver: {driverPath}");
        if (!File.Exists(assemblerPath))
            throw new SidExportException($"Assembler not found: {assemblerPath}");

        string? originalPatternsText = File.Exists(patternsPath) ? File.ReadAllText(patternsPath) : null;

        string tag = Guid.NewGuid().ToString("N");
        string tempPrgPath = Path.Combine(playerRoot, $"_export_{tag}.prg");
        string tempSymPath = Path.Combine(playerRoot, $"_export_{tag}.sym");

        try
        {
            // Tables.s reads its song data from this exact, hardcoded relative path - swap in the
            // song being exported, build, then always restore what was there before, whether or not
            // the assembly below succeeds. The song arrives already pruned.
            PatternsFileWriter.Write(song, patternsPath);

            RunAssembler(assemblerPath, playerRoot, tempPrgPath, tempSymPath, loadAddress);

            byte[] prgBytes = File.ReadAllBytes(tempPrgPath);
            if (prgBytes.Length < 2 || prgBytes[0] != (loadAddress & 0xFF) || prgBytes[1] != (loadAddress >> 8))
            {
                throw new SidExportException(
                    $"Assembled .prg has an unexpected load address (expected ${loadAddress:X4}).");
            }

            return prgBytes[2..];
        }
        finally
        {
            if (originalPatternsText is not null)
                File.WriteAllText(patternsPath, originalPatternsText);

            TryDelete(tempPrgPath);
            TryDelete(tempSymPath);
        }
    }

    private static void RunAssembler(
        string assemblerPath, string playerRoot, string tempPrgPath, string tempSymPath, int loadAddress)
    {
        var psi = new ProcessStartInfo
        {
            FileName = assemblerPath,
            WorkingDirectory = playerRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("../../../include/");
        // export_sid.s defaults to $1000 behind an "if !def" guard, so this declaration is what
        // moves the whole segment. The -s start address has to agree with it or the saved block
        // would begin somewhere the assembler put nothing.
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add($"v_export_loc=${loadAddress:X4}");
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(tempPrgPath);
        psi.ArgumentList.Add($"${loadAddress:X4}");
        psi.ArgumentList.Add("+");
        psi.ArgumentList.Add("export_sid.s");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(tempSymPath);

        using var process = Process.Start(psi) ?? throw new SidExportException("Failed to start the assembler process.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(tempPrgPath))
        {
            throw new SidExportException(
                $"Assembly failed (exit code {process.ExitCode}).\n\n{stdout}\n{stderr}".Trim());
        }
    }

    private static byte[] BuildPsid(byte[] memoryImage, Metadata metadata, ChipModel chipModel, int loadAddress)
    {
        const int headerSize = 0x7C;
        var header = new byte[headerSize];

        Encoding.ASCII.GetBytes("PSID").CopyTo(header, 0x00);
        PutBigEndian16(header, 0x04, 2);            // version 2
        PutBigEndian16(header, 0x06, headerSize);   // dataOffset
        PutBigEndian16(header, 0x08, loadAddress);
        PutBigEndian16(header, 0x0A, loadAddress);
        PutBigEndian16(header, 0x0C, loadAddress + PlayEntryOffset);
        PutBigEndian16(header, 0x0E, 1);             // songs
        PutBigEndian16(header, 0x10, 1);             // startSong
        // speed left at 0: play is driven once per vertical blank (50Hz PAL), matching how
        // music.play is already called once per frame by the real player's raster IRQ.
        PutAsciiField(header, 0x16, 32, metadata.Name);
        PutAsciiField(header, 0x36, 32, metadata.Author);
        PutAsciiField(header, 0x56, 32, metadata.Released);

        // flags: bit0 musPlayer=0 (built-in player), bit1 psidSpecific=0, bits2-3 clock=PAL(01),
        // bits4-5 sidModel1 - MOS6581=01, MOS8580=10 (PSID v2NG) - previously hardcoded to
        // MOS6581 regardless of what the editor's own toolbar had selected, so an export could
        // audibly disagree with in-editor playback the moment someone switched to 8580.
        int sidModelBits = chipModel == ChipModel.Mos8580 ? 0b10 : 0b01;
        int flags = (0b01 << 2) | (sidModelBits << 4);
        PutBigEndian16(header, 0x76, flags);
        // startPage/pageLength left at 0: no relocation info supplied.

        var result = new byte[headerSize + memoryImage.Length];
        header.CopyTo(result, 0);
        memoryImage.CopyTo(result, headerSize);
        return result;
    }

    private static void PutBigEndian16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static void PutAsciiField(byte[] buffer, int offset, int length, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        int count = Math.Min(bytes.Length, length - 1); // leave a trailing null
        Array.Copy(bytes, 0, buffer, offset, count);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a scratch file - leaving it behind isn't harmful.
        }
    }

    private static string LocatePlayerProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MagSid.sln")))
            dir = dir.Parent;

        if (dir?.Parent is null)
        {
            throw new SidExportException(
                "Could not locate the MagSID Editor solution root to find the sibling MAGSIDv7_LowMemory project.");
        }

        string candidate = Path.Combine(dir.Parent.FullName, "MAGSIDv7_LowMemory");
        if (!Directory.Exists(candidate))
            throw new SidExportException($"Expected sibling assembly project at {candidate}, but it was not found.");

        return candidate;
    }
}
