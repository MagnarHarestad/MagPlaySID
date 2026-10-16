namespace MagSid.Midi;

/// <summary>
/// One system's MIDI input: what can be listened to, and listening to one of them. Messages
/// arrive on whatever thread the system delivers them on, as status and up to two data bytes.
/// </summary>
internal interface IMidiBackend
{
    IReadOnlyList<string> Sources();

    /// <summary>Starts delivering the source's messages; false when it cannot be opened.</summary>
    bool Connect(int index, Action<byte, byte, byte> message);

    void Disconnect();
}

internal static class MidiBackend
{
    private static readonly Lazy<IMidiBackend?> Instance = new(Create);

    /// <summary>The system's backend, or null where it has no MIDI at all - a Linux machine
    /// without libasound, say. Made once: CoreMIDI and ALSA both expect one client per program.</summary>
    public static IMidiBackend? Current => Instance.Value;

    private static IMidiBackend? Create()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return new WinMmBackend();
            if (OperatingSystem.IsMacOS())
                return new CoreMidiBackend();
            if (OperatingSystem.IsLinux())
                return new AlsaSequencerBackend();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MidiException)
        {
        }

        return null;
    }
}

public sealed class MidiException(string message) : Exception(message);
