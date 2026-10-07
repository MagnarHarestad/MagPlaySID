using MagSid.Core;
using MagSid.Core.Serialization;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// The slide code in the 6502 player against the C# engine: every frame's SID writes, run on the
/// cycle-exact CPU from the image that SongImageTests pins byte for byte to c6510. And the size
/// the editor reports, against the image actually built.
/// </summary>
public class PatternSlidePlayerTests
{
    private static string FixturePath(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", file);

    /// <summary>Slide over a note program and a vibrato, stopped by "100", then a new note and a
    /// downward slide on another channel - every path through the slide code at once.</summary>
    private const string Mixed = """
        var v_Song_Speed = 1

        INSTRUMENTS:
        01, $41, $09, $a0, WAVE $41 $41, ARP "C-4" "E-4" "G-4", VIB $00 $04
        02, $21, $09, $a0, VIB $32 $10
        03, $11, $09, $a0
        END:

        PAT01:
        "C-4", 01
        "103", 00
        "...", 00
        "100", 00
        "...", 00
        "E-4", 02
        "220", 00
        "...", 00
        "...", 00
        "G-3", 03
        "17F", 00
        "...", 00
        END:

        PAT02:
        "C-3", 03
        "280", 00
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
        1, 2
        END:
        """;

    public static TheoryData<string> Songs => new()
    {
        "fixture:slide_only.s",
        "fixture:slide_and_vib.s",
        "mixed",
    };

    private static Core.Model.Song Load(string which) => which == "mixed"
        ? PatternsFileReader.Parse(Mixed)
        : PatternsFileReader.Read(FixturePath(which["fixture:".Length..]));

    [Theory]
    [MemberData(nameof(Songs))]
    public void The_6502_player_slides_exactly_as_the_engine_does(string which)
    {
        var song = Load(which);

        var fromCpu = new Dictionary<int, int>();
        var cpu = new PlayerCpu((register, value) => fromCpu[register] = value);
        var image = SongImageBuilder.Build(song);
        Array.Copy(image, 0, cpu.Memory, 0x1000, image.Length);
        cpu.Call(0x1000, a: 0);

        var fromSequencer = new Dictionary<int, int>();
        var sequencer = new SongSequencer(song, (register, value) => fromSequencer[register] = value & 0xFF);

        for (int frame = 0; frame < 400; frame++)
        {
            fromCpu.Clear();
            fromSequencer.Clear();
            cpu.Call(0x1003);
            sequencer.Frame();

            Assert.True(fromCpu.Count == fromSequencer.Count
                        && fromCpu.All(w => fromSequencer.TryGetValue(w.Key, out int v) && v == w.Value),
                $"{which}, frame {frame}: 6502 {Format(fromCpu)} / engine {Format(fromSequencer)}");
        }
    }

    [Theory]
    [MemberData(nameof(Songs))]
    public void The_reported_size_is_the_built_size(string which)
    {
        var song = Load(which);

        Assert.Equal(SongImageBuilder.Build(song).Length, SongSizeCalculator.Measure(song).TotalBytes);
    }

    private static string Format(Dictionary<int, int> writes) =>
        string.Join(" ", writes.OrderBy(w => w.Key).Select(w => $"D4{w.Key:X2}=${w.Value:X2}"));
}
