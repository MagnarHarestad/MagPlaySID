using MagSid.Core.Model;

namespace MagSid.Core.Tests;

/// <summary>
/// "Change sound" in the sound bank: an instrument takes on another's whole sound and keeps its
/// number. Every property is found by reflection here too, so a setting added to Instrument later
/// is checked without anyone remembering to.
/// </summary>
public class InstrumentCopySoundTests
{
    private static Instrument EverythingSet()
    {
        var source = new Instrument { Number = 9 };
        foreach (var property in typeof(Instrument).GetProperties().Where(p => p.CanWrite && p.Name != nameof(Instrument.Number)))
        {
            object value = property.PropertyType switch
            {
                var t when t == typeof(string) => "Snare 2",
                var t when t == typeof(bool) => true,
                var t when t == typeof(byte) => (byte)0x5A,
                var t when t == typeof(int) => 3,
                var t when t.IsEnum => Enum.GetValues(t).Cast<object>().Last(),
                var t => throw new InvalidOperationException($"Instrument.{property.Name} is a {t.Name}: teach this test a value for it"),
            };
            property.SetValue(source, value);
        }

        source.Wavetable.AddRange([0x81, 0x41, 0x40]);
        source.Arpeggio.AddRange([NoteName.Parse("C-6"), NoteName.Parse("A#3")]);
        source.Filter.Add(FilterStep.Setup(0x1F, 0xF1, 0x40));
        source.Filter.Add(FilterStep.Sweep(0x20));
        return source;
    }

    [Fact]
    public void Every_setting_and_program_comes_over_and_the_number_stays()
    {
        var source = EverythingSet();
        var target = new Instrument { Number = 2, Name = "Flute", Waveform = 0x11 };
        target.Wavetable.Add(0x09);

        target.CopySoundFrom(source);

        Assert.Equal(2, target.Number);
        foreach (var property in typeof(Instrument).GetProperties().Where(p => p.Name != nameof(Instrument.Number)))
        {
            object? mine = property.GetValue(target), theirs = property.GetValue(source);
            if (mine is System.Collections.IEnumerable list && mine is not string)
                Assert.Equal(((System.Collections.IEnumerable)theirs!).Cast<object>(), list.Cast<object>());
            else
                Assert.Equal(theirs, mine);
        }
    }

    [Fact]
    public void The_programs_are_copies_not_shared()
    {
        var source = EverythingSet();
        var target = new Instrument { Number = 2 };

        target.CopySoundFrom(source);
        target.Wavetable.Add(0x21);
        target.Arpeggio.Clear();

        Assert.Equal(3, source.Wavetable.Count);
        Assert.Equal(2, source.Arpeggio.Count);
    }
}
