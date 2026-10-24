using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Assembler.Tests;

/// <summary>
/// The complete memory image the editor builds - player and song data - against what c6510 produced
/// for the same song.
///
/// The comparison is against frozen output in Goldens\ rather than a fresh assembly, because the
/// assembler and the assembly player no longer live in this repository. See Goldens\README.md for
/// how those files were made and the one rule about changing them.
///
/// Comparing whole images and not just their lengths is the point of this file. Reversing the note
/// dictionary's build order - arpeggios before pattern rows, which is the one ordering decision
/// Startup\Tables.s bothered to explain - produces an image of exactly the right length with every
/// note wrong, and the length cross-checks further down stay green through it. Only the byte
/// comparison catches that, and it is the failure mode that would ship: a .sid that plays, at the
/// right tempo, with the right instruments, and the wrong tune.
/// </summary>
public class SongImageTests
{
    /// <summary>SongData sits at the repository root, five levels up from
    /// tests\X\bin\Debug\net8.0.</summary>
    private static string SongDataDirectory => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SongData"));

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>One set of goldens per hard-restart setting (see MagSid.Core.HardRestart), both
    /// c6510's own output, so whichever the player is set to is compared against the real thing.</summary>
    private static string GoldenDirectory => HardRestart.KeepsWaveform
        ? Path.Combine(AppContext.BaseDirectory, "Goldens", "GateOff")
        : Path.Combine(AppContext.BaseDirectory, "Goldens");

    /// <summary>The seven real songs. Between them they reach only two of the four feature
    /// combinations - five are bare, BellaCiao has vibrato - which is what the fixtures below are
    /// for.</summary>
    [Theory]
    [InlineData("Airstrike.s", "airstrike")]
    [InlineData("BellaCiao.s", "bellaciao")]
    [InlineData("Breakdance.s", "breakdance")]
    [InlineData("Lightforce.s", "lightforce")]
    [InlineData("Magnificent.s", "magnificent")]
    [InlineData("CityBomber4k(Megastyle).s", "megastyle")]
    [InlineData("SOS.s", "sos")]
    public void Whole_image_matches_what_c6510_produced(string songFile, string golden)
    {
        AssertMatchesGolden(Path.Combine(SongDataDirectory, songFile), songFile, golden, 0x1000);
    }

    /// <summary>Nothing in SongData uses SLIDE, so without these the slide code and the shared
    /// frequency-modulation block with both effects on would never be compared against anything. A
    /// gated feature that no song enables is how a broken vibrato declaration once passed a green
    /// suite.</summary>
    [Theory]
    [InlineData("slide_only.s", "slide_only")]
    [InlineData("slide_and_vib.s", "slide_and_vib")]
    public void Whole_image_matches_for_the_slide_fixtures(string songFile, string golden)
    {
        AssertMatchesGolden(Path.Combine(FixtureDirectory, songFile), songFile, golden, 0x1000);
    }

    /// <summary>No song in SongData swings either. Funk speed changes only the tick reload and adds
    /// the shuffle byte after everything else in zero page - after the slide state too, which is
    /// what the second fixture is for.</summary>
    [Theory]
    [InlineData("funk_bellaciao.s", "funk_bellaciao")]
    [InlineData("funk_slide.s", "funk_slide")]
    public void Whole_image_matches_for_the_funk_fixtures(string songFile, string golden)
    {
        AssertMatchesGolden(Path.Combine(FixtureDirectory, songFile), songFile, golden, 0x1000);

        var song = LoadSong(Path.Combine(FixtureDirectory, songFile), songFile);
        Assert.True(song.HasFunk);
        Assert.Equal(SongSizeCalculator.Measure(song).TotalBytes, SongImageBuilder.Build(song).Length);
    }

    /// <summary>Relocation on a whole real song rather than the toy program EmitterTests uses. This
    /// is what the export dialog's load-address field rests on.</summary>
    [Theory]
    [InlineData(0x2000, "bellaciao_2000")]
    [InlineData(0x4400, "bellaciao_4400")]
    public void Whole_image_matches_at_another_origin(int origin, string golden)
    {
        AssertMatchesGolden(
            Path.Combine(SongDataDirectory, "BellaCiao.s"), "BellaCiao.s", golden, origin);
    }

    /// <summary>PatternEncoder and SongSizeCalculator encode independently. The calculator is what
    /// the editor's live Song Length reads and what SongSizeTests pins, so the two must agree on
    /// every pattern - otherwise one of them is wrong and only the other would be noticed.</summary>
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

    private static void AssertMatchesGolden(string songPath, string what, string golden, int origin)
    {
        string goldenPath = Path.Combine(GoldenDirectory, golden + ".bin");
        Assert.True(File.Exists(goldenPath),
            $"Missing golden {golden}.bin. It is c6510's own output and cannot honestly be " +
            "regenerated from the code under test - see Goldens\\README.md.");

        byte[] reference = File.ReadAllBytes(goldenPath);
        byte[] mine = SongImageBuilder.Build(LoadSong(songPath, what), origin);

        Assert.True(mine.Length == reference.Length,
            $"{what} at ${origin:X4}: built {mine.Length} bytes, c6510 produced {reference.Length}.");

        for (int i = 0; i < reference.Length; i++)
        {
            if (mine[i] != reference[i])
            {
                Assert.Fail(
                    $"{what} at ${origin:X4}: first difference at ${origin + i:X4} (offset {i}) - " +
                    $"built ${mine[i]:X2}, c6510 has ${reference[i]:X2}." + Environment.NewLine +
                    $"  built: {Hex(mine, i)}" + Environment.NewLine +
                    $"  c6510: {Hex(reference, i)}");
            }
        }
    }

    private static Song LoadSong(string path, string name)
    {
        Assert.True(File.Exists(path),
            $"{name} is missing from {Path.GetDirectoryName(path)} - renamed or deleted? A song that " +
            "vanishes quietly is how this suite once stayed green over a renamed file.");

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
