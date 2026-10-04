using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Playback.Tests;

/// <summary>
/// A position's frame count is how the live rastertime lines the 6502 player up with what is
/// heard: it replays that many play calls from the top before running alongside. So it has to
/// count the music from its top, through resumes, and a position found by seeking has to be the
/// frame the song really reaches it on.
/// </summary>
public class SequencerFrameTests
{
    private static Song Lightforce() => PatternsFileReader.Read(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SongData", "Lightforce.s")));

    [Fact]
    public void The_frame_count_runs_from_the_top_and_carries_through_a_resume()
    {
        var song = Lightforce();
        var sequencer = new SongSequencer(song, (_, _) => { });
        for (int i = 0; i < 100; i++)
            sequencer.Frame();

        var at100 = sequencer.Capture();
        Assert.Equal(100, at100.Frame);

        var resumed = new SongSequencer(song, (_, _) => { }, at100);
        for (int i = 0; i < 50; i++)
            resumed.Frame();

        Assert.Equal(150, resumed.Capture().Frame);
    }

    [Fact]
    public void A_position_found_by_seeking_is_reached_on_its_own_frame()
    {
        var song = Lightforce();
        var found = SongSequencer.FindPosition(song, channel: 2, orderIndex: 5, rowIndex: 16);
        Assert.NotNull(found);
        Assert.True(found!.Frame > 0);

        var sequencer = new SongSequencer(song, (_, _) => { });
        for (int i = 0; i < found.Frame; i++)
            sequencer.Frame();

        var there = sequencer.Capture();
        Assert.Equal(found.SpeedCounter, there.SpeedCounter);
        Assert.Equal(found.Channels, there.Channels);
    }
}
