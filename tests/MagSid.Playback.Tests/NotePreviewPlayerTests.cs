using MagSid.Core.Model;
using MagSid.Playback;

namespace MagSid.Playback.Tests;

/// <summary>
/// The note audition has one job beyond making a sound: getting out of the way. An instrument
/// whose own release is seconds long would stack notes on top of each other while typing, so the
/// preview forces a short one - these render the envelope and check it actually decays, which is
/// not something the code reads as true just by looking at it.
/// </summary>
public class NotePreviewPlayerTests
{
    private const int SampleRate = 44100;

    private static Instrument LongReleaseInstrument() => new()
    {
        Number = 1,
        Waveform = 0x41,          // pulse
        AttackDecay = 0x00,       // fastest attack, so the note is at full level immediately
        SustainRelease = 0xFF,    // full sustain and the *longest* release the chip offers
    };

    /// <summary>Renders `milliseconds` of audio and returns the peak-to-peak amplitude of each
    /// 50ms window, which is what "is it still sounding" actually looks like for an oscillator.</summary>
    private static List<int> RenderWindows(NotePreviewPlayer player, int milliseconds)
    {
        const int windowMs = 50;
        int samplesPerWindow = SampleRate * windowMs / 1000;
        var windows = new List<int>();

        for (int elapsed = 0; elapsed < milliseconds; elapsed += windowMs)
        {
            var buffer = new byte[samplesPerWindow * 2];
            player.Read(buffer, 0, buffer.Length);

            int min = int.MaxValue, max = int.MinValue;
            for (int i = 0; i < buffer.Length; i += 2)
            {
                short sample = BitConverter.ToInt16(buffer, i);
                min = Math.Min(min, sample);
                max = Math.Max(max, sample);
            }
            windows.Add(max - min);
        }

        return windows;
    }

    [Fact]
    public void A_triggered_note_actually_makes_sound()
    {
        using var player = new NotePreviewPlayer();

        player.Trigger(NoteName.Parse("C-4"), LongReleaseInstrument());
        var windows = RenderWindows(player, 100);

        Assert.True(windows[0] > 1000, $"first 50ms should be clearly audible, peak-to-peak was {windows[0]}");
    }

    [Fact]
    public void The_note_dies_away_even_when_the_instrument_has_the_longest_release()
    {
        // SustainRelease 0xFF asks for a ~24 second release; the preview overrides the release
        // nibble so it is gone in well under a second. Without that override this test would
        // still be ringing at the end.
        using var player = new NotePreviewPlayer();

        player.Trigger(NoteName.Parse("C-4"), LongReleaseInstrument());
        var windows = RenderWindows(player, 900);

        int loudest = windows.Max();
        int settled = windows[^1];
        Assert.True(
            settled < loudest / 20,
            $"note should have faded by 900ms: peaked at {loudest}, still {settled} at the end");
    }

    [Fact]
    public void Nothing_sounds_until_a_note_is_triggered()
    {
        using var player = new NotePreviewPlayer();

        var windows = RenderWindows(player, 150);

        Assert.All(windows, window => Assert.True(window < 200, $"idle output should be quiet, was {window}"));
    }

    [Fact]
    public void Retriggering_while_a_note_rings_starts_the_new_one()
    {
        // Typing fast retriggers; the second note has to take over rather than being swallowed
        // because the first one still owns the voice.
        using var player = new NotePreviewPlayer();

        player.Trigger(NoteName.Parse("C-4"), LongReleaseInstrument());
        RenderWindows(player, 400); // let it get well into its release
        player.Trigger(NoteName.Parse("G-4"), LongReleaseInstrument());
        var afterRetrigger = RenderWindows(player, 50);

        Assert.True(afterRetrigger[0] > 1000, $"retriggered note should sound, peak-to-peak was {afterRetrigger[0]}");
    }
}
