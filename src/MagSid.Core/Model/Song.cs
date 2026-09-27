namespace MagSid.Core.Model;

/// <summary>The root aggregate: everything one SongData\Patterns.s file describes.</summary>
public sealed class Song
{
    public const int ChannelCount = 3;

    public int Speed { get; set; } = 6;
    public List<Instrument> Instruments { get; } = [];
    public List<Pattern> Patterns { get; } = [];

    public ChannelOrder[] ChannelOrders { get; } =
    [
        new() { ChannelNumber = 1 },
        new() { ChannelNumber = 2 },
        new() { ChannelNumber = 3 },
    ];
}
