using MagSid.Core.Model;

namespace MagSid.Playback;

/// <summary>
/// Plays a song through with no audio device and no SID at all - just the sequencer, stepped frame
/// by frame - to find out which program steps it actually reaches.
///
/// This exists so trimming a program does not depend on the user having listened to the whole song.
/// Coverage gathered from a live play-through is only as complete as that play-through was, and a
/// pass that stopped early reports steps as unreached that the song genuinely uses - which is
/// exactly the evidence you must not act on. Running it here instead makes the answer complete by
/// construction, and takes milliseconds.
/// </summary>
public static class SongCoverageAnalyzer
{
    /// <summary>Enough for a very long song at 50Hz (about 33 minutes) while still bounding a
    /// song whose order lists never line up - the analysis stops as soon as every channel has
    /// been round its own order list once, which is normally far sooner.</summary>
    private const int DefaultFrameCap = 100_000;

    /// <summary>
    /// Coverage indexed by instrument number, for one full pass of every channel's order list.
    ///
    /// One pass each is enough rather than a full musical cycle: the three channels have
    /// independent order lengths (Lightforce's channel 3 runs its whole order twice per pass of
    /// the others), but every row of every pattern a channel plays is visited within its own
    /// pass, and a row's note length is fixed - so each note exercises its instrument's programs
    /// the same way on every repeat.
    /// </summary>
    public static IReadOnlyList<ProgramCoverage> Run(Song song, int frameCap = DefaultFrameCap) =>
        Pass(song, frameCap, out _).AllCoverage;

    /// <summary>How many frames one full pass of every channel's order list takes - the same
    /// stopping rule as <see cref="Run"/>, for anything else that wants to cover the whole song
    /// once, such as measuring the player's rastertime.</summary>
    public static int PassFrames(Song song, int frameCap = DefaultFrameCap)
    {
        Pass(song, frameCap, out int frames);
        return frames;
    }

    private static SongSequencer Pass(Song song, int frameCap, out int frames)
    {
        var sequencer = new SongSequencer(song, static (_, _) => { });

        // Counting order entries, not watching OrderPos: that one only moves when a pattern
        // ends, so "back where it started" is trivially true for the whole of the first pattern
        // and a stop condition built on it fires on frame one. Entering every entry is not
        // enough either - the last one has to finish - so this waits for one more advance than
        // there are entries.
        var target = new int[3];
        var start = new int[3];
        for (int ch = 0; ch < 3; ch++)
        {
            start[ch] = sequencer.OrderEntriesStarted(ch);
            target[ch] = song.ChannelOrders[ch].Tokens.Count + 1;
        }

        frames = frameCap;
        for (int frame = 0; frame < frameCap; frame++)
        {
            sequencer.Frame();

            bool complete = true;
            for (int ch = 0; ch < 3; ch++)
            {
                if (sequencer.OrderEntriesStarted(ch) - start[ch] < target[ch])
                {
                    complete = false;
                    break;
                }
            }

            if (complete)
            {
                frames = frame + 1;
                break;
            }
        }

        return sequencer;
    }
}
