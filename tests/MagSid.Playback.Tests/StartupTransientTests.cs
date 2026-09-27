using MagSid.Core.Model;
using MagSid.Playback;

namespace MagSid.Playback.Tests;

/// <summary>
/// Both players open a sound card and immediately hand it whatever the chip produces. reSID's
/// external filter starts from zero, so the volume-register write at construction is a DC step
/// worth ~15000 peak-to-peak over the first 50ms - which comes out of the speakers as a thump
/// on every Play press and on the first note typed. These pin down that it is clocked out
/// before anything reaches the output.
/// </summary>
public class StartupTransientTests
{
    private const int SampleRate = 44100;

    private static int FirstWindowPeakToPeak(NAudio.Wave.IWaveProvider provider)
    {
        var buffer = new byte[SampleRate / 20 * 2]; // 50ms, mono 16-bit
        provider.Read(buffer, 0, buffer.Length);

        int min = int.MaxValue, max = int.MinValue;
        for (int i = 0; i < buffer.Length; i += 2)
        {
            short sample = BitConverter.ToInt16(buffer, i);
            min = Math.Min(min, sample);
            max = Math.Max(max, sample);
        }
        return max - min;
    }

    /// <summary>A song that plays nothing, so any output is the chip settling rather than a note.</summary>
    private static Song SilentSong()
    {
        var song = new Song { Speed = 6 };
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x11, AttackDecay = 0, SustainRelease = 0 });
        song.Patterns.Add(Pattern.CreateEmpty(1));
        foreach (var order in song.ChannelOrders)
        {
            order.Tokens.Add(new PatternRefToken(1));
        }
        return song;
    }

    [Fact]
    public void Song_playback_does_not_thump_when_it_starts()
    {
        int peakToPeak = FirstWindowPeakToPeak(new SongPlaybackEngine(SilentSong()));

        Assert.True(peakToPeak < 200, $"first 50ms of a silent song should be quiet, was {peakToPeak}");
    }

    [Fact]
    public void Note_preview_does_not_thump_when_it_starts()
    {
        using var player = new NotePreviewPlayer();

        int peakToPeak = FirstWindowPeakToPeak(player);

        Assert.True(peakToPeak < 200, $"first 50ms before any note should be quiet, was {peakToPeak}");
    }

    /// <summary>Row 0's own trigger has to land at sample 0, not one whole frame (~882 samples
    /// at 44.1kHz/50Hz) later. A short first note - a two-row kick hit, ~80ms total - loses most
    /// of its audible attack to that one frame of silent lead-in stacked on top of the two-frame
    /// hard-reset arm sequence every trigger already takes; a long-held first note survives it
    /// unnoticed, which is why this only ever showed up on a short percussive channel.</summary>
    [Fact]
    public void The_first_row_triggers_at_sample_zero_not_one_frame_late()
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x41, AttackDecay = 0x00, SustainRelease = 0xf0 });
        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), 1));
        pattern.Rows.Add(new HoldRow());
        song.Patterns.Add(pattern);
        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        var engine = new SongPlaybackEngine(song);
        var buffer = new byte[SampleRate / 20 * 2]; // 50ms
        engine.Read(buffer, 0, buffer.Length);

        Assert.True(engine.TryDequeueRowEvent(out var firstEvent), "row 0 should have triggered");
        Assert.Equal(0, firstEvent.SampleIndex);
    }
}
