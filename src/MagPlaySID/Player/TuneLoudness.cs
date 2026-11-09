using MagPlay.Amiga;
using MagSid.Playback;

namespace MagPlaySid.Player;

/// <summary>
/// How loud each kind of tune is played: a .sid and each kind of module raised by its own gain, so
/// that on average it comes out at <see cref="TargetLufs"/>, and limited to a true peak of
/// <see cref="CeilingDb"/>. A recording plays as it was mastered.
/// </summary>
internal static class TuneLoudness
{
    public const double TargetLufs = -11;
    public const double CeilingDb = -0.3;

    /// <summary>The gains, in dB, that bring each kind to <see cref="TargetLufs"/> on average through
    /// the limiter, as measured on 156 .sid tunes (150 picked at random from HVSC), 84 ProTracker
    /// modules, 44 XM, 5 IT and 3 S3M. FastTracker's and Impulse Tracker's own mixing leaves
    /// headroom for many channels, so theirs are the largest and the limiter does the most.</summary>
    public const double SidGainDb = 5;
    public const double ModGainDb = 8;
    public const double S3mGainDb = 7.5;
    public const double XmGainDb = 13.5;
    public const double ItGainDb = 13.5;

    public static double ModuleGainDb(ITrackerModule module) => module.Format switch
    {
        "S3M" => S3mGainDb,
        "XM" => XmGainDb,
        "IT" => ItGainDb,
        _ => ModGainDb,
    };

    public static PeakLimiter Limiter(int channels, int sampleRate, double gainDb) =>
        new(channels, sampleRate, CeilingDb) { GainDb = gainDb };
}
