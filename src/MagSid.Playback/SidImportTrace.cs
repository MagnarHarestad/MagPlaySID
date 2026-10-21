using MagSid.Core.SidImport;

namespace MagSid.Playback;

/// <summary>
/// Runs a .sid tune silently and records what it writes to the SID, for the SID import.
///
/// MagSID's player runs once a frame, so the writes are grouped by 50 Hz frame however often the
/// tune's play routine is called: a tune on CIA 1 at 100 calls a second puts two calls' writes in
/// each frame, in order, and the import hears that frame's last word on every register.
/// </summary>
public static class SidImportTrace
{
    public const double FrameCycles = PlayRate.PalClock / 50.0;

    /// <summary>Records <paramref name="frames"/> 50 Hz frames of subtune <paramref name="song"/>
    /// (1-based).</summary>
    public static SidTrace Record(SidFile file, int song, int frames)
    {
        if (file.UnsupportedReason() is { } reason)
            throw new SidPlaybackException(reason);

        var memory = new byte[0x10000];
        file.Data.CopyTo(memory, file.LoadAddress);

        bool timerSet = false;
        var current = new List<(int Register, int Value)>();
        var cpu = new Mos6502(memory, (address, value) =>
        {
            if (address >= 0xD400 && address <= 0xD41F)
                current.Add((address & 0x1F, value));
            else if (PlayRate.IsTimerLatch(address))
                timerSet = true;
        });

        cpu.A = Math.Clamp(song, 1, Math.Max(1, file.Songs)) - 1;
        cpu.Call(file.InitAddress);
        var init = current;

        // The time of the next play call, in cycles; each frame takes every call due before it ends.
        var byFrame = new List<IReadOnlyList<(int, int)>>(frames);
        double nextCall = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            current = [];
            double frameEnd = (frame + 1) * FrameCycles;
            while (nextCall < frameEnd)
            {
                cpu.Call(file.PlayAddress);
                nextCall += PlayRate.CyclesPerCall(file, song, memory, timerSet);
            }

            byFrame.Add(current);
        }

        return new SidTrace(init, byFrame);
    }

    /// <summary>A MagSID song played through MagSID's own sequencer, recorded the same way - what
    /// the import is checked against (SidTranscriber.Agreement).</summary>
    public static SidTrace Replay(MagSid.Core.Model.Song song, int frames)
    {
        var current = new List<(int Register, int Value)>();
        var sequencer = new SongSequencer(song, (register, value) => current.Add((register, value)));
        var init = current;

        var byFrame = new List<IReadOnlyList<(int, int)>>(frames);
        for (int frame = 0; frame < frames; frame++)
        {
            current = [];
            sequencer.Frame();
            byFrame.Add(current);
        }

        return new SidTrace(init, byFrame);
    }

    /// <summary>How many times a frame the tune's play routine runs, as init leaves it - 1 for a
    /// tune on the vertical blank.</summary>
    public static double CallsPerFrame(SidFile file, int song)
    {
        if (!file.UsesCiaTimer(song))
            return 1;

        var memory = new byte[0x10000];
        file.Data.CopyTo(memory, file.LoadAddress);
        bool timerSet = false;
        var cpu = new Mos6502(memory, (address, _) => timerSet |= PlayRate.IsTimerLatch(address));
        cpu.A = Math.Clamp(song, 1, Math.Max(1, file.Songs)) - 1;
        cpu.Call(file.InitAddress);
        return FrameCycles / PlayRate.CyclesPerCall(file, song, memory, timerSet);
    }
}
