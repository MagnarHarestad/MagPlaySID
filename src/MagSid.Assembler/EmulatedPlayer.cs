using MagSid.Core.Model;

namespace MagSid.Assembler;

/// <summary>
/// A song's exported player image on a cycle-exact 6502: init, then one play call per frame, each
/// returning what it cost in cycles - "jsr" and "rts" included. What both the rastertime
/// measurement and the live figure during playback run.
/// </summary>
public sealed class EmulatedPlayer
{
    private readonly PlayerCpu _cpu = new();
    private readonly int _loadAddress;

    public EmulatedPlayer(Song song, int loadAddress = Core.SidLoadAddress.Default)
    {
        _loadAddress = loadAddress;
        var image = SongImageBuilder.Build(song, loadAddress);
        Array.Copy(image, 0, _cpu.Memory, loadAddress, image.Length);
    }

    public long Init() => _cpu.Call(_loadAddress, a: 0);

    public long Play() => _cpu.Call(_loadAddress + 3);
}
