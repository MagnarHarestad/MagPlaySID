using MagSid.Core;
using MagSid.Core.Serialization;

namespace MagSid.Core.Tests;

/// <summary>
/// Every figure here came from assembling that song with c6510 and reading the resulting image,
/// not from running the calculator. That is the entire point: SongSizeCalculator is a hand-written
/// mirror of Startup\Tables.s, so the only thing keeping the two honest is a set of totals taken
/// from the assembler itself. If a change to the emitter, the pools or the player's own size moves
/// a song, one of these fails and says by how much.
///
/// Refresh them with, from MAGSIDv7_LowMemory:
///   c6510.exe -p ..\..\..\include\ -s out.prg $1000 + export_&lt;song&gt;.s -y out.sym
/// and take the file size minus its two-byte load address.
/// </summary>
public class SongSizeTests
{
    private static readonly string SongDataDirectory =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "SongData"));

    [Theory]
    [InlineData("Airstrike.s", 894)]
    [InlineData("BellaCiao.s", 1043)]
    [InlineData("Breakdance.s", 864)]
    [InlineData("Lightforce.s", 1006)]
    [InlineData("Magnificent.s", 1137)]
    [InlineData("CityBomber4k(Megastyle).s", 751)]
    [InlineData("SOS.s", 877)]
    public void Reported_size_matches_what_c6510_actually_assembles(string file, int assembledBytes)
    {
        // Only the whole SongData folder being absent counts as "not checked out". A folder that
        // exists but has lost this one file means it was renamed or deleted, and skipping that
        // silently is how Megastyle.s got renamed with the test staying green.
        if (!Directory.Exists(SongDataDirectory)) return;

        string path = Path.Combine(SongDataDirectory, file);
        Assert.True(File.Exists(path),
            $"{file} is missing from SongData - renamed or deleted? Update this test and whichever "
            + "export_*.s driver names it.");

        var song = PatternsFileReader.Parse(File.ReadAllText(path), file);
        var size = SongSizeCalculator.Measure(song);

        // The figures are c6510's for the $08 hard restart. The player that keeps the waveform
        // instead assembles exactly two bytes larger for every song - see the GateOff goldens.
        Assert.Equal(assembledBytes + (HardRestart.KeepsWaveform ? 2 : 0), size.TotalBytes);
    }

    [Fact]
    public void The_parts_add_up_to_the_whole()
    {
        string path = Path.Combine(SongDataDirectory, "BellaCiao.s");
        if (!File.Exists(path)) return;   // sibling asm project not checked out

        var song = PatternsFileReader.Parse(File.ReadAllText(path), "BellaCiao.s");
        var size = SongSizeCalculator.Measure(song);

        Assert.Equal(size.SongDataBytes + size.PlayerBytes, size.TotalBytes);
        Assert.Equal(0x1000 + size.TotalBytes - 1, size.EndAddress);
    }

    [Fact]
    public void An_instrument_using_vibrato_pulls_the_vibrato_code_into_the_player()
    {
        // The player is assembled without any vibrato code or tables when nothing uses them,
        // so adding it to one instrument has to move the total by more than the two table bytes.
        string path = Path.Combine(SongDataDirectory, "Megastyle.s");
        if (!File.Exists(path)) return;   // sibling asm project not checked out

        var song = PatternsFileReader.Parse(File.ReadAllText(path), "Megastyle.s");
        int before = SongSizeCalculator.Measure(song).TotalBytes;

        song.Instruments[0].VibratoDelaySpeed = 0x32;
        song.Instruments[0].VibratoDepth = 0x10;
        int after = SongSizeCalculator.Measure(song).TotalBytes;

        Assert.Equal(112 + 2 * song.Instruments.Count, after - before);
    }
}
