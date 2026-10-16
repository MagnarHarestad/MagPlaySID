using MagSid.Core.Model;

namespace MagSid.Midi;

/// <summary>
/// One MIDI input, turned into events the editor can use - the same events, and the same rules,
/// as the Windows editor's MidiInputService, on every system.
///
/// The system delivers on a thread of its own; every event is handed to <c>post</c> first, which
/// takes it onto the UI thread. Keys outside the player's C-0..B-7 raise nothing.
/// </summary>
public sealed class MidiInput : IDisposable
{
    private readonly Action<Action> _post;
    private bool _open;

    /// <param name="post">Runs an action on the UI thread - Dispatcher.UIThread.Post, say.</param>
    public MidiInput(Action<Action> post) => _post = post;

    public event Action<NoteName, int>? NotePressed;

    public event Action<NoteName>? NoteReleased;

    /// <summary>Controller number and its 0..127 value; the channel is ignored, as in the Windows
    /// editor, so a binding survives the keyboard being set to another channel.</summary>
    public event Action<int, int>? ControlChanged;

    /// <summary>Null when nothing is open.</summary>
    public string? DeviceName { get; private set; }

    public bool IsOpen => _open;

    /// <summary>The inputs there are now - read again each time, so a keyboard plugged in
    /// mid-session shows up. Empty where the system has no MIDI.</summary>
    public static IReadOnlyList<string> Devices()
    {
        try
        {
            return MidiBackend.Current?.Sources() ?? [];
        }
        catch (Exception ex) when (ex is MidiException or DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    /// <summary>Opens a device by its place in <see cref="Devices"/>. False rather than an
    /// exception when it cannot be had - another program holding it is the ordinary case.</summary>
    public bool Open(int index)
    {
        Close();

        if (MidiBackend.Current is not { } backend)
            return false;

        var names = Devices();
        if (index < 0 || index >= names.Count)
            return false;

        try
        {
            if (!backend.Connect(index, Dispatch))
                return false;
        }
        catch (Exception ex) when (ex is MidiException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }

        _open = true;
        DeviceName = names[index];
        return true;
    }

    public void Close()
    {
        if (!_open)
            return;

        _open = false;
        DeviceName = null;
        try
        {
            MidiBackend.Current?.Disconnect();
        }
        catch (MidiException)
        {
            // Unplugged mid-session; there is nothing left to disconnect.
        }
    }

    /// <summary>A channel message, from whichever backend - turned into the editor's events.</summary>
    internal void Dispatch(byte status, byte data1, byte data2)
    {
        switch (status & 0xF0)
        {
            // A note-on at velocity zero is a note-off; keyboards send it either way.
            case 0x90 when data2 > 0:
                if (MidiMapping.NoteFor(data1) is { } on)
                    _post(() => NotePressed?.Invoke(on, data2));
                break;
            case 0x90:
            case 0x80:
                if (MidiMapping.NoteFor(data1) is { } off)
                    _post(() => NoteReleased?.Invoke(off));
                break;
            case 0xB0:
                _post(() => ControlChanged?.Invoke(data1, data2));
                break;
        }
    }

    public void Dispose() => Close();
}
