using MagSid.Core.Model;
using NAudio.Midi;

namespace MagSid.Editor.App.Midi;

/// <summary>
/// One MIDI input device, turned into events the editor can use.
///
/// NAudio raises its messages on a driver callback thread, and touching a WinForms control from
/// there is the kind of fault that shows up as a hang weeks later rather than an exception now - so
/// everything here is marshalled onto the UI thread before it leaves.
///
/// Keys outside the player's own C-0..B-7 raise nothing at all. A five-octave keyboard reaches past
/// the frequency table at both ends, and folding those keys into the nearest octave would make the
/// instrument play a note the player never asked for.
/// </summary>
internal sealed class MidiInputService : IDisposable
{
    private readonly Control _marshal;
    private MidiIn? _input;

    /// <param name="marshal">Any control on the UI thread; used only to hop back onto it.</param>
    public MidiInputService(Control marshal) => _marshal = marshal;

    public event Action<NoteName, int>? NotePressed;

    public event Action<NoteName>? NoteReleased;

    /// <summary>Controller number and its 0..127 value. The channel is deliberately ignored: a
    /// keyboard's knobs send on whatever channel it is set to, and a binding that stopped working
    /// because that setting moved would be a puzzle rather than a feature.</summary>
    public event Action<int, int>? ControlChanged;

    /// <summary>Null when nothing is open.</summary>
    public string? DeviceName { get; private set; }

    public bool IsOpen => _input is not null;

    public static IReadOnlyList<string> Devices()
    {
        var names = new List<string>();

        try
        {
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
                names.Add(MidiIn.DeviceInfo(i).ProductName);
        }
        catch (Exception)
        {
            // A machine with no MIDI stack at all answers by throwing rather than by saying zero.
        }

        return names;
    }

    /// <summary>Opens a device by index. Returns false rather than throwing when it is already
    /// taken by another program, which is the ordinary case and not an error worth a dialog.</summary>
    public bool Open(int deviceIndex)
    {
        Close();

        try
        {
            _input = new MidiIn(deviceIndex);
            _input.MessageReceived += OnMessage;
            _input.Start();
            DeviceName = MidiIn.DeviceInfo(deviceIndex).ProductName;
            return true;
        }
        catch (Exception)
        {
            Close();
            return false;
        }
    }

    public void Close()
    {
        var input = _input;
        _input = null;
        DeviceName = null;

        if (input is null)
            return;

        try
        {
            input.Stop();
            input.MessageReceived -= OnMessage;
        }
        catch (Exception)
        {
            // Unplugged mid-session; there is nothing left to stop.
        }

        input.Dispose();
    }

    private void OnMessage(object? sender, MidiInMessageEventArgs e)
    {
        switch (e.MidiEvent)
        {
            // A note-on at velocity zero is a note-off. Keyboards send it either way and a player
            // that only understood one of them would leave notes hanging on half the hardware
            // in the world.
            case NoteOnEvent { Velocity: > 0 } on when MidiMapping.NoteFor(on.NoteNumber) is { } note:
                Post(() => NotePressed?.Invoke(note, on.Velocity));
                break;

            case NoteEvent note2 when note2.CommandCode is MidiCommandCode.NoteOff or MidiCommandCode.NoteOn
                                      && MidiMapping.NoteFor(note2.NoteNumber) is { } note3:
                Post(() => NoteReleased?.Invoke(note3));
                break;

            case ControlChangeEvent change:
                Post(() => ControlChanged?.Invoke((int)change.Controller, change.ControllerValue));
                break;
        }
    }

    /// <summary>Onto the UI thread, and never onto a window that has already gone.</summary>
    private void Post(Action action)
    {
        if (_marshal.IsDisposed || !_marshal.IsHandleCreated)
            return;

        try
        {
            _marshal.BeginInvoke(action);
        }
        catch (Exception)
        {
            // The window closed between the check and the post.
        }
    }

    public void Dispose() => Close();
}
