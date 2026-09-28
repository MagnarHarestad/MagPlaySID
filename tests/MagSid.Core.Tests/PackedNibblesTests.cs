using MagSid.Core.Model;

namespace MagSid.Core.Tests;

/// <summary>
/// The DAW view offers each half of a packed byte as its own knob, so every turn of one has to put
/// its half back without touching the other. Getting that wrong would not throw or look wrong - it
/// would quietly change an instrument's decay while someone adjusted its attack, and only show up
/// as a song that no longer sounds the way it did.
/// </summary>
public class PackedNibblesTests
{
    [Theory]
    [InlineData(0x00, 0x0, 0x0)]
    [InlineData(0x0F, 0x0, 0xF)]
    [InlineData(0xF0, 0xF, 0x0)]
    [InlineData(0xBA, 0xB, 0xA)]
    [InlineData(0x34, 0x3, 0x4)]
    public void Splits_a_byte_into_its_two_halves(int value, int high, int low)
    {
        Assert.Equal(high, PackedNibbles.High((byte)value));
        Assert.Equal(low, PackedNibbles.Low((byte)value));
    }

    [Fact]
    public void Replacing_one_half_leaves_the_other_alone()
    {
        for (int original = 0; original <= 0xFF; original++)
        {
            for (int replacement = 0; replacement <= 0xF; replacement++)
            {
                byte withHigh = PackedNibbles.WithHigh((byte)original, replacement);
                Assert.Equal(replacement, PackedNibbles.High(withHigh));
                Assert.Equal(PackedNibbles.Low((byte)original), PackedNibbles.Low(withHigh));

                byte withLow = PackedNibbles.WithLow((byte)original, replacement);
                Assert.Equal(replacement, PackedNibbles.Low(withLow));
                Assert.Equal(PackedNibbles.High((byte)original), PackedNibbles.High(withLow));
            }
        }
    }

    /// <summary>A knob that ran past its range must not spill into the neighbouring half - the
    /// failure that would turn a decay of 0 into an attack of 15.</summary>
    [Theory]
    [InlineData(0x10)]
    [InlineData(0xFF)]
    [InlineData(-1)]
    public void An_out_of_range_half_cannot_reach_the_other(int rogue)
    {
        Assert.Equal(PackedNibbles.Low(0xBA), PackedNibbles.Low(PackedNibbles.WithHigh(0xBA, rogue)));
        Assert.Equal(PackedNibbles.High(0xBA), PackedNibbles.High(PackedNibbles.WithLow(0xBA, rogue)));
    }

    [Fact]
    public void Round_trips_every_byte()
    {
        for (int value = 0; value <= 0xFF; value++)
        {
            byte b = (byte)value;
            Assert.Equal(b, PackedNibbles.From(PackedNibbles.High(b), PackedNibbles.Low(b)));
        }
    }
}
