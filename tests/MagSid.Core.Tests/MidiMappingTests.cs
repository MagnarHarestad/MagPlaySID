using MagSid.Core.Model;

namespace MagSid.Core.Tests;

/// <summary>
/// What a MIDI keyboard's numbers mean here. Both halves decide values that end up in a song, and
/// neither can be checked by playing a keyboard at the screen - a semitone out or a knob that never
/// quite reaches its top would take a long time to notice and longer to explain.
/// </summary>
public class MidiMappingTests
{
    /// <summary>Middle C is MIDI 60 and it has to land on C-4, because that is what the keyboard's
    /// own labelling promises.</summary>
    [Theory]
    [InlineData(60, "C-4")]
    [InlineData(12, "C-0")]
    [InlineData(107, "B-7")]
    [InlineData(61, "C#4")]
    [InlineData(59, "B-3")]
    public void A_note_number_names_the_note_a_keyboard_promises(int noteNumber, string expected)
    {
        Assert.Equal(NoteName.Parse(expected), MidiMapping.NoteFor(noteNumber));
    }

    /// <summary>Outside C-0..B-7 the player's frequency table has nothing at all, and folding those
    /// keys into the nearest octave would make the keyboard lie about what it just played.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(108)]
    [InlineData(127)]
    public void A_key_the_player_cannot_reach_is_refused(int noteNumber)
    {
        Assert.Null(MidiMapping.NoteFor(noteNumber));
    }

    [Fact]
    public void Every_playable_note_round_trips()
    {
        foreach (var note in NoteName.AllNotes)
            Assert.Equal(note, MidiMapping.NoteFor(MidiMapping.NoteNumberOf(note)));
    }

    /// <summary>The ends matter most: a knob that cannot be driven to its own maximum from a
    /// controller is the kind of fault that gets blamed on the hardware.</summary>
    [Theory]
    [InlineData(0, 0, 15, 0)]
    [InlineData(127, 0, 15, 15)]
    [InlineData(0, -128, 127, -128)]
    [InlineData(127, -128, 127, 127)]
    [InlineData(0, 0, 255, 0)]
    [InlineData(127, 0, 255, 255)]
    public void A_controller_reaches_both_ends_of_a_knob(int controller, int min, int max, int expected)
    {
        Assert.Equal(expected, MidiMapping.ScaleController(controller, min, max));
    }

    /// <summary>
    /// The middle of a controller's travel lands on the middle of the knob's range, within whichever
    /// of the two is the coarser: the knob's own unit, or one controller step.
    ///
    /// Never exactly on it, and it cannot be. A wide knob is the coarser way round - 127 controller
    /// steps do not divide 255 evenly, so the centre of -128..127 sits at controller 63.75 and no
    /// whole controller value names it. A narrow one is the other way round: the controller resolves
    /// finer than a 0..15 knob can, and 7.5 is not a value that knob has. Asking for exactness was a
    /// test wrong about arithmetic rather than a mapping wrong about knobs.
    /// </summary>
    [Theory]
    [InlineData(0, 15)]
    [InlineData(0, 255)]
    [InlineData(-128, 127)]
    public void The_middle_of_the_travel_is_the_middle_of_the_range(int min, int max)
    {
        double middle = (min + max) / 2.0;
        double tolerance = Math.Max(1.0, (max - min) / 127.0);

        Assert.InRange(
            MidiMapping.ScaleController(64, min, max), middle - tolerance, middle + tolerance);
    }

    /// <summary>Rounded rather than truncated: with truncation a 0..15 knob would give eight
    /// controller steps to 0..7 and only seven to 8..15.</summary>
    [Theory]
    [InlineData(64, 8)]
    [InlineData(63, 7)]
    public void A_small_range_divides_evenly(int controller, int expected)
    {
        Assert.Equal(expected, MidiMapping.ScaleController(controller, 0, 15));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(200)]
    public void A_controller_value_outside_its_own_range_is_clamped(int controller)
    {
        int scaled = MidiMapping.ScaleController(controller, 0, 15);
        Assert.InRange(scaled, 0, 15);
    }

    /// <summary>A knob whose range is a single value has nowhere to go, and must not divide by the
    /// zero that describes it.</summary>
    [Fact]
    public void A_knob_with_no_range_stays_where_it_is()
    {
        Assert.Equal(7, MidiMapping.ScaleController(100, 7, 7));
        Assert.Equal(0, MidiMapping.ControllerFor(7, 7, 7));
    }

    /// <summary>
    /// A knob no wider than the controller comes back exactly, which is what a surface with a lit
    /// ring needs if its ring is not to drift from the knob.
    ///
    /// A wider one cannot: 256 values do not survive a trip through 128, and two of them will
    /// always share a controller step. Vibrato depth and slide are both that wide, so the guarantee
    /// there is only that the value returns within one step - and a knob that moves by one while
    /// the hardware sits still is the price of turning a byte with a 7-bit controller.
    /// </summary>
    [Theory]
    [InlineData(0, 15)]
    [InlineData(0, 127)]
    public void A_value_survives_the_round_trip_when_the_knob_fits_the_controller(int min, int max)
    {
        for (int value = min; value <= max; value++)
        {
            int controller = MidiMapping.ControllerFor(value, min, max);
            Assert.Equal(value, MidiMapping.ScaleController(controller, min, max));
        }
    }

    [Theory]
    [InlineData(0, 255)]
    [InlineData(-128, 127)]
    public void A_knob_wider_than_the_controller_returns_within_one_step(int min, int max)
    {
        double step = (max - min) / 127.0;

        for (int value = min; value <= max; value++)
        {
            int controller = MidiMapping.ControllerFor(value, min, max);
            int back = MidiMapping.ScaleController(controller, min, max);

            Assert.InRange(back, value - step - 1, value + step + 1);
        }
    }
}
