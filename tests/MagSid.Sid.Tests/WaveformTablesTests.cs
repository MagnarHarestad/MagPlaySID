using MagSid.Sid;

namespace MagSid.Sid.Tests;

/// <summary>Data-integrity checks: every combined-waveform table must be exactly 4096
/// bytes (one per 12-bit accumulator value), and spot values are checked against the
/// exact bytes read from simonowen/resid's wave6581__ST.cc during porting.</summary>
public class WaveformTablesTests
{
    [Theory]
    [InlineData(nameof(WaveformTables.Wave6581_ST))]
    [InlineData(nameof(WaveformTables.Wave6581_PT))]
    [InlineData(nameof(WaveformTables.Wave6581_PS))]
    [InlineData(nameof(WaveformTables.Wave6581_PST))]
    [InlineData(nameof(WaveformTables.Wave8580_ST))]
    [InlineData(nameof(WaveformTables.Wave8580_PT))]
    [InlineData(nameof(WaveformTables.Wave8580_PS))]
    [InlineData(nameof(WaveformTables.Wave8580_PST))]
    public void Every_table_has_4096_entries(string fieldName)
    {
        var field = typeof(WaveformTables).GetField(fieldName)!;
        var table = (byte[])field.GetValue(null)!;
        Assert.Equal(4096, table.Length);
    }

    [Fact]
    public void Wave6581_ST_matches_known_source_bytes()
    {
        // Verified directly against wave6581__ST.cc during porting:
        // /* 0xfe0: */ 0x38 x16   /* 0xff0: */ 0x3c x8   /* 0xff8: */ 0x3e,0x3e,0x3f,0x3f,0x7f,0x7f,0x7f,0x7f
        Assert.Equal(0x00, WaveformTables.Wave6581_ST[0]);
        Assert.Equal(0x38, WaveformTables.Wave6581_ST[0xfe0]);
        Assert.Equal(0x3c, WaveformTables.Wave6581_ST[0xff0]);
        Assert.Equal(0x3e, WaveformTables.Wave6581_ST[0xff8]);
        Assert.Equal(0x3f, WaveformTables.Wave6581_ST[0xffa]);
        Assert.Equal(0x7f, WaveformTables.Wave6581_ST[0xfff]);
    }
}
