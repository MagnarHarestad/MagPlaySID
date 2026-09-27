using MagSid.Core.Model;

namespace MagSid.Assembler;

/// <summary>
/// tbl_songorder: the three channels' pattern lists, one after another in a single array.
///
/// Each list is its pattern numbers, then $00, then where to restart. The restart position is made
/// <em>absolute</em> into the whole array before it is written, because voice_orderpos indexes all
/// three lists from the same base - which is also why the array is capped at 255 bytes and why each
/// channel needs its own start offset (v_ord_start1..3) to seed that variable.
/// </summary>
public sealed record SongOrderTable(byte[] Bytes, int Start1, int Start2, int Start3)
{
    public static SongOrderTable Build(Song song)
    {
        int patternCount = song.Patterns.Count == 0 ? 0 : song.Patterns.Max(p => p.Number);

        var bytes = new List<byte>();
        var starts = new int[Song.ChannelCount];

        for (int channel = 0; channel < Song.ChannelCount; channel++)
        {
            var order = song.ChannelOrders[channel];
            starts[channel] = bytes.Count;

            var patterns = order.Tokens.OfType<PatternRefToken>().Select(t => t.PatternNumber).ToList();
            if (patterns.Count == 0)
            {
                throw new SongTooLargeException(
                    $"ORDER_CH{channel + 1} is empty; every channel needs at least one pattern.");
            }

            foreach (int number in patterns)
            {
                if (number < 1 || number > patternCount)
                {
                    throw new SongTooLargeException(
                        $"ORDER_CH{channel + 1} references pattern {number}, which the song does not define.");
                }

                bytes.Add((byte)number);
            }

            // RestartPosition is 1-based within this channel's own list; the player wants a 0-based
            // index into the shared array, so it loses one and gains this channel's start.
            int restart = order.RestartPosition;
            if (restart < 1 || restart > patterns.Count)
            {
                throw new SongTooLargeException(
                    $"ORDER_CH{channel + 1}: restart position {restart} is outside its list of " +
                    $"{patterns.Count} entries.");
            }

            bytes.Add(0x00);                                    // end of this channel's list
            bytes.Add((byte)(starts[channel] + restart - 1));
        }

        if (bytes.Count > 255)
        {
            throw new SongTooLargeException(
                $"The song order totals {bytes.Count} bytes across the three channels - " +
                "voice_orderpos is a single byte, so 255 is the ceiling.");
        }

        return new SongOrderTable(bytes.ToArray(), starts[0], starts[1], starts[2]);
    }
}
