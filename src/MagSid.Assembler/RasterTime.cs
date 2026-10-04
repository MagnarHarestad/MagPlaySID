using MagSid.Core.Model;

namespace MagSid.Assembler;

/// <summary>What the player costs in CPU time, measured on its own 6502 code.</summary>
/// <param name="InitCycles">One call of init.</param>
/// <param name="AverageCycles">Per call of play, over the frames measured.</param>
/// <param name="MaxCycles">The dearest single call of play.</param>
/// <param name="MaxFrame">Which frame that was, counted from the first play call.</param>
/// <param name="Frames">How many play calls were measured.</param>
public sealed record RasterTimeResult(long InitCycles, double AverageCycles, long MaxCycles, int MaxFrame, int Frames)
{
    /// <summary>A PAL raster line, in CPU cycles.</summary>
    public const int CyclesPerLine = 63;

    /// <summary>The average in raster lines, to the nearest line.</summary>
    public int AverageLines => (int)Math.Round(AverageCycles / CyclesPerLine);

    /// <summary>The worst call in raster lines, rounded up - a line it starts is a line it uses.</summary>
    public int MaxLines => (int)Math.Ceiling(MaxCycles / (double)CyclesPerLine);
}

/// <summary>
/// Rastertime: the image <see cref="SongImageBuilder"/> builds, run on a cycle-exact 6502 - init
/// once, then play once per frame - so the figure is what "jsr play" really costs on a C64, JSR
/// and RTS included, rather than an estimate from the source. Not counted: badlines and sprites,
/// which take cycles from whatever the CPU is doing when the play call lands in the visible screen.
/// </summary>
public static class RasterTime
{
    public static RasterTimeResult Measure(Song song, int frames,
        int loadAddress = Core.SidLoadAddress.Default, CancellationToken cancel = default)
    {
        var image = SongImageBuilder.Build(song, loadAddress);
        var cpu = new PlayerCpu();
        Array.Copy(image, 0, cpu.Memory, loadAddress, image.Length);

        long init = cpu.Call(loadAddress, a: 0);

        long sum = 0, max = 0;
        int maxFrame = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & 1023) == 0)
                cancel.ThrowIfCancellationRequested();

            long cycles = cpu.Call(loadAddress + 3);
            sum += cycles;
            if (cycles > max)
            {
                max = cycles;
                maxFrame = frame;
            }
        }

        return new RasterTimeResult(init, frames == 0 ? 0 : sum / (double)frames, max, maxFrame, frames);
    }
}
