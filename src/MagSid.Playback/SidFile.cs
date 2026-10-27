using System.Text;

namespace MagSid.Playback;

/// <summary>
/// A parsed PSID/RSID file: where its data loads, where init and play are, and how often play wants
/// calling.
///
/// Only what is needed to play one back. The editor writes these itself (see SidExporter), so the
/// first thing this has to get right is reading its own output - but the fields are the format's, not
/// this project's, so an ordinary .sid from elsewhere loads too.
/// </summary>
public sealed class SidFile
{
    private SidFile()
    {
    }

    /// <summary>PSID or RSID. RSID tunes expect a real C64 environment - a running KERNAL, an IRQ
    /// they install themselves - which this player does not provide.</summary>
    public bool IsRsid { get; private init; }

    public int Version { get; private init; }

    public string Name { get; private init; } = string.Empty;

    public string Author { get; private init; } = string.Empty;

    public string Released { get; private init; } = string.Empty;

    public int LoadAddress { get; private init; }

    public int InitAddress { get; private init; }

    public int PlayAddress { get; private init; }

    public int Songs { get; private init; }

    public int StartSong { get; private init; }

    /// <summary>One bit per song, low bit first: 0 means play once per vertical blank, 1 means
    /// play is driven by CIA 1's timer instead - see PlayRate.</summary>
    public uint Speed { get; private init; }

    /// <summary>Whether <paramref name="song"/> (counting from 1) is played on the CIA timer. Songs
    /// past the 32nd share the last bit, as the format has it.</summary>
    public bool UsesCiaTimer(int song) => ((Speed >> (Math.Clamp(song, 1, 32) - 1)) & 1) != 0;

    /// <summary>The tune's own bytes, to be placed at <see cref="LoadAddress"/>.</summary>
    public byte[] Data { get; private init; } = [];

    /// <summary>True when the header says 8580 rather than 6581.</summary>
    public bool Is8580 { get; private init; }

    /// <summary>The header's video standard, bits 2-3 of its flags: 1 PAL, 2 NTSC, 3 either, 0 unknown.</summary>
    public int VideoStandard { get; private init; }

    /// <summary>The tune wants NTSC timing: its header says NTSC and not PAL too.</summary>
    public bool IsNtsc => VideoStandard == 2;

    /// <summary>An RSID whose init is a BASIC program to RUN, not machine code to call.</summary>
    public bool IsBasic { get; private init; }

    /// <summary>The page the header says a driver may use, and how many pages from it; 0 and 0
    /// when it says nothing, $FF when no page is free.</summary>
    public int FreePage { get; private init; }

    public int FreePages { get; private init; }

    /// <summary>Where the header puts a second and a third SID (v3 and v4 headers), as addresses
    /// such as $D420 or $DE00; empty for a tune written for one chip.</summary>
    public IReadOnlyList<int> ExtraSids { get; private init; } = [];

    /// <summary>Whether each of <see cref="ExtraSids"/> is an 8580 - its own bits of the header's
    /// flags, or the first chip's model where they leave it unknown.</summary>
    public IReadOnlyList<bool> ExtraSids8580 { get; private init; } = [];

    public static SidFile Parse(byte[] bytes)
    {
        if (bytes.Length < 0x76 + 2)
            throw new SidPlaybackException("Too short to be a .sid file - the header alone is 118 bytes.");

        string magic = Encoding.ASCII.GetString(bytes, 0, 4);
        if (magic is not ("PSID" or "RSID"))
            throw new SidPlaybackException($"Not a .sid file: it starts with \"{magic}\", not PSID or RSID.");

        int version = Be16(bytes, 0x04);
        int dataOffset = Be16(bytes, 0x06);
        if (dataOffset < 0x76 || dataOffset > bytes.Length)
            throw new SidPlaybackException($"The header says its data starts at {dataOffset}, which is outside the file.");

        int loadAddress = Be16(bytes, 0x08);
        int initAddress = Be16(bytes, 0x0A);
        int playAddress = Be16(bytes, 0x0C);

        var data = bytes[dataOffset..];

        // A load address of 0 means the real one is the first two bytes of the data, little-endian,
        // exactly as a .prg carries it.
        if (loadAddress == 0)
        {
            if (data.Length < 2)
                throw new SidPlaybackException("The header defers the load address to the data, but there is none.");

            loadAddress = data[0] | (data[1] << 8);
            data = data[2..];
        }

        int flags = version >= 2 && bytes.Length >= 0x78 ? Be16(bytes, 0x76) : 0;

        return new SidFile
        {
            IsRsid = magic == "RSID",
            Version = version,
            Name = AsciiField(bytes, 0x16),
            Author = AsciiField(bytes, 0x36),
            Released = AsciiField(bytes, 0x56),
            LoadAddress = loadAddress,
            InitAddress = initAddress == 0 ? loadAddress : initAddress,
            PlayAddress = playAddress,
            Songs = Math.Max(1, Be16(bytes, 0x0E)),
            StartSong = Math.Max(1, Be16(bytes, 0x10)),
            Speed = (uint)((bytes[0x12] << 24) | (bytes[0x13] << 16) | (bytes[0x14] << 8) | bytes[0x15]),
            Data = data,
            // Bits 4-5: 01 = MOS6581, 10 = MOS8580.
            Is8580 = ((flags >> 4) & 0b11) == 0b10,
            VideoStandard = (flags >> 2) & 0b11,
            IsBasic = magic == "RSID" && (flags & 0b10) != 0,
            FreePage = version >= 2 && bytes.Length >= 0x7A ? bytes[0x78] : 0,
            FreePages = version >= 2 && bytes.Length >= 0x7A ? bytes[0x79] : 0,
            ExtraSids = [.. ExtraSidFields(version, bytes).Select(field => 0xD000 | (bytes[field.Address] << 4))],
            ExtraSids8580 = [.. ExtraSidFields(version, bytes).Select(field => ((flags >> field.ModelBits) & 0b11) switch
            {
                0b10 => true,
                0b01 => false,
                _ => ((flags >> 4) & 0b11) == 0b10,
            })],
        };
    }

    /// <summary>The header's second and third SID: the version they came in, the byte with the
    /// address, the flags bits with the model - those that name a SID.</summary>
    private static IEnumerable<(int Version, int Address, int ModelBits)> ExtraSidFields(int version, byte[] bytes) =>
        new[] { (Version: 3, Address: 0x7A, ModelBits: 6), (Version: 4, Address: 0x7B, ModelBits: 8) }
            .Where(field => version >= field.Version && bytes.Length > field.Address && IsSidPage(bytes[field.Address]));

    /// <summary>A header's SID address byte: $Dxx0 with xx even, from $D420 to $D7E0 or in the
    /// I/O areas at $DE00-$DFE0.</summary>
    private static bool IsSidPage(byte value) => (value & 1) == 0 && value is >= 0x42 and <= 0x7F or >= 0xE0 and <= 0xFE;

    /// <summary>What this player can and cannot handle, in the user's terms. Null when it can.</summary>
    public string? UnsupportedReason()
    {
        if (IsRsid)
        {
            return "This is an RSID file. Those expect a real C64 environment - KERNAL, an interrupt " +
                   "the tune installs itself - which this player does not provide.";
        }

        if (PlayAddress == 0)
        {
            return "The tune has no play address, so it installs its own interrupt handler. This " +
                   "player calls play() once per frame instead and cannot drive it.";
        }

        if (LoadAddress + Data.Length - 1 > 0xFFFF)
            return "The tune's data would run past the end of memory.";

        return null;
    }

    private static int Be16(byte[] bytes, int offset) => (bytes[offset] << 8) | bytes[offset + 1];

    private static string AsciiField(byte[] bytes, int offset)
    {
        int end = offset;
        while (end < offset + 32 && end < bytes.Length && bytes[end] != 0)
            end++;

        return Encoding.ASCII.GetString(bytes, offset, end - offset).TrimEnd();
    }
}
