using MagSid.Core.Model;
using MagSid.Midi;
using Xunit.Abstractions;

namespace MagSid.Midi.Tests;

/// <summary>
/// The real system path, end to end, with no keyboard attached: a virtual keyboard of the test's
/// own plays into the system's MIDI, and MidiInput has to find it by name, open it and hear it.
/// Each system's test runs on that system's CI runner and says so when it cannot.
/// </summary>
public class MidiLoopbackTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    [Fact]
    public void CoreMidi_hears_a_virtual_source()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var backend = Assert.IsType<CoreMidiBackend>(MidiBackend.Current);
        uint source = backend.CreateVirtualSource("MagSID test keyboard");
        try
        {
            using var heard = new Heard();
            using var input = heard.Input;
            Assert.True(input.Open(IndexOf("MagSID test keyboard")), "the virtual source would not open");

            CoreMidiBackend.Send(source, [0x90, 60, 100]);
            CoreMidiBackend.Send(source, [0xB0, 74, 99, 0x80, 60, 0]);   // two messages in one packet

            heard.WaitFor(pressed: 1, released: 1, controls: 1);
            Assert.Equal((NoteName.Parse("C-4"), 100), heard.Pressed.Single());
            Assert.Equal(NoteName.Parse("C-4"), heard.Released.Single());
            Assert.Equal((74, 99), heard.Controls.Single());
        }
        finally
        {
            CoreMidiBackend.Dispose(source);
        }
    }

    [Fact]
    public void Alsa_hears_another_sequencer_client()
    {
        if (!OperatingSystem.IsLinux())
            return;
        if (!File.Exists("/dev/snd/seq"))
        {
            output.WriteLine("No /dev/snd/seq on this machine: the ALSA loopback was not run.");
            return;
        }

        Assert.IsType<AlsaSequencerBackend>(MidiBackend.Current);
        using var keyboard = new AlsaSequencerBackend.TestKeyboard("MagSID test keyboard");
        using var heard = new Heard();
        using var input = heard.Input;
        Assert.True(input.Open(IndexOf("MagSID test keyboard")), "the test client would not open");

        keyboard.Send(6, 0, 64, 90);         // note on, E-4
        keyboard.Send(10, 3, 71, 0, 42);     // controller 71 = 42
        keyboard.Send(6, 0, 64, 0);          // note on at velocity 0: a release

        heard.WaitFor(pressed: 1, released: 1, controls: 1);
        Assert.Equal((NoteName.Parse("E-4"), 90), heard.Pressed.Single());
        Assert.Equal(NoteName.Parse("E-4"), heard.Released.Single());
        Assert.Equal((71, 42), heard.Controls.Single());
        output.WriteLine("ALSA loopback ran.");
    }

    [Fact]
    public void Listing_inputs_never_throws()
    {
        var devices = MidiInput.Devices();
        output.WriteLine($"{devices.Count} MIDI input(s): {string.Join(", ", devices)}");
    }

    private static int IndexOf(string name)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            int index = MidiInput.Devices().ToList().FindIndex(n => n.Contains(name, StringComparison.Ordinal));
            if (index >= 0)
                return index;
            Assert.True(DateTime.UtcNow < deadline, $"'{name}' never showed up among {string.Join(", ", MidiInput.Devices())}");
            Thread.Sleep(50);
        }
    }

    /// <summary>A MidiInput whose events land in lists, from whichever thread delivers them.</summary>
    private sealed class Heard : IDisposable
    {
        private readonly object _lock = new();

        public Heard()
        {
            Input = new MidiInput(action =>
            {
                lock (_lock)
                    action();
            });
            Input.NotePressed += (note, velocity) => Pressed.Add((note, velocity));
            Input.NoteReleased += Released.Add;
            Input.ControlChanged += (controller, value) => Controls.Add((controller, value));
        }

        public MidiInput Input { get; }

        public List<(NoteName, int)> Pressed { get; } = [];

        public List<NoteName> Released { get; } = [];

        public List<(int, int)> Controls { get; } = [];

        public void WaitFor(int pressed, int released, int controls)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock)
                {
                    if (Pressed.Count >= pressed && Released.Count >= released && Controls.Count >= controls)
                        return;
                }
                Thread.Sleep(20);
            }

            lock (_lock)
                Assert.Fail($"heard {Pressed.Count} presses, {Released.Count} releases, {Controls.Count} controllers");
        }

        public void Dispose() => Input.Dispose();
    }
}
