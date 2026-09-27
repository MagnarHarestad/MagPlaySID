using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Playback.Tests;

/// <summary>
/// The analyzer decides which program steps Optimize is allowed to delete, so the failure that
/// matters is stopping too early: a pass that ends before every pattern has been visited reports
/// steps as unreached that the song genuinely plays, and those would be cut. Each of these
/// compares its answer against simply running the song far longer.
/// </summary>
public class SongCoverageAnalyzerTests
{
    private static readonly string SongDataDirectory =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "SongData"));

    private static List<(int Instrument, string Program, int Step)> Reached(
        IReadOnlyList<ProgramCoverage> coverage)
    {
        var result = new List<(int, string, int)>();
        for (int instrument = 0; instrument < coverage.Count; instrument++)
        {
            for (int step = 0; step < ProgramCoverage.MaxSteps; step++)
            {
                if (coverage[instrument].Wavetable[step]) result.Add((instrument, "wave", step));
                if (coverage[instrument].Arpeggio[step]) result.Add((instrument, "arp", step));
                if (coverage[instrument].Filter[step]) result.Add((instrument, "filter", step));
            }
        }
        return result;
    }

    private static IReadOnlyList<ProgramCoverage> BruteForce(Song song, int frames)
    {
        var sequencer = new SongSequencer(song, static (_, _) => { });
        for (int i = 0; i < frames; i++)
            sequencer.Frame();
        return sequencer.AllCoverage;
    }

    [Theory]
    [InlineData("BellaCiao.s")]
    [InlineData("Breakdance.s")]
    [InlineData("Lightforce.s")]
    [InlineData("Magnificent.s")]
    [InlineData("Megastyle.s")]
    [InlineData("Airstrike.s")]
    [InlineData("SOS.s")]
    public void One_pass_finds_everything_a_much_longer_run_does(string file)
    {
        string path = Path.Combine(SongDataDirectory, file);
        if (!File.Exists(path)) return;   // sibling asm project not checked out

        var song = PatternsFileReader.Parse(File.ReadAllText(path), file);

        var analyzed = Reached(SongCoverageAnalyzer.Run(song));
        // 30000 frames is ten minutes of song at 50Hz - many times round every order list.
        var exhaustive = Reached(BruteForce(PatternsFileReader.Parse(File.ReadAllText(path), file), 30_000));

        Assert.Equal(exhaustive, analyzed);
    }

    [Fact]
    public void A_pattern_only_reached_late_in_the_order_is_still_covered()
    {
        // Guards the stop condition directly: instrument 2 appears only in the last pattern of
        // channel 1's order, so an analyzer that stopped after the first pattern would miss it.
        var song = new Song { Speed = 0 };
        for (int n = 1; n <= 2; n++)
            song.Instruments.Add(new Instrument { Number = n, Waveform = 0x41 });
        song.Instruments[1].Wavetable.AddRange([0x81, 0x41, 0x21]);

        for (int p = 1; p <= 4; p++)
        {
            var pattern = new Pattern { Number = p };
            pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), p == 4 ? 2 : 1));
            pattern.Rows.Add(new HoldRow());
            pattern.Rows.Add(new HoldRow());
            song.Patterns.Add(pattern);
        }

        foreach (var order in song.ChannelOrders)
            for (int p = 1; p <= 4; p++)
                order.Tokens.Add(new PatternRefToken(p));

        var coverage = SongCoverageAnalyzer.Run(song);

        Assert.True(coverage[2].Wavetable[0]);
        Assert.True(coverage[2].Wavetable[2]);
    }
}
