using MagSid.Assembler;
using MagSid.Core.Model;

namespace MagSid.Editor.App.Player;

/// <summary>
/// The rastertime of the frames being heard, while the song plays. The editor plays through the C#
/// sequencer, not the 6502 player, so this keeps the player's own 6502 code running alongside: one
/// play call for every frame the song has played, from the top - which on a start mid-song means
/// winding it forward first, in the background, since the 6502 player can only begin at init.
///
/// Called from the UI thread as the audio clock moves; between two calls it plays whatever frames
/// went by, usually one to three. What it reports is the dearest of the last few frames, so a
/// spike lasting one frame is still on screen long enough to be read.
/// </summary>
internal sealed class LiveRasterTime
{
    /// <summary>About a fifth of a second of frames - long enough to read, short enough to follow
    /// the music.</summary>
    private const int HoldFrames = 10;

    /// <summary>The most frames played in one call. A UI that stalled for longer than this simply
    /// skips ahead rather than freezing again to catch up.</summary>
    private const int MaxStepPerCall = 500;

    private readonly EmulatedPlayer _player;
    private readonly long[] _recent = new long[HoldFrames];
    private readonly Task _windForward;
    private int _frame;
    private int _recentCount;
    private int _recentNext;

    /// <param name="song">A copy - the player is built once, from the song as it stood.</param>
    /// <param name="startFrame">The frame playback starts from, counted from the top.</param>
    public LiveRasterTime(Song song, int loadAddress, int startFrame)
    {
        _player = new EmulatedPlayer(song, loadAddress);
        _windForward = Task.Run(() =>
        {
            _player.Init();
            for (int i = 0; i < startFrame; i++)
                _player.Play();
            _frame = startFrame;
        });
    }

    /// <summary>Plays the 6502 player up to <paramref name="heardFrame"/> - the frames the song
    /// has played at the instant being heard - and returns the dearest recent play call, or null
    /// while it is still winding forward or has nothing yet.</summary>
    public long? Advance(int heardFrame)
    {
        if (!_windForward.IsCompleted)
            return null;

        if (heardFrame - _frame > MaxStepPerCall)
            _frame = heardFrame - MaxStepPerCall;

        while (_frame < heardFrame)
        {
            _recent[_recentNext] = _player.Play();
            _recentNext = (_recentNext + 1) % HoldFrames;
            _recentCount = Math.Min(HoldFrames, _recentCount + 1);
            _frame++;
        }

        if (_recentCount == 0)
            return null;

        long peak = 0;
        for (int i = 0; i < _recentCount; i++)
            peak = Math.Max(peak, _recent[i]);
        return peak;
    }
}
