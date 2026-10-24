using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// Funk speed: a song's ticks alternate between two speeds - <c>var v_Song_Funk = N</c> beside
/// <c>v_Song_Speed</c> - the way GoatTracker's funktempo swings its rows. One global setting, no
/// pattern command: the player's reload flips a zero-page shuffle byte with the speeds' difference
/// every tick (6 bytes, one zero-page byte), and a song without it is assembled as before.
/// </summary>
public class FunkSpeedTests
{
    private static string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", file);

    [Theory]
    [InlineData("funk_bellaciao.s")]
    [InlineData("funk_slide.s")]
    public void The_emulated_player_writes_what_the_sequencer_writes(string file)
    {
        var song = PatternsFileReader.Read(Fixture(file));
        Assert.True(song.HasFunk);

        var fromCpu = new Dictionary<int, int>();
        var cpu = new PlayerCpu((register, value) => fromCpu[register] = value);
        var image = SongImageBuilder.Build(song);
        Array.Copy(image, 0, cpu.Memory, 0x1000, image.Length);
        cpu.Call(0x1000, a: 0);

        var fromSequencer = new Dictionary<int, int>();
        var sequencer = new SongSequencer(song, (register, value) => fromSequencer[register] = value & 0xFF);

        int frames = SongCoverageAnalyzer.PassFrames(song) + 200;
        for (int frame = 0; frame < frames; frame++)
        {
            fromCpu.Clear();
            fromSequencer.Clear();
            cpu.Call(0x1003);
            sequencer.Frame();

            Assert.True(fromCpu.Count == fromSequencer.Count
                        && fromCpu.All(w => fromSequencer.TryGetValue(w.Key, out int v) && v == w.Value),
                $"{file}, frame {frame}: the 6502 wrote {Format(fromCpu)}, the sequencer {Format(fromSequencer)}");
        }
    }

    private static string Format(Dictionary<int, int> writes) =>
        string.Join(" ", writes.OrderBy(w => w.Key).Select(w => $"D4{w.Key:X2}=${w.Value:X2}"));

    [Fact]
    public void The_ticks_take_the_two_speeds_in_turn_the_first_one_first()
    {
        // Speeds 5 and 4: ticks of 6 and 5 frames, from the song's first row.
        var song = PatternsFileReader.Read(Fixture("funk_bellaciao.s"));
        var sequencer = new SongSequencer(song, (_, _) => { });

        var ticks = new List<int>();
        for (int frame = 0; frame < 120; frame++)
            if (sequencer.Frame())
                ticks.Add(frame);

        var lengths = ticks.Zip(ticks.Skip(1), (a, b) => b - a).Take(8);
        Assert.Equal([6, 5, 6, 5, 6, 5, 6, 5], lengths);
        Assert.Equal(0, ticks[0]);
        Assert.Equal(song.FrameOfTick(4), ticks[4]);
    }

    [Fact]
    public void It_costs_six_bytes_and_a_zero_page_byte()
    {
        var swinging = PatternsFileReader.Read(Fixture("funk_bellaciao.s"));
        var straight = SongCloner.Clone(swinging);
        straight.FunkSpeed = null;

        Assert.Equal(SongImageBuilder.Build(straight).Length + 6, SongImageBuilder.Build(swinging).Length);
        Assert.Equal(SongSizeCalculator.Measure(straight).TotalBytes + 6, SongSizeCalculator.Measure(swinging).TotalBytes);
        Assert.Equal(new ZeroPageLayout(new PlayerFeatures(true, false)).ByteCount + 1,
                     new ZeroPageLayout(new PlayerFeatures(true, false, Funk: true)).ByteCount);
    }

    [Fact]
    public void The_song_file_keeps_it_and_a_second_speed_equal_to_the_first_is_none()
    {
        var song = PatternsFileReader.Read(Fixture("funk_bellaciao.s"));
        string text = PatternsFileWriter.Render(song);
        Assert.Contains("var v_Song_Funk = 4", text);
        Assert.Equal(4, PatternsFileReader.Parse(text, "funk.s").FunkSpeed);

        song.FunkSpeed = song.Speed;
        Assert.False(song.HasFunk);
        Assert.DoesNotContain("v_Song_Funk", PatternsFileWriter.Render(song));
        var straight = SongCloner.Clone(song);
        straight.FunkSpeed = null;
        Assert.Equal(SongImageBuilder.Build(straight), SongImageBuilder.Build(song));
    }
}
