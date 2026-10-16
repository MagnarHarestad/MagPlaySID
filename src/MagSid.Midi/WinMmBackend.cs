using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MagSid.Midi;

/// <summary>Windows: the WinMM MIDI input API, as NAudio's MidiIn uses it.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WinMmBackend : IMidiBackend
{
    private const uint CallbackFunction = 0x00030000;
    private const uint MimData = 0x3C3;

    private static Action<byte, byte, byte>? _sink;
    private IntPtr _handle;

    public IReadOnlyList<string> Sources()
    {
        var names = new List<string>();
        uint count = midiInGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            var caps = default(MidiInCaps);
            names.Add(midiInGetDevCapsW(i, ref caps, (uint)Marshal.SizeOf<MidiInCaps>()) == 0 ? caps.Name : $"MIDI input {i + 1}");
        }

        return names;
    }

    public bool Connect(int index, Action<byte, byte, byte> message)
    {
        Disconnect();
        _sink = message;

        var callback = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr, void>)&OnMessage;
        if (midiInOpen(out _handle, (uint)index, callback, IntPtr.Zero, CallbackFunction) != 0)
        {
            _handle = IntPtr.Zero;
            _sink = null;
            return false;
        }

        if (midiInStart(_handle) != 0)
        {
            Disconnect();
            return false;
        }

        return true;
    }

    public void Disconnect()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            midiInStop(handle);
            midiInReset(handle);
            midiInClose(handle);
        }

        _sink = null;
    }

    /// <summary>The driver's callback: a short message comes packed into one word, status in the
    /// low byte.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnMessage(IntPtr handle, uint message, IntPtr instance, IntPtr param1, IntPtr param2)
    {
        if (message != MimData || _sink is not { } sink)
            return;

        uint packed = (uint)(long)param1;
        sink((byte)packed, (byte)((packed >> 8) & 0x7F), (byte)((packed >> 16) & 0x7F));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiInCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;

        public uint Support;
    }

    [DllImport("winmm.dll")]
    private static extern uint midiInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint midiInGetDevCapsW(nuint deviceId, ref MidiInCaps caps, uint size);

    [DllImport("winmm.dll")]
    private static extern uint midiInOpen(out IntPtr handle, uint deviceId, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern uint midiInStart(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern uint midiInStop(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern uint midiInReset(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern uint midiInClose(IntPtr handle);
}
