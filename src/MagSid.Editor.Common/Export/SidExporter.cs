using System.Text;
using MagSid.Assembler;
using MagSid.Core;
using MagSid.Core.Model;
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
/// its own - it needs no assembler, no include library and no player sources beside it, which a
/// distributed .exe does not have. That is only defensible because MagSid.Assembler.Tests compares
/// the result byte for byte against what c6510 produced for every song and at more than one load
/// address. Those reference images are frozen in the test project, the assembler itself having been
/// dropped from the repository once it was no longer needed to build anything.
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

    public static byte[] Export(
        Song song, Metadata metadata, ChipModel chipModel, int loadAddress = SidLoadAddress.Default)
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
            memoryImage = SongImageBuilder.Build(exported, loadAddress);
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
}
