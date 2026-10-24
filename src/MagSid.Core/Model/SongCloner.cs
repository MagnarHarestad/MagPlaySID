namespace MagSid.Core.Model;

/// <summary>
/// A full, independent copy of a song. Only the mutable containers are actually copied: every
/// <see cref="PatternRow"/> and <see cref="OrderToken"/> is an immutable record and
/// <see cref="FilterStep"/> a readonly struct, so those are shared rather than duplicated and a
/// clone stays cheap enough to take on every keystroke - which is what the editor's undo history
/// does with it.
/// </summary>
public static class SongCloner
{
    public static Song Clone(Song song)
    {
        var copy = new Song { Speed = song.Speed, FunkSpeed = song.FunkSpeed };

        foreach (var instrument in song.Instruments)
            copy.Instruments.Add(Clone(instrument));

        foreach (var pattern in song.Patterns)
        {
            var clonedPattern = new Pattern { Number = pattern.Number };
            clonedPattern.Rows.AddRange(pattern.Rows);
            copy.Patterns.Add(clonedPattern);
        }

        for (int channel = 0; channel < Song.ChannelCount; channel++)
        {
            var source = song.ChannelOrders[channel];
            var target = copy.ChannelOrders[channel];
            target.Tokens.AddRange(source.Tokens);
            target.RestartPosition = source.RestartPosition;
        }

        return copy;
    }

    public static Instrument Clone(Instrument instrument)
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
        return copy;
    }
}
