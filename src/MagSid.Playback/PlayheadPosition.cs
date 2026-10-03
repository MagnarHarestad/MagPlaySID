namespace MagSid.Playback;

/// <summary>
/// Where in a pattern a channel is, to a fraction of a row.
///
/// The sequencer does not keep that directly. It reads a note together with the holds after it as
/// one event - the same run-length collapsing the assembled player does - so its row index jumps a
/// whole note at a time and points past the event it is playing, not at it. Drawn as is, that
/// gives a playhead that stands on a note for its full length and then leaps.
///
/// Everything needed is in the captured state, though. Once an event is read, <c>RowIndex</c> is
/// the row after its last hold and <c>Duration</c> counts the holds still to come, so the row being
/// heard is <c>RowIndex - 1 - Duration</c>. How far into that row is the tick countdown: a row lasts
/// <c>Speed + 1</c> frames, and <c>Speed - SpeedCounter</c> of them have passed. The sub-frame part
/// is the caller's, from the audio clock. So the line follows the song's own speed exactly, with
/// nothing estimated.
/// </summary>
public static class PlayheadPosition
{
    /// <param name="channel">The channel as captured at the start of the frame being heard.</param>
    /// <param name="speedCounter">The sequencer's tick countdown in the same capture.</param>
    /// <param name="speed">The song's speed: a row lasts this many frames plus one.</param>
    /// <param name="subFrame">How far into that frame the listener is, 0 to 1.</param>
    /// <returns>The pattern row being heard, and how far through it, 0 to 1.</returns>
    public static (int Row, double Fraction) Of(
        SongSequencer.ChannelPlaybackState channel, int speedCounter, int speed, double subFrame)
    {
        int row = Math.Max(0, channel.RowIndex - 1 - channel.Duration);

        int framesPerRow = Math.Max(1, speed + 1);

        // Before the first tick the countdown sits at -1, which would read as more than a whole
        // row gone. Nothing has played yet; the row is simply at its start.
        int framesGone = Math.Clamp(speed - speedCounter, 0, framesPerRow - 1);

        double fraction = (framesGone + Math.Clamp(subFrame, 0.0, 1.0)) / framesPerRow;
        return (row, Math.Clamp(fraction, 0.0, 1.0));
    }
}
