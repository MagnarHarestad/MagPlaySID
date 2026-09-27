using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Assembler.Tests;

/// <summary>
/// The whole point of the assembler project: the complete memory image the editor builds must be the
/// one c6510 builds, byte for byte, for every song.
///
/// PlayerImageTests already covers the code. This covers the song data, which is the half that fails
/// quietly - a mis-numbered note or a pool offset one out gives a .sid that plays at the right length
/// with the right instruments and the wrong notes. Nothing short of comparing the bytes finds that.
/// </summary>
public class SongImageTests
{
    private static string SongDataDirectory => Path.Combine(C6510.PlayerRoot, "SongData");

    private const string Fixtures = "../MagSID_Editor/tests/MagSid.Assembler.Tests/Fixtures/";

    [Theory]
    [InlineData("Airstrike.s", "export_airstrike.s")]
    [InlineData("BellaCiao.s", "export_bellaciao.s")]
    [InlineData("Breakdance.s", "export_breakdance.s")]
    [InlineData("Lightforce.s", "export_lightforce.s")]
    [InlineData("Magnificent.s", "export_magnificent.s")]
    [InlineData("CityBomber4k(Megastyle).s", "export_megastyle.s")]
    [InlineData("SOS.s", "export_sos.s")]
    public void Whole_image_is_byte_identical_to_c6510(string songFile, string driver)
    {
        AssertMatches(Path.Combine(SongDataDirectory, songFile), songFile, driver);
    }

    /// <summary>The SLIDE paths again - no real song uses them, so without these two the song-data
    /// side of slide_rate would go uncompared just as the player side would.</summary>
    [Theory]
    [InlineData("slide_only.s", Fixtures + "export_slide_only.s")]
    [InlineData("slide_and_vib.s", Fixtures + "export_slide_and_vib.s")]
    public void Whole_image_is_byte_identical_for_the_slide_fixtures(string songFile, string driver)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", songFile);
        AssertMatches(Path.GetFullPath(path), songFile, driver);
    }

    /// <summary>Relocation, end to end. The emitter's own relocation test covers a toy program; this
    /// one proves a whole real song moves, which is what the export dialog now lets a user ask
    /// for.</summary>
    [Theory]
    [InlineData(0x2000)]
    [InlineData(0x4400)]
    public void Whole_image_is_byte_identical_to_c6510_at_another_origin(int origin)
    {
        // export_sid.s is the only checked-in driver that takes an origin, and it reads
        // SongData\Patterns.s - so this uses the fixture driver's own trick instead and points a
        // relocatable driver at BellaCiao.
        var song = LoadSong(Path.Combine(SongDataDirectory, "BellaCiao.s"), "BellaCiao.s");
        var (reference, _) = C6510.AssembleDriver(Fixtures + "export_bellaciao_reloc.s", origin);

        AssertImagesMatch(SongImageBuilder.Build(song, origin), reference, $"BellaCiao at ${origin:X4}", origin);
    }

    /// <summary>PatternEncoder and SongSizeCalculator encode independently; the calculator is the one
    /// pinned to c6510 by SongSizeTests and read by the editor's live Song Length. They have to agree
    /// on every pattern, or one of them is wrong and only the other would be noticed.</summary>
    [Theory]
    [InlineData("Airstrike.s")]
    [InlineData("BellaCiao.s")]
    [InlineData("Breakdance.s")]
    [InlineData("Lightforce.s")]
    [InlineData("Magnificent.s")]
    [InlineData("CityBomber4k(Megastyle).s")]
    [InlineData("SOS.s")]
    public void Encoded_pattern_lengths_agree_with_SongSizeCalculator(string songFile)
    {
        var song = LoadSong(Path.Combine(SongDataDirectory, songFile), songFile);
        var notes = NoteDictionary.Build(song);

        foreach (var pattern in song.Patterns)
        {
            Assert.Equal(
                SongSizeCalculator.EncodedPatternLength(pattern.Rows),
                PatternEncoder.Encode(pattern, notes).Length);
        }
    }

    /// <summary>And the totals, which is the figure the editor shows and the one pinned against real
    /// builds. If the image is the right bytes it is the right length too, but a mismatch here says
    /// immediately which of the two models moved.</summary>
    [Theory]
    [InlineData("Airstrike.s")]
    [InlineData("BellaCiao.s")]
    [InlineData("Breakdance.s")]
    [InlineData("Lightforce.s")]
    [InlineData("Magnificent.s")]
    [InlineData("CityBomber4k(Megastyle).s")]
    [InlineData("SOS.s")]
    public void Image_length_agrees_with_SongSizeCalculator(string songFile)
    {
        var song = LoadSong(Path.Combine(SongDataDirectory, songFile), songFile);

        Assert.Equal(
            SongSizeCalculator.Measure(song).TotalBytes,
            SongImageBuilder.Build(song).Length);
    }

    private static void AssertMatches(string songPath, string songFile, string driver)
    {
        var song = LoadSong(songPath, songFile);
        var (reference, _) = C6510.AssembleDriver(driver);

        AssertImagesMatch(SongImageBuilder.Build(song), reference, songFile, 0x1000);
    }

    private static void AssertImagesMatch(byte[] mine, byte[] reference, string what, int origin)
    {
        Assert.True(mine.Length == reference.Length,
            $"{what}: built {mine.Length} bytes, c6510 produced {reference.Length}.");

        for (int i = 0; i < reference.Length; i++)
        {
            if (mine[i] != reference[i])
            {
                Assert.Fail(
                    $"{what}: first difference at ${origin + i:X4} (offset {i}) - built " +
                    $"${mine[i]:X2}, c6510 has ${reference[i]:X2}." + Environment.NewLine +
                    $"  built: {Hex(mine, i)}" + Environment.NewLine +
                    $"  c6510: {Hex(reference, i)}");
            }
        }
    }

    private static Song LoadSong(string path, string name)
    {
        Assert.True(File.Exists(path),
            $"{name} is missing from {Path.GetDirectoryName(path)} - renamed or deleted? Update this " +
            "test and whichever export driver names it.");

        return PatternsFileReader.Parse(File.ReadAllText(path), name);
    }

    private static string Hex(byte[] bytes, int around)
    {
        int start = Math.Max(0, around - 4);
        int end = Math.Min(bytes.Length, around + 8);
        return string.Join(" ", Enumerable.Range(start, end - start)
            .Select(i => i == around ? $"[{bytes[i]:X2}]" : $"{bytes[i]:X2}"));
    }
}
