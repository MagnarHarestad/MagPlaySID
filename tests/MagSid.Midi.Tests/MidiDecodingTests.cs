using MagSid.Core.Model;
using MagSid.Midi;

namespace MagSid.Midi.Tests;

/// <summary>What every backend shares: the byte stream turned into messages, and messages turned
/// into the editor's events.</summary>
public class MidiDecodingTests
{
    private static List<(byte, byte, byte)> Parse(params byte[] bytes)
    {
        var messages = new List<(byte, byte, byte)>();
        new MidiStreamParser((s, a, b) => messages.Add((s, a, b))).Feed(bytes);
        return messages;
    }

    [Fact]
    public void Running_status_repeats_the_last_status_byte()
    {
        var messages = Parse(0x90, 60, 100, 64, 90, 67, 0);

        Assert.Equal([(0x90, 60, 100), (0x90, 64, 90), (0x90, 67, 0)], messages);
    }

    [Fact]
    public void Realtime_bytes_inside_a_message_do_not_break_it()
    {
        var messages = Parse(0x90, 0xF8, 60, 0xFE, 100, 0xB3, 74, 0xF8, 127);

        Assert.Equal([(0x90, 60, 100), (0xB3, 74, 127)], messages);
    }

    [Fact]
    public void System_exclusive_is_skipped_to_its_end()
    {
        var messages = Parse(0xF0, 0x43, 0x10, 0x4C, 0xF7, 0x80, 60, 0);

        Assert.Equal([(0x80, 60, 0)], messages);
    }

    [Fact]
    public void Program_change_carries_one_data_byte()
    {
        var messages = Parse(0xC0, 5, 0xC0, 6);

        Assert.Equal([(0xC0, 5, 0), (0xC0, 6, 0)], messages);
    }

    [Fact]
    public void Data_bytes_with_no_status_before_them_are_dropped()
    {
        Assert.Empty(Parse(60, 100, 0xF2, 1, 2, 3));
    }

    [Fact]
    public void Note_on_at_velocity_zero_is_a_release()
    {
        var (pressed, released, _, input) = Listen();

        input.Dispatch(0x91, 60, 100);
        input.Dispatch(0x91, 60, 0);
        input.Dispatch(0x82, 62, 64);

        Assert.Equal([(NoteName.Parse("C-4"), 100)], pressed);
        Assert.Equal([NoteName.Parse("C-4"), NoteName.Parse("D-4")], released);
    }

    [Fact]
    public void Keys_outside_the_players_range_raise_nothing()
    {
        var (pressed, released, _, input) = Listen();

        input.Dispatch(0x90, 11, 100);   // below C-0
        input.Dispatch(0x90, 108, 100);  // C-8, above B-7
        input.Dispatch(0x80, 108, 0);

        Assert.Empty(pressed);
        Assert.Empty(released);
    }

    [Fact]
    public void Controllers_ignore_the_channel()
    {
        var (_, _, controls, input) = Listen();

        input.Dispatch(0xB0, 74, 10);
        input.Dispatch(0xBF, 74, 20);

        Assert.Equal([(74, 10), (74, 20)], controls);
    }

    internal static (List<(NoteName, int)>, List<NoteName>, List<(int, int)>, MidiInput) Listen()
    {
        var pressed = new List<(NoteName, int)>();
        var released = new List<NoteName>();
        var controls = new List<(int, int)>();
        var input = new MidiInput(action => action());
        input.NotePressed += (note, velocity) => pressed.Add((note, velocity));
        input.NoteReleased += released.Add;
        input.ControlChanged += (controller, value) => controls.Add((controller, value));
        return (pressed, released, controls, input);
    }
}
