using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MagSid.Midi;

/// <summary>
/// Linux: the ALSA sequencer, which sees USB keyboards and software ports alike (virtual
/// keyboards, the PipeWire and JACK bridges). One sequencer client with one input port for the life
/// of the program; opening a keyboard subscribes the port to it. A thread of its own waits on the
/// sequencer and reads its events, which come already decoded.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class AlsaSequencerBackend : IMidiBackend
{
    private const string Asound = "libasound.so.2";

    private const int OpenInput = 2;
    private const int OpenOutput = 1;
    private const int NonBlock = 1;
    private const uint CapRead = 1 << 0;
    private const uint CapWrite = 1 << 1;
    private const uint CapSubsRead = 1 << 5;
    private const uint CapSubsWrite = 1 << 6;
    private const uint TypeMidiGeneric = 1 << 1;
    private const uint TypeApplication = 1 << 20;
    private const short PollIn = 1;
    private const byte EventNoteOn = 6;
    private const byte EventNoteOff = 7;
    private const byte EventController = 10;
    private const int EventSize = 28;

    private readonly object _lock = new();
    private readonly IntPtr _seq;
    private readonly int _client;
    private readonly int _port;
    private (int Client, int Port)? _connected;
    private Action<byte, byte, byte>? _sink;
    private Thread? _reader;

    public AlsaSequencerBackend()
    {
        if (snd_seq_open(out _seq, "default", OpenInput, NonBlock) < 0)
            throw new MidiException("No ALSA sequencer (is /dev/snd/seq there?)");

        snd_seq_set_client_name(_seq, "MagSID");
        _client = snd_seq_client_id(_seq);
        _port = snd_seq_create_simple_port(_seq, "MagSID in", CapWrite | CapSubsWrite, TypeMidiGeneric | TypeApplication);
        if (_port < 0)
            throw new MidiException("Could not make an ALSA sequencer port.");
    }

    public IReadOnlyList<string> Sources() => [.. Enumerate().Select(s => s.Name)];

    /// <summary>Every port that can be read from and subscribed to, except the system's own and
    /// this program's.</summary>
    private List<(int Client, int Port, string Name)> Enumerate()
    {
        var found = new List<(int, int, string)>();
        lock (_lock)
        {
            snd_seq_client_info_malloc(out IntPtr clientInfo);
            snd_seq_port_info_malloc(out IntPtr portInfo);
            try
            {
                snd_seq_client_info_set_client(clientInfo, -1);
                while (snd_seq_query_next_client(_seq, clientInfo) >= 0)
                {
                    int client = snd_seq_client_info_get_client(clientInfo);
                    if (client == 0 || client == _client)
                        continue;

                    string clientName = Text(snd_seq_client_info_get_name(clientInfo));
                    snd_seq_port_info_set_client(portInfo, client);
                    snd_seq_port_info_set_port(portInfo, -1);
                    while (snd_seq_query_next_port(_seq, portInfo) >= 0)
                    {
                        uint caps = snd_seq_port_info_get_capability(portInfo);
                        if ((caps & (CapRead | CapSubsRead)) != (CapRead | CapSubsRead))
                            continue;

                        string portName = Text(snd_seq_port_info_get_name(portInfo));
                        string name = portName.StartsWith(clientName, StringComparison.Ordinal) ? portName : $"{clientName}: {portName}";
                        found.Add((client, snd_seq_port_info_get_port(portInfo), name));
                    }
                }
            }
            finally
            {
                snd_seq_port_info_free(portInfo);
                snd_seq_client_info_free(clientInfo);
            }
        }

        return found;
    }

    public bool Connect(int index, Action<byte, byte, byte> message)
    {
        Disconnect();
        var sources = Enumerate();
        if (index < 0 || index >= sources.Count)
            return false;

        var (client, port, _) = sources[index];
        lock (_lock)
        {
            if (snd_seq_connect_from(_seq, _port, client, port) < 0)
                return false;
            _connected = (client, port);
            _sink = message;
        }

        if (_reader is null)
        {
            _reader = new Thread(Read) { IsBackground = true, Name = "ALSA MIDI input" };
            _reader.Start();
        }

        return true;
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            if (_connected is { } from)
                snd_seq_disconnect_from(_seq, _port, from.Client, from.Port);
            _connected = null;
            _sink = null;
        }
    }

    /// <summary>Waits on the sequencer a tenth of a second at a time, and hands each note and
    /// controller event to whoever is listening, as the status and data bytes it stands for.</summary>
    private void Read()
    {
        int count = snd_seq_poll_descriptors_count(_seq, PollIn);
        var descriptors = new PollDescriptor[Math.Max(1, count)];
        fixed (PollDescriptor* fds = descriptors)
        {
            snd_seq_poll_descriptors(_seq, fds, (uint)descriptors.Length, PollIn);
            while (true)
            {
                poll(fds, (nuint)descriptors.Length, 100);
                lock (_lock)
                {
                    while (snd_seq_event_input(_seq, out IntPtr ev) >= 0)
                    {
                        if (_sink is { } sink && ev != IntPtr.Zero)
                            Deliver((byte*)ev, sink);
                    }
                }
            }
        }
    }

    private static void Deliver(byte* ev, Action<byte, byte, byte> sink)
    {
        // snd_seq_event_t: type, flags, tag, queue, an 8-byte time, source and destination
        // addresses, then the event's data from byte 16.
        byte channel = (byte)(ev[16] & 0x0F);
        switch (ev[0])
        {
            case EventNoteOn:
                sink((byte)(0x90 | channel), ev[17], ev[18]);
                break;
            case EventNoteOff:
                sink((byte)(0x80 | channel), ev[17], ev[18]);
                break;
            case EventController:
                sink((byte)(0xB0 | channel), (byte)(*(uint*)(ev + 20) & 0x7F), (byte)(*(int*)(ev + 24) & 0x7F));
                break;
        }
    }

    private static string Text(IntPtr utf8) => Marshal.PtrToStringUTF8(utf8) ?? string.Empty;

    // ------------------------------------------------------------------ for the tests

    /// <summary>A sequencer client of its own with a readable port - how the tests play a keyboard
    /// on a machine with none attached. Its events go to whoever subscribed, this backend included.</summary>
    internal sealed class TestKeyboard : IDisposable
    {
        private readonly IntPtr _seq;
        private readonly int _port;

        public TestKeyboard(string name)
        {
            if (snd_seq_open(out _seq, "default", OpenOutput, 0) < 0)
                throw new MidiException("No ALSA sequencer.");
            snd_seq_set_client_name(_seq, name);
            _port = snd_seq_create_simple_port(_seq, name, CapRead | CapSubsRead, TypeMidiGeneric | TypeApplication);
        }

        public void Send(byte type, byte channel, byte data1, byte data2, int value = 0)
        {
            byte* ev = stackalloc byte[EventSize];
            new Span<byte>(ev, EventSize).Clear();
            ev[0] = type;
            ev[3] = 253;                // SND_SEQ_QUEUE_DIRECT
            ev[13] = (byte)_port;       // source port
            ev[14] = 254;               // to the subscribers
            ev[15] = 253;
            ev[16] = channel;
            if (type == EventController)
            {
                *(uint*)(ev + 20) = data1;
                *(int*)(ev + 24) = value;
            }
            else
            {
                ev[17] = data1;
                ev[18] = data2;
            }

            snd_seq_event_output_direct(_seq, (IntPtr)ev);
        }

        public void Dispose() => snd_seq_close(_seq);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int Fd;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport(Asound)]
    private static extern int snd_seq_open(out IntPtr seq, string name, int streams, int mode);

    [DllImport(Asound)]
    private static extern int snd_seq_close(IntPtr seq);

    [DllImport(Asound)]
    private static extern int snd_seq_set_client_name(IntPtr seq, string name);

    [DllImport(Asound)]
    private static extern int snd_seq_client_id(IntPtr seq);

    [DllImport(Asound)]
    private static extern int snd_seq_create_simple_port(IntPtr seq, string name, uint caps, uint type);

    [DllImport(Asound)]
    private static extern int snd_seq_client_info_malloc(out IntPtr info);

    [DllImport(Asound)]
    private static extern void snd_seq_client_info_free(IntPtr info);

    [DllImport(Asound)]
    private static extern void snd_seq_client_info_set_client(IntPtr info, int client);

    [DllImport(Asound)]
    private static extern int snd_seq_client_info_get_client(IntPtr info);

    [DllImport(Asound)]
    private static extern IntPtr snd_seq_client_info_get_name(IntPtr info);

    [DllImport(Asound)]
    private static extern int snd_seq_query_next_client(IntPtr seq, IntPtr info);

    [DllImport(Asound)]
    private static extern int snd_seq_port_info_malloc(out IntPtr info);

    [DllImport(Asound)]
    private static extern void snd_seq_port_info_free(IntPtr info);

    [DllImport(Asound)]
    private static extern void snd_seq_port_info_set_client(IntPtr info, int client);

    [DllImport(Asound)]
    private static extern void snd_seq_port_info_set_port(IntPtr info, int port);

    [DllImport(Asound)]
    private static extern int snd_seq_port_info_get_port(IntPtr info);

    [DllImport(Asound)]
    private static extern uint snd_seq_port_info_get_capability(IntPtr info);

    [DllImport(Asound)]
    private static extern IntPtr snd_seq_port_info_get_name(IntPtr info);

    [DllImport(Asound)]
    private static extern int snd_seq_query_next_port(IntPtr seq, IntPtr info);

    [DllImport(Asound)]
    private static extern int snd_seq_connect_from(IntPtr seq, int myPort, int sourceClient, int sourcePort);

    [DllImport(Asound)]
    private static extern int snd_seq_disconnect_from(IntPtr seq, int myPort, int sourceClient, int sourcePort);

    [DllImport(Asound)]
    private static extern int snd_seq_poll_descriptors_count(IntPtr seq, short events);

    [DllImport(Asound)]
    private static extern int snd_seq_poll_descriptors(IntPtr seq, PollDescriptor* fds, uint space, short events);

    [DllImport(Asound)]
    private static extern int snd_seq_event_input(IntPtr seq, out IntPtr ev);

    [DllImport(Asound)]
    private static extern int snd_seq_event_output_direct(IntPtr seq, IntPtr ev);

    [DllImport("libc")]
    private static extern int poll(PollDescriptor* fds, nuint count, int timeout);
}
