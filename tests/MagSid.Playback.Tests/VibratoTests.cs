using MagSid.Core.Serialization;

namespace MagSid.Playback.Tests;

/// <summary>
/// Vibrato, checked against the real assembled player: the expected frequencies below were
/// captured by running MAGSIDv7_LowMemory's own musicplayer.s (assembled with a VIB instrument, so
/// v_vibrato is defined) through tools\cpu6502.py on this exact song, frame by frame. They are
/// not derived from the C# implementation, so a drift between VoiceEngine.StepVibrato and
/// Voice_Automation's .vibrato section shows up here.
/// </summary>
public class VibratoTests
{
    private const string SlideSongText = """
        var v_Song_Speed = 4

        INSTRUMENTS:
        01, $41, $09, $a0, WAVE $41, ARP ROOT, SLIDE +8
        02, $41, $09, $a0, SLIDE -5
        03, $41, $09, $a0, WAVE $41 $41, ARP "C-4" "E-4" "G-4", VIB $00 $04, SLIDE +3
        END:

        PAT01:
        "C-4", 01
        "...", 00
        "...", 00
        "...", 00
        "E-4", 02
        "...", 00
        "...", 00
        "...", 00
        "G-3", 03
        "...", 00
        "...", 00
        "...", 00
        END:

        PAT02:
        "C-3", 02
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

    private const string SongText = """
        var v_Song_Speed = 4

        INSTRUMENTS:
        01, $41, $09, $a0, WAVE $41, ARP ROOT, VIB $32 $10
        02, $41, $09, $a0, VIB $23 $08
        03, $41, $09, $a0, WAVE $41 $41, ARP "C-4" "E-4" "G-4", VIB $00 $ff
        END:

        PAT01:
        "C-4", 01
        "...", 00
        "...", 00
        "...", 00
        "E-4", 02
        "...", 00
        "...", 00
        "...", 00
        "G-3", 03
        "...", 00
        "...", 00
        "...", 00
        END:

        PAT02:
        "C-3", 02
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

    /// <summary>The frequency each voice was left at after every frame - the last lo/hi pair
    /// written to that voice's registers that frame, carried over when a frame writes none.</summary>
    private static List<int[]> RunFrames(int frames, string text = SongText)
    {
        var song = PatternsFileReader.Parse(text);
        var pair = new int[3, 2];
        var sequencer = new SongSequencer(song, (offset, value) =>
        {
            int voice = offset / 7, reg = offset % 7;
            if (voice < 3 && reg < 2)
            {
                pair[voice, reg] = value;
            }
        });

        var result = new List<int[]>();
        for (int frame = 0; frame < frames; frame++)
        {
            sequencer.Frame();
            result.Add(new[] { 0, 1, 2 }.Select(v => (pair[v, 1] << 8) | pair[v, 0]).ToArray());
        }

        return result;
    }

    [Fact]
    public void Voice_1_traces_the_assembled_players_triangle_after_its_delay()
    {
        var frames = RunFrames(20);

        // Delay $3 = 12 frames of plain C-4 ($1125), then +$10 per frame, turning round after
        // the half-length first run and every speed+1 = 3 frames after that.
        int[] expected =
        [
            .. Enumerable.Repeat(0x1125, 13 - 0),
            0x1135, 0x1145, 0x1135, 0x1125, 0x1115, 0x1125, 0x1135,
        ];

        // Frame 13 is where the vibrato first moves the pitch: the 13th plain frame (index 12) is
        // the last one at the bare note, so the first 13 entries are $1125 and the run follows.
        Assert.Equal(expected.Length, frames.Count);
        Assert.Equal(expected, frames.Select(f => f[0]).ToArray());
    }

    [Fact]
    public void Voice_2_without_an_arpeggio_still_gets_its_vibrato_written()
    {
        var frames = RunFrames(19);

        // No ARP, so the bare note is written once at the trigger and only vibrato rewrites the
        // frequency afterwards - which is why the player has to keep the base frequency itself.
        Assert.All(frames.Take(9), f => Assert.Equal(0x0893, f[1]));
        Assert.Equal(
            new[] { 0x089b, 0x08a3, 0x089b, 0x0893, 0x088b, 0x0883, 0x088b, 0x0893, 0x089b, 0x08a3 },
            frames.Skip(9).Select(f => f[1]).ToArray());
    }

    [Fact]
    public void Slide_adds_its_rate_every_frame_with_and_without_an_arpeggio()
    {
        var frames = RunFrames(56, SlideSongText);

        // Voice 1: ARP ROOT rewrites the bare note every frame, so the slide has to go on top of
        // it. Voice 2: no ARP, so only the slide's own write moves the pitch.
        for (int frame = 0; frame <= 12; frame++)
        {
            Assert.Equal(0x1125 + 8 * frame, frames[frame][0]);
            Assert.Equal(0x0893 - 5 * frame, frames[frame][1]);
        }
    }

    [Fact]
    public void Slide_and_vibrato_share_one_offset_over_an_arpeggio()
    {
        var frames = RunFrames(56, SlideSongText);

        // Captured from the assembled player: slide +3 every frame plus a speed-0, depth-4
        // vibrato that turns round every frame, over a C-4/E-4/G-4 arpeggio.
        Assert.Equal(
            new[]
            {
                0x112c, 0x15a0, 0x19be, 0x1131, 0x15ad, 0x19c3, 0x113e, 0x15b2,
                0x19d0, 0x1143, 0x15bf, 0x19d5, 0x1150, 0x15c4, 0x19e2,
            },
            frames.Skip(41).Take(15).Select(f => f[0]).ToArray());
    }
}
