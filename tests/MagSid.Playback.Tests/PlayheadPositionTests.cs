using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// The playhead has to move at the song's own pace - the same distance every frame, through notes,
/// holds and the boundaries between them. The sequencer reads a note and its holds as one event, so
/// its raw row index jumps a note at a time; these drive the real sequencer frame by frame and
/// check that the position worked out from its state does not.
/// </summary>
public class PlayheadPositionTests
{
    /// <summary>Notes of three different lengths, so the event boundaries fall unevenly: a note
    /// with two holds, one with one, one with two.</summary>
    private static Song Build(int speed)
    {
        var song = new Song { Speed = speed };
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x41, AttackDecay = 0x09, SustainRelease = 0xAA });

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), 1));
        pattern.Rows.Add(new HoldRow());
        pattern.Rows.Add(new HoldRow());
        pattern.Rows.Add(new NoteRow(NoteName.Parse("E-4"), 1));
        pattern.Rows.Add(new HoldRow());
        pattern.Rows.Add(new NoteRow(NoteName.Parse("G-4"), 1));
        pattern.Rows.Add(new HoldRow());
        pattern.Rows.Add(new HoldRow());
        song.Patterns.Add(pattern);

        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        return song;
    }

    /// <summary>Where channel 1 is after each of the first <paramref name="frames"/> frames, in
    /// rows, with the frame boundary as the instant asked about.</summary>
    private static List<double> Track(int speed, int frames)
    {
        var sequencer = new SongSequencer(Build(speed), (_, _) => { });
        var positions = new List<double>();

        for (int i = 0; i < frames; i++)
        {
            sequencer.Frame();
            var captured = sequencer.Capture();
            var (row, fraction) = PlayheadPosition.Of(captured.Channels[0], captured.SpeedCounter, speed, 0.0);
            positions.Add(row + fraction);
        }

        return positions;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(0)]
    public void The_playhead_moves_the_same_distance_every_frame(int speed)
    {
        int framesPerRow = speed + 1;
        int frames = 8 * framesPerRow;   // the whole pattern, stopping short of its wrap

        var positions = Track(speed, frames);

        for (int frame = 0; frame < frames; frame++)
        {
            double expected = frame / (double)framesPerRow;
            Assert.True(
                Math.Abs(positions[frame] - expected) < 1e-9,
                $"speed {speed}, frame {frame}: at {positions[frame]:F4}, expected {expected:F4}");
        }
    }

    /// <summary>The bug this replaces: the raw row index sits on the next event for the length of
    /// the current one. The worked-out row must name the row actually being heard.</summary>
    [Fact]
    public void Each_row_is_named_while_it_is_heard_not_the_event_after_it()
    {
        const int speed = 2;
        var sequencer = new SongSequencer(Build(speed), (_, _) => { });
        var heard = new List<int>();

        for (int i = 0; i < 8 * (speed + 1); i++)
        {
            sequencer.Frame();
            var captured = sequencer.Capture();
            heard.Add(PlayheadPosition.Of(captured.Channels[0], captured.SpeedCounter, speed, 0.0).Row);
        }

        // Three frames on each of the eight rows, in order - holds included.
        int[] expected = [.. Enumerable.Range(0, 8).SelectMany(row => Enumerable.Repeat(row, speed + 1))];
        Assert.Equal(expected, heard);
    }

    [Fact]
    public void Part_of_a_frame_moves_it_part_of_the_way()
    {
        var state = new SongSequencer.ChannelPlaybackState(0, 1, RowIndex: 3, 0, 0, Duration: 2);

        // Speed 3 is four frames to a row; half a frame in is an eighth of the row.
        var (row, fraction) = PlayheadPosition.Of(state, speedCounter: 3, speed: 3, subFrame: 0.5);

        Assert.Equal(0, row);
        Assert.Equal(0.125, fraction, 9);
    }
}
