namespace MagSid.Midi;

/// <summary>
/// Turns a raw MIDI byte stream into whole channel messages - what CoreMIDI hands over, where one
/// packet may hold several messages, a message may lean on the status byte of the one before
/// (running status), and clock ticks may sit in the middle of a message.
///
/// Only channel messages come out. System exclusive is skipped to its end, and the one- and
/// two-byte system messages are dropped, since nothing here plays from them.
/// </summary>
internal sealed class MidiStreamParser(Action<byte, byte, byte> message)
{
    private byte _status;
    private int _needed;
    private readonly byte[] _data = new byte[2];
    private int _have;
    private bool _inSysEx;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            Feed(b);
    }

    public void Feed(byte b)
    {
        // Real-time messages (clock, start, stop, active sensing) may come between any two bytes
        // and do not disturb what is being gathered.
        if (b >= 0xF8)
            return;

        if (b >= 0x80)
        {
            _inSysEx = b == 0xF0;
            _have = 0;
            if (b >= 0xF0)
            {
                // System common: not a running status, and nothing here wants its data.
                _status = 0;
                _needed = 0;
                return;
            }

            _status = b;
            _needed = (b & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
            return;
        }

        if (_inSysEx || _status == 0)
            return;

        _data[_have++] = b;
        if (_have < _needed)
            return;

        message(_status, _data[0], _needed == 2 ? _data[1] : (byte)0);

        // Running status: the next data byte starts another message of the same kind.
        _have = 0;
    }
}
