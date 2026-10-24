namespace MagSid.Core.Model;

/// <summary>
/// Drops patterns no channel's song-order references any more.
///
/// Nothing in the editor deletes a pattern: a song-order entry is only a reference, so removing
/// one leaves the pattern in the pool where typing its number again brings it straight back.
/// That is deliberate, but it means a pattern that fell out of use still gets assembled into the
/// .prg/.sid and costs real C64 bytes - so the export prunes them on the way out.
/// </summary>
public static class SongPruning
{
    /// <summary>A copy of <paramref name="song"/> containing only the patterns some channel
    /// still plays, renumbered to stay contiguous from 1 (Startup\Tables.s asserts that) with
    /// every order reference remapped to match.
    ///
    /// Returns a new Song rather than editing in place: this runs against the song the user has
    /// open, and pruning must never be something they can discover by finding their patterns
    /// gone. Nothing is shared with the original except the row and token records, which are
    /// immutable.</summary>
    public static Song WithUnusedPatternsRemoved(this Song song)
    {
        var referenced = song.ChannelOrders
            .SelectMany(order => order.Tokens)
            .OfType<PatternRefToken>()
            .Select(token => token.PatternNumber)
            .ToHashSet();

        var kept = song.Patterns
            .Where(pattern => referenced.Contains(pattern.Number))
            .OrderBy(pattern => pattern.Number)
            .ToList();

        // Old number -> new number, assigned in ascending order so the surviving patterns keep
        // their relative numbering rather than being shuffled.
        var renumbered = new Dictionary<int, int>();
        for (int i = 0; i < kept.Count; i++)
        {
            renumbered[kept[i].Number] = i + 1;
        }

        var result = new Song { Speed = song.Speed, FunkSpeed = song.FunkSpeed };

        foreach (var instrument in song.Instruments)
        {
            var copy = new Instrument
            {
                Number = instrument.Number,
                Name = instrument.Name,
                Waveform = instrument.Waveform,
                AttackDecay = instrument.AttackDecay,
                SustainRelease = instrument.SustainRelease,
                ArpeggioEnding = instrument.ArpeggioEnding,
                ArpeggioLoopPoint = instrument.ArpeggioLoopPoint,
                PulseWidth = instrument.PulseWidth,
                PulseSweep = instrument.PulseSweep,
                PulseContinuous = instrument.PulseContinuous,
                NoHardRestart = instrument.NoHardRestart,
                SoftHardRestart = instrument.SoftHardRestart,
                VibratoDelaySpeed = instrument.VibratoDelaySpeed,
                VibratoDepth = instrument.VibratoDepth,
            };
            copy.Wavetable.AddRange(instrument.Wavetable);
            copy.Arpeggio.AddRange(instrument.Arpeggio);
            copy.Filter.AddRange(instrument.Filter);
            result.Instruments.Add(copy);
        }

        foreach (var pattern in kept)
        {
            var copy = new Pattern { Number = renumbered[pattern.Number] };
            copy.Rows.AddRange(pattern.Rows);
            result.Patterns.Add(copy);
        }

        for (int ch = 0; ch < Song.ChannelCount; ch++)
        {
            var source = song.ChannelOrders[ch];
            var target = result.ChannelOrders[ch];
            target.RestartPosition = source.RestartPosition;

            foreach (var token in source.Tokens)
            {
                // A reference to a pattern that doesn't exist can't survive renumbering, so it
                // is dropped rather than carried through as a dangling number the assembler
                // would then reject. The editor clamps entries to real patterns, so this only
                // guards against a hand-edited file.
                if (token is PatternRefToken(var number) && renumbered.TryGetValue(number, out int mapped))
                {
                    target.Tokens.Add(new PatternRefToken(mapped));
                }
            }

            target.RestartPosition = Math.Clamp(target.RestartPosition, 1, Math.Max(1, target.Tokens.Count));
        }

        return result;
    }
}
