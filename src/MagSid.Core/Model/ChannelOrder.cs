namespace MagSid.Core.Model;

/// <summary>One channel's ORDER_CHn: list - its own, independent song-order. The list always
/// loops: once it runs out, playback restarts at the 1-based <see cref="RestartPosition"/>
/// (defaults to 1, the start of the list).</summary>
public sealed class ChannelOrder
{
    public required int ChannelNumber { get; init; }
    public List<OrderToken> Tokens { get; } = [];
    public int RestartPosition { get; set; } = 1;

    /// <summary>Inserts a pattern reference at <paramref name="index"/>, keeping
    /// <see cref="RestartPosition"/> aimed at the same entry as before - it shifts down by one
    /// when the insertion lands at or before it.</summary>
    public void InsertEntry(int index, int patternNumber)
    {
        index = Math.Clamp(index, 0, Tokens.Count);
        Tokens.Insert(index, new PatternRefToken(patternNumber));

        if (index <= RestartPosition - 1)
        {
            RestartPosition++;
        }
        ClampRestartPosition();
    }

    /// <summary>Removes the entry at <paramref name="index"/>, keeping
    /// <see cref="RestartPosition"/> aimed at the same entry where it still can - it shifts up
    /// by one when something ahead of it goes away. Removing the restart target itself leaves
    /// the number alone, so it lands on whatever entry slid into that slot.</summary>
    public void RemoveEntryAt(int index)
    {
        if (index < 0 || index >= Tokens.Count)
        {
            return;
        }

        Tokens.RemoveAt(index);
        if (index < RestartPosition - 1)
        {
            RestartPosition--;
        }
        ClampRestartPosition();
    }

    /// <summary>Swaps the entry at <paramref name="index"/> with the one <paramref name="delta"/>
    /// steps away, carrying <see cref="RestartPosition"/> along if it pointed at either of them.
    /// Returns the moved entry's new index, or -1 if the move would run off the list.</summary>
    public int MoveEntry(int index, int delta)
    {
        int target = index + delta;
        if (index < 0 || index >= Tokens.Count || target < 0 || target >= Tokens.Count)
        {
            return -1;
        }

        (Tokens[index], Tokens[target]) = (Tokens[target], Tokens[index]);

        int restartIndex = RestartPosition - 1;
        if (restartIndex == index)
        {
            RestartPosition = target + 1;
        }
        else if (restartIndex == target)
        {
            RestartPosition = index + 1;
        }

        return target;
    }

    private void ClampRestartPosition() =>
        RestartPosition = Math.Clamp(RestartPosition, 1, Math.Max(1, Tokens.Count));
}
