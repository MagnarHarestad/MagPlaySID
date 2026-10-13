using MagSid.Core.Serialization;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// A note off parks the wavetable on a $00 hold instead of stopping the voice, so pulse, note
/// program, vibrato and slide run on through the release. The 6502 player against the C# engine,
/// every frame's SID writes, over the cases that change: a release before the voice's first note,
/// a release with vibrato and a pulse sweep, and a NOHR instrument's next note after its release.
/// </summary>
public class ReleaseRunsOnTests
{
    private const string Song = """
        var v_Song_Speed = 1

        INSTRUMENTS:
        01, $41, $09, $a8, ARP "C-4" "G-4", PW 4 +20, VIB $12 $10
        02, $11, $00, $f9, NOHR
        END:

        PAT01:
        "---", 00
        "...", 00
        "C-5", 01
        "...", 00
        "---", 00
        "...", 00
        "...", 00
        "E-4", 02
        "...", 00
        "---", 00
        "...", 00
        "C-4", 01
        "...", 00
        END:

        PAT02:
        "---", 00
        "...", 00
        "...", 00
        "...", 00
        END:

        ORDER_CH1:
        1
        END:

        ORDER_CH2:
        2
        END:

        ORDER_CH3:
        2
        END:
        """;

    [Fact]
    public void The_6502_player_runs_the_release_on_exactly_as_the_engine_does()
    {
        var song = PatternsFileReader.Parse(Song);

        var fromCpu = new Dictionary<int, int>();
        var cpu = new PlayerCpu((register, value) => fromCpu[register] = value);
        var image = SongImageBuilder.Build(song);
        Array.Copy(image, 0, cpu.Memory, 0x1000, image.Length);
        cpu.Call(0x1000, a: 0);

        var fromSequencer = new Dictionary<int, int>();
        var sequencer = new SongSequencer(song, (register, value) => fromSequencer[register] = value & 0xFF);

        for (int frame = 0; frame < 300; frame++)
        {
            fromCpu.Clear();
            fromSequencer.Clear();
            cpu.Call(0x1003);
            sequencer.Frame();

            Assert.True(fromCpu.Count == fromSequencer.Count
                        && fromCpu.All(w => fromSequencer.TryGetValue(w.Key, out int v) && v == w.Value),
                $"frame {frame}: 6502 {Format(fromCpu)} / engine {Format(fromSequencer)}");
        }
    }

    private static string Format(Dictionary<int, int> writes) =>
        string.Join(" ", writes.OrderBy(w => w.Key).Select(w => $"D4{w.Key:X2}=${w.Value:X2}"));
}
