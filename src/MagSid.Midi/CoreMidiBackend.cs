using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MagSid.Midi;

/// <summary>
/// macOS: CoreMIDI, called directly. One client and one input port for the life of the program;
/// opening a keyboard connects the port to that source. Packets arrive on CoreMIDI's own thread
/// as raw bytes and go through <see cref="MidiStreamParser"/>.
///
/// The client is made on a thread of its own that runs a CFRunLoop: CoreMIDI tells a client about
/// sources coming and going through the run loop it was made on, and without one the list of
/// sources never changes - a keyboard plugged in later would never appear.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class CoreMidiBackend : IMidiBackend
{
    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;

    private static MidiStreamParser? _parser;

    private uint _client;
    private uint _port;
    private readonly IntPtr _displayName;
    private readonly IntPtr _plainName;
    private uint _connected;

    public CoreMidiBackend()
    {
        // The keys' own values, kMIDIPropertyDisplayName and kMIDIPropertyName: the exported
        // constants read as null from here, and CoreMIDI then turns every query down (paramErr).
        _displayName = CreateString("displayName");
        _plainName = CreateString("name");
        IntPtr defaultMode = *(IntPtr*)NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFRunLoopDefaultMode");

        int status = 0;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            IntPtr name = CreateString("MagSID");
            try
            {
                var notify = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnNotification;
                status = MIDIClientCreate(name, notify, IntPtr.Zero, out _client);
                if (status == 0)
                {
                    var read = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnPackets;
                    status = MIDIInputPortCreate(_client, name, read, IntPtr.Zero, out _port);
                }
            }
            finally
            {
                CFRelease(name);
                ready.Set();
            }

            // A second at a time; a run loop with nothing in it returns at once, so then it rests.
            while (status == 0)
            {
                if (CFRunLoopRunInMode(defaultMode, 1.0, 0) == RunLoopFinished)
                    Thread.Sleep(100);
            }
        })
        {
            IsBackground = true,
            Name = "CoreMIDI",
        };
        thread.Start();
        ready.Wait();
        Check(status, "MIDIClientCreate");
    }

    private const int RunLoopFinished = 1;

    /// <summary>Setup changes; the client only needs them delivered for its list to stay current.</summary>
    [UnmanagedCallersOnly]
    private static void OnNotification(IntPtr message, IntPtr refCon)
    {
    }

    public IReadOnlyList<string> Sources()
    {
        var names = new List<string>();
        nuint count = MIDIGetNumberOfSources();
        for (nuint i = 0; i < count; i++)
            names.Add(NameOf(MIDIGetSource(i)) ?? $"MIDI source {i + 1}");
        return names;
    }

    public bool Connect(int index, Action<byte, byte, byte> message)
    {
        Disconnect();
        if (index < 0 || (nuint)index >= MIDIGetNumberOfSources())
            return false;

        uint source = MIDIGetSource((nuint)index);
        if (source == 0)
            return false;

        _parser = new MidiStreamParser(message);
        if (MIDIPortConnectSource(_port, source, IntPtr.Zero) != 0)
        {
            _parser = null;
            return false;
        }

        _connected = source;
        return true;
    }

    public void Disconnect()
    {
        if (_connected != 0)
            MIDIPortDisconnectSource(_port, _connected);
        _connected = 0;
        _parser = null;
    }

    /// <summary>
    /// A MIDIPacketList: a count, then packets of a timestamp, a length and that many bytes. Packets
    /// are packed to 4 bytes, and on Apple Silicon each next one starts on a 4-byte boundary
    /// (MIDIPacketNext); on Intel straight after the last.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void OnPackets(IntPtr list, IntPtr readRefCon, IntPtr sourceRefCon)
    {
        if (_parser is not { } parser)
            return;

        uint count = *(uint*)list;
        byte* packet = (byte*)list + 4;
        bool aligned = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        for (uint i = 0; i < count; i++)
        {
            ushort length = *(ushort*)(packet + 8);
            byte* data = packet + 10;
            parser.Feed(new ReadOnlySpan<byte>(data, length));
            packet = data + length;
            if (aligned)
                packet = (byte*)(((nuint)packet + 3) & ~(nuint)3);
        }
    }

    /// <summary>The name a keyboard shows in Audio MIDI Setup - its device's and its own together -
    /// or, for a virtual source, which has no device, its own plain name.</summary>
    private string? NameOf(uint endpoint) =>
        endpoint == 0 ? null : StringProperty(endpoint, _displayName) ?? StringProperty(endpoint, _plainName);

    private static string? StringProperty(uint endpoint, IntPtr property)
    {
        if (MIDIObjectGetStringProperty(endpoint, property, out IntPtr text) != 0 || text == IntPtr.Zero)
            return null;

        try
        {
            return ReadString(text);
        }
        finally
        {
            CFRelease(text);
        }
    }

    // ------------------------------------------------------------------ for the tests

    /// <summary>A virtual source of this program's own - how the tests play a keyboard on a
    /// machine with none attached.</summary>
    internal uint CreateVirtualSource(string name)
    {
        IntPtr text = CreateString(name);
        try
        {
            Check(MIDISourceCreate(_client, text, out uint source), "MIDISourceCreate");
            return source;
        }
        finally
        {
            CFRelease(text);
        }
    }

    /// <summary>Sends bytes out of a virtual source, as one packet, to whoever listens to it.</summary>
    internal static void Send(uint source, ReadOnlySpan<byte> bytes)
    {
        byte* list = stackalloc byte[4 + 8 + 2 + 256];
        *(uint*)list = 1;
        *(ulong*)(list + 4) = 0;
        *(ushort*)(list + 12) = (ushort)bytes.Length;
        bytes.CopyTo(new Span<byte>(list + 14, 256));
        Check(MIDIReceived(source, (IntPtr)list), "MIDIReceived");
    }

    internal static void Dispose(uint endpoint) => MIDIEndpointDispose(endpoint);

    // ------------------------------------------------------------------ CoreFoundation strings

    private static IntPtr CreateString(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* p = bytes)
            return CFStringCreateWithCString(IntPtr.Zero, p, Utf8);
    }

    private static string ReadString(IntPtr text)
    {
        nint size = CFStringGetLength(text) * 4 + 1;
        byte[] buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (CFStringGetCString(text, p, size, Utf8) == 0)
                return string.Empty;
        }

        return Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
    }

    private static void Check(int status, string call)
    {
        if (status != 0)
            throw new MidiException($"{call} failed: OSStatus {status}");
    }

    [DllImport(CoreMidi)]
    private static extern int MIDIClientCreate(IntPtr name, IntPtr notifyProc, IntPtr notifyRefCon, out uint client);

    [DllImport(CoreMidi)]
    private static extern int MIDIInputPortCreate(uint client, IntPtr portName, IntPtr readProc, IntPtr refCon, out uint port);

    [DllImport(CoreMidi)]
    private static extern nuint MIDIGetNumberOfSources();

    [DllImport(CoreMidi)]
    private static extern uint MIDIGetSource(nuint index);

    [DllImport(CoreMidi)]
    private static extern int MIDIObjectGetStringProperty(uint obj, IntPtr propertyId, out IntPtr text);

    [DllImport(CoreMidi)]
    private static extern int MIDIPortConnectSource(uint port, uint source, IntPtr connRefCon);

    [DllImport(CoreMidi)]
    private static extern int MIDIPortDisconnectSource(uint port, uint source);

    [DllImport(CoreMidi)]
    private static extern int MIDISourceCreate(uint client, IntPtr name, out uint source);

    [DllImport(CoreMidi)]
    private static extern int MIDIReceived(uint source, IntPtr packetList);

    [DllImport(CoreMidi)]
    private static extern int MIDIEndpointDispose(uint endpoint);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, byte* text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetLength(IntPtr text);

    [DllImport(CoreFoundation)]
    private static extern byte CFStringGetCString(IntPtr text, byte* buffer, nint size, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    [DllImport(CoreFoundation)]
    private static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, byte returnAfterSourceHandled);
}
