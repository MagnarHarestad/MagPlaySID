using MagSid.Audio.PortAudio;
using MagSid.Playback;
using NAudio.Wave;

// magsid-sidplay - plays a .sid through PortAudio from a terminal, on any system the engine runs
// on. The first step of the cross-platform port: it proves the engine and the sound card path
// before there is a window to put them in.
//
//   magsid-sidplay <file.sid> [seconds] [--song N] [--mute]

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("usage: magsid-sidplay <file.sid> [seconds] [--song N] [--mute]");
    return 1;
}

string path = args[0];
int seconds = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 60;
int? song = Array.IndexOf(args, "--song") is var at and >= 0 && at + 1 < args.Length ? int.Parse(args[at + 1]) : null;
bool mute = args.Contains("--mute");

try
{
    var engine = SidFilePlaybackEngine.FromFile(path, song: song);
    var file = engine.File;
    Console.WriteLine($"{file.Name} - {file.Author} - {file.Released}");
    Console.WriteLine($"song {engine.Song} of {file.Songs}, {(file.UsesCiaTimer(engine.Song) ? "CIA timer" : "50Hz")}");

    AudioOutput.Open = PortAudioOutput.Open;
    using var output = AudioOutput.Start(mute ? new Muted(engine) : engine, latencyMs: 200);
    output.Stopped += error => Console.WriteLine(error is null ? "stopped" : $"stopped: {error.Message}");

    for (int second = 1; second <= seconds; second++)
    {
        Thread.Sleep(1000);
        Console.WriteLine($"{second,4}s  played {output.SamplesPlayed / (double)SidFilePlaybackEngine.SampleRate,6:0.00}s  play calls {engine.PlayCalls}");
    }

    output.Stop();
    return 0;
}
catch (Exception ex) when (ex is SidPlaybackException or IOException or InvalidOperationException or PortAudioSharp.PortAudioException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

/// <summary>Runs the engine as usual and hands the device silence - for checking the path without
/// a sound.</summary>
sealed class Muted(IWaveProvider source) : IWaveProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        Array.Clear(buffer, offset, read);
        return read;
    }
}
