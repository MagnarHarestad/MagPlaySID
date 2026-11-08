using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MagSid.Audio.PortAudio;

/// <summary>
/// Tells when the system's default output device changes - in Windows' sound settings, from the
/// tray, or as a headset is switched on or a USB interface unplugged - so the sound can follow it.
/// On Windows, the multimedia device API's own notice; on a Mac, Core Audio's default output looked
/// at once a second. On Linux, PulseAudio and PipeWire move a playing stream to the new default
/// themselves, so nothing is needed there.
/// A burst of notices - Windows sends one for each role a device takes - is answered once, a moment
/// after the last.
/// </summary>
internal static class DefaultDeviceWatcher
{
    private static Timer? _settle;
    private static Action? _changed;

    // Kept alive: the system holds only native pointers to them.
    private static object? _enumerator;
    private static NotificationClient? _client;
    private static Timer? _poll;
    private static uint _macDevice;

    public static void Start(Action changed)
    {
        if (_changed is not null)
            return;
        _changed = changed;
        _settle = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            if (OperatingSystem.IsWindows())
                // On a thread of the pool - the multithreaded apartment - so Windows calls back on its own
                // thread rather than the window's.
                Task.Run(StartWindows).Wait();
            else if (OperatingSystem.IsMacOS())
            {
                _macDevice = MacDefaultOutput();
                _poll = new Timer(_ => PollMac(), null, 1000, 1000);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException or AggregateException)
        {
            // Without the notice the sound stays where it started, as before.
            Trace.WriteLine($"MagPlaySID: the default output device cannot be watched: {ex.Message}");
        }
    }

    /// <summary>A change noticed: answered once, a quarter second after the last of a burst.</summary>
    private static void Changed() => _settle?.Change(250, Timeout.Infinite);

    private static void Fire()
    {
        try
        {
            _changed?.Invoke();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"MagPlaySID: moving to the new output device failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ Windows

    private static void StartWindows()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        var client = new NotificationClient();
        Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(client));
        (_enumerator, _client) = (enumerator, client);
    }

    private const int Render = 0;
    private const int RoleConsole = 0, RoleMultimedia = 1;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IMMNotificationClient client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);

        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [ComVisible(true)]
    private sealed class NotificationClient : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, int newState)
        {
        }

        public void OnDeviceAdded(string deviceId)
        {
        }

        public void OnDeviceRemoved(string deviceId)
        {
        }

        public void OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
        {
            if (flow == Render && role is RoleConsole or RoleMultimedia)
                Changed();
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
        }
    }

    // ------------------------------------------------------------------ macOS

    private static void PollMac()
    {
        uint device = MacDefaultOutput();
        if (device != 0 && device != _macDevice)
        {
            _macDevice = device;
            Changed();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [DllImport("/System/Library/Frameworks/CoreAudio.framework/CoreAudio")]
    private static extern int AudioObjectGetPropertyData(uint objectId, ref AudioObjectPropertyAddress address, uint qualifierSize,
                                                         IntPtr qualifier, ref uint dataSize, out uint data);

    /// <summary>Core Audio's default output device: kAudioHardwarePropertyDefaultOutputDevice of the
    /// system object.</summary>
    private static uint MacDefaultOutput()
    {
        var address = new AudioObjectPropertyAddress { Selector = 0x644F7574, Scope = 0x676C6F62, Element = 0 };
        uint size = sizeof(uint);
        return AudioObjectGetPropertyData(1, ref address, 0, IntPtr.Zero, ref size, out uint device) == 0 ? device : 0;
    }
}
