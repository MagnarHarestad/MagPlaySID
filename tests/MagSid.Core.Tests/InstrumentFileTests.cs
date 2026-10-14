using System.Collections;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using Xunit;

namespace MagSid.Core.Tests;

public class InstrumentFileTests
{
    /// <summary>SongData sits at the repository root, five levels up from
    /// tests\X\bin\Debug\net8.0.</summary>
    private static string SongDataDirectory => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SongData"));

    public static TheoryData<string> Songs()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.GetFiles(SongDataDirectory, "*.s"))
            data.Add(Path.GetFileName(path));
        return data;
    }

    /// <summary>Compared property by property through reflection, so a property added to
    /// Instrument later and left out of the file fails here rather than going missing.</summary>
    [Theory]
    [MemberData(nameof(Songs))]
    public void Every_instrument_of_every_song_survives_the_round_trip(string file)
    {
        var song = PatternsFileReader.Read(Path.Combine(SongDataDirectory, file));
        Assert.NotEmpty(song.Instruments);

        foreach (var original in song.Instruments)
        {
            var read = InstrumentFile.Read(InstrumentFile.Write(original));
            read.Number = original.Number;

            foreach (var property in typeof(Instrument).GetProperties())
            {
                object? expected = property.GetValue(original), actual = property.GetValue(read);
                if (expected is IEnumerable list and not string)
                    Assert.Equal(list.Cast<object>(), ((IEnumerable)actual!).Cast<object>());
                else
                    Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void The_file_reads_as_a_person_would_write_it()
    {
        var instrument = Instrument.CreateDefault(3);
        instrument.Name = "Lead";
        instrument.Waveform = 0x41;
        instrument.Filter.Add(FilterStep.Setup(0x1f, 0xf1, 0x03, sweepRate: -2));
        instrument.Filter.Add(FilterStep.Sweep(0x08));

        string json = InstrumentFile.Write(instrument);

        Assert.Contains("\"format\": \"MagSID instrument\"", json);
        Assert.Contains("\"waveform\": \"$41\"", json);
        Assert.Contains("\"ending\": \"root\"", json);
        Assert.DoesNotContain("\"number\"", json);
    }

    [Fact]
    public void Bytes_may_be_hex_with_a_dollar_or_0x_or_a_plain_number()
    {
        var read = InstrumentFile.Read("""
            { "format": "MagSID instrument", "version": 1,
              "waveform": "$41", "attackDecay": "0x0a", "sustainRelease": 249 }
            """);

        Assert.Equal(0x41, read.Waveform);
        Assert.Equal(0x0a, read.AttackDecay);
        Assert.Equal(249, read.SustainRelease);
    }

    [Fact]
    public void Missing_fields_read_as_a_bare_instrument()
    {
        var read = InstrumentFile.Read("""{ "format": "MagSID instrument", "version": 1 }""");
        var bare = new Instrument();

        Assert.Equal(bare.PulseWidth, read.PulseWidth);
        Assert.Equal(bare.ArpeggioEnding, read.ArpeggioEnding);
        Assert.Empty(read.Wavetable);
        Assert.Empty(read.Filter);
        Assert.Equal("", read.Name);
    }

    [Theory]
    [InlineData("""[1, 2]""", "not a JSON object")]
    [InlineData("""{ "format": "something else" }""", "Not a MagSID instrument")]
    [InlineData("""{ "format": "MagSID instrument", "version": 2 }""", "newer")]
    [InlineData("""{ "format": "MagSID instrument", "waveform": "$141" }""", "$00..$FF")]
    [InlineData("""{ "format": "MagSID instrument", "wavetable": ["$41", "$00"] }""", "$00 ends a program")]
    [InlineData("""{ "format": "MagSID instrument", "noteProgram": { "steps": ["H-4"] } }""", "not a note")]
    [InlineData("""{ "format": "MagSID instrument", "noteProgram": { "ending": "bounce" } }""", "loop, hold or root")]
    [InlineData("""{ "format": "MagSID instrument", "noteProgram": { "ending": "hold" } }""", "ARP HOLD")]
    [InlineData("""{ "format": "MagSID instrument", "filterProgram": [{ "cutoff": "$FF" }] }""", "FLT cutoff")]
    [InlineData("""{ "format": "MagSID instrument", "pulse": { "width": 16 } }""", "PW width")]
    [InlineData("""{ "format": "MagSID instrument", "pulse": { "continuous": "yes" } }""", "true or false")]
    [InlineData("""{ "format": "MagSID instrument", "waveform": """, "not valid JSON")]
    public void A_file_a_song_could_not_take_is_refused_with_a_reason(string json, string reason)
    {
        var ex = Assert.Throws<InstrumentFileException>(() => InstrumentFile.Read(json));
        Assert.Contains(reason, ex.Message);
    }
}
