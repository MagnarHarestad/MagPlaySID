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
    /// flags, or the first chip's model where they leave it unknown; in a v5 header, all the first's.</summary>
    public IReadOnlyList<bool> ExtraSids8580 { get; private init; } = [];

    /// <summary>Where each SID is heard, the first and then <see cref="ExtraSids"/> in order: a v5
    /// header's panning, laid out as the format's tables have it; all in the middle otherwise.</summary>
    public IReadOnlyList<SidPan> Panning { get; private init; } = [SidPan.Center];

    /// <summary>Whether the SIDs are heard apart - left and right - rather than all in the middle.</summary>
    public bool IsStereo => Panning.Any(pan => pan != SidPan.Center);

    /// <summary>Each song's length as the file itself gives it (v5, flag bit 10), from song 1; null
    /// for a song whose entry is not a length, and the whole list null when the file has none.</summary>
    public IReadOnlyList<TimeSpan?>? SongLengths { get; private init; }

    /// <summary>A v5 tune that also plays an FM OPL chip - an SFX Sound Expander or FM-YAM - at $DF00.</summary>
    public bool RequiresFmOpl { get; private init; }

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
        int songs = Math.Max(1, Be16(bytes, 0x0E));
        int flags = version >= 2 && bytes.Length >= 0x78 ? Be16(bytes, 0x76) : 0;
        bool v5 = version >= 5;

        // A v5 file may end with its songs' lengths, four bytes each: they are not the C64's data.
        int dataEnd = bytes.Length;
        IReadOnlyList<TimeSpan?>? lengths = null;
        if (v5 && (flags & 0x400) != 0)
        {
            dataEnd = bytes.Length - (songs * 4);
            if (dataEnd < dataOffset)
                throw new SidPlaybackException("The header says the file ends with its songs' lengths, but it is too short to hold them.");
            lengths = [.. Enumerable.Range(0, songs).Select(song => SongLength(bytes, dataEnd + (song * 4)))];
        }

        var data = bytes[dataOffset..dataEnd];

        // A load address of 0 means the real one is the first two bytes of the data, little-endian,
        // exactly as a .prg carries it.
        if (loadAddress == 0)
        {
            if (data.Length < 2)
                throw new SidPlaybackException("The header defers the load address to the data, but there is none.");

            loadAddress = data[0] | (data[1] << 8);
            data = data[2..];
        }

        bool is8580 = ((flags >> 4) & 0b11) == 0b10;
        var (name, author, released) = (v5 ? TextV5(bytes) : null) ?? (TextField(bytes, 0x16), TextField(bytes, 0x36), TextField(bytes, 0x56));
        int[] extraSids = v5 ? SidsV5(bytes) : [.. ExtraSidFields(version, bytes).Select(field => 0xD000 | (bytes[field.Address] << 4))];
        bool fmOpl = v5 && (flags & 0x800) != 0;
        if (fmOpl && extraSids.Any(address => address >= 0xDF00))
            throw new SidPlaybackException("The header puts a SID where its FM OPL chip is, at $DF00-$DFFF - the format does not allow it.");

        return new SidFile
        {
            IsRsid = magic == "RSID",
            Version = version,
            Name = name,
            Author = author,
            Released = released,
            LoadAddress = loadAddress,
            InitAddress = initAddress == 0 ? loadAddress : initAddress,
            PlayAddress = playAddress,
            Songs = songs,
            StartSong = Math.Max(1, Be16(bytes, 0x10)),
            Speed = (uint)((bytes[0x12] << 24) | (bytes[0x13] << 16) | (bytes[0x14] << 8) | bytes[0x15]),
            Data = data,
            // Bits 4-5: 01 = MOS6581, 10 = MOS8580.
            Is8580 = ((flags >> 4) & 0b11) == 0b10,
            VideoStandard = (flags >> 2) & 0b11,
            IsBasic = magic == "RSID" && (flags & 0b10) != 0,
            FreePage = version >= 2 && bytes.Length >= 0x7A ? bytes[0x78] : 0,
            FreePages = version >= 2 && bytes.Length >= 0x7A ? bytes[0x79] : 0,
            ExtraSids = extraSids,
            ExtraSids8580 = v5
                ? [.. extraSids.Select(_ => is8580)]
                : [.. ExtraSidFields(version, bytes).Select(field => ((flags >> field.ModelBits) & 0b11) switch
                {
                    0b10 => true,
                    0b01 => false,
                    _ => is8580,
                })],
            Panning = v5 ? Pan((flags >> 6) & 0b11, (flags >> 8) & 0b11, 1 + extraSids.Length) : [.. Enumerable.Repeat(SidPan.Center, 1 + extraSids.Length)],
            SongLengths = lengths,
            RequiresFmOpl = fmOpl,
        };
    }

    /// <summary>
    /// A v5 header's name, author and released: three strings, each ended by a null, in the 96 bytes
    /// from +16, nulls between them skipped. Null when they are not all there and not empty - the
    /// three 32-byte fields of the older headers are read then, as the format says.
    /// </summary>
    private static (string Name, string Author, string Released)? TextV5(byte[] bytes)
    {
        const int From = 0x16, End = 0x16 + 96;
        if (bytes.Length < End)
            return null;
        int at = From;
        string? Next(bool last)
        {
            while (at < End && bytes[at] == 0)
                at++;
            int start = at;
            while (at < End && bytes[at] != 0)
                at++;
            // Name and author end with a null; released may run to the end of the area.
            if (at == start || (at == End && !last))
                return null;
            return HeaderText.GetString(bytes, start, at - start);
        }
        return Next(false) is { } name && Next(false) is { } author && Next(true) is { } released
            ? (name.TrimEnd(), author.TrimEnd(), released.TrimEnd())
            : null;
    }

    /// <summary>The steps between a v5 header's SIDs, for each of its address configurations: one
    /// step taken again and again, or two in turn.</summary>
    private static readonly int[][] SidSteps =
    [
        [0x20], [0x40], [0x80], [0x100], [0x20, 0x60], [0x20, 0xE0], [0x100, 0x300],
    ];

    /// <summary>
    /// A v5 header's SIDs after the first at $D400: as many as +7A's low nibble says, from SID 2's
    /// place - the configuration's own, or +7B's - stepping on by the configuration's steps; past
    /// $D7E0 they go on at $DE00, past $DFE0 there are no more. A header asking for more SIDs than
    /// that gives is not a tune's.
    /// </summary>
    private static int[] SidsV5(byte[] bytes)
    {
        int config = bytes.Length > 0x7A ? bytes[0x7A] : 0;
        int count = config & 0x0F;
        if (count < 2)
            return [];
        int layout = config >> 4;
        if (layout >= SidSteps.Length)
            throw new SidPlaybackException($"The header asks for SID address configuration {layout}, which the format keeps reserved.");
        int[] steps = SidSteps[layout];
        int start = bytes.Length > 0x7B && IsSidPage(bytes[0x7B]) ? 0xD000 | (bytes[0x7B] << 4) : 0;

        // From SID 2's own start the steps begin with the first; from the configuration's, the first
        // step was the one from $D400, and the next goes on from there.
        int step = start != 0 ? 0 : 1;
        int address = start != 0 ? start : 0xD400 + steps[0];
        var sids = new List<int>();
        while (sids.Count < count - 1 && address <= 0xDFE0)
        {
            sids.Add(address);
            address += steps[step++ % steps.Length];
            if (address > 0xD7E0 && address < 0xDE00)
                address = 0xDE00 + (address - 0xD800);
        }
        if (sids.Count < count - 1)
            throw new SidPlaybackException($"The header asks for {count} SIDs, but its address configuration has room for only {sids.Count + 1}.");
        return [.. sids];
    }

    /// <summary>A v5 song length: minutes and seconds, then milliseconds, as BCD; null when the
    /// digits are not a time.</summary>
    private static TimeSpan? SongLength(byte[] bytes, int at)
    {
        static int Bcd(int value) => (value >> 4) > 9 || (value & 0x0F) > 9 ? -1 : ((value >> 4) * 10) + (value & 0x0F);
        int minutes = Bcd(bytes[at]), seconds = Bcd(bytes[at + 1]);
        int high = bytes[at + 2], rest = Bcd(bytes[at + 3]);
        if (minutes < 0 || seconds is < 0 or > 59 || (high >> 4) != 0 || (high & 0x0F) > 9 || rest < 0)
            return null;
        return new TimeSpan(0, 0, minutes, seconds, ((high & 0x0F) * 100) + rest);
    }

    /// <summary>
    /// Where each of <paramref name="chips"/> SIDs is heard, by a v5 header's panning layout (bits
    /// 6-7: standard, L/C/R, center first, fully centered) and mode (bits 8-9: direct, reverse, group,
    /// spread) - the format's own rules, which its tables spell out for 1-15 SIDs.
    /// </summary>
    public static SidPan[] Pan(int layout, int mode, int chips)
    {
        const SidPan L = SidPan.Left, C = SidPan.Center, R = SidPan.Right;
        var pans = new List<SidPan>();
        void Put(SidPan pan, int times)
        {
            for (int i = 0; i < times; i++)
                pans.Add(pan);
        }
        if (chips <= 1 || layout == 3)
        {
            Put(C, Math.Max(1, chips));
            return [.. pans];
        }
        int third = chips / 3;
        switch (mode)
        {
            case 0 or 1:
                if (layout == 0)
                    for (int i = 0; i < chips; i++)
                        pans.Add(i % 2 == 0 ? L : R);
                else if (layout == 1)
                    for (int i = 0; i < chips; i++)
                        pans.Add((i % 3) switch { 0 => L, 1 => C, _ => R });
                else
                {
                    Put(C, chips - (third * 2));
                    for (int i = 0; pans.Count < chips; i++)
                        pans.Add(i % 2 == 0 ? L : R);
                }
                if (mode == 1)
                {
                    // Reverse: left and right swapped - and center first's centre moved to the end.
                    for (int i = 0; i < pans.Count; i++)
                        pans[i] = pans[i] == L ? R : pans[i] == R ? L : C;
                    if (layout == 2)
                        pans = [.. pans.Where(pan => pan != C), .. pans.Where(pan => pan == C)];
                }
                break;
            case 2:
                if (layout == 0)
                {
                    for (int i = 0; i < chips; i++)
                        pans.Add(chips == 2 ? (i == 0 ? L : R) : (i / 2) % 2 == 0 ? L : R);
                }
                else if (layout == 1)
                {
                    foreach (int group in chips <= 9 ? [chips] : new[] { 6, chips - 6 })
                    {
                        int sides = (group + 1) / 3;
                        Put(L, sides);
                        Put(C, group - (2 * sides));
                        Put(R, sides);
                    }
                }
                else
                {
                    Put(C, chips - (2 * third));
                    if (third == 1)
                    {
                        Put(L, 1);
                        Put(R, 1);
                    }
                    else if (third > 1)
                    {
                        int left = third;
                        if (third % 2 == 1)
                        {
                            Put(L, 3);
                            Put(R, 3);
                            left -= 3;
                        }
                        for (; left > 0; left -= 2)
                        {
                            Put(L, 2);
                            Put(R, 2);
                        }
                    }
                }
                break;
            default:
                if (layout == 0)
                {
                    Put(L, (chips + 1) / 2);
                    Put(R, chips / 2);
                }
                else if (layout == 1)
                {
                    Put(L, third);
                    Put(C, chips - (2 * third));
                    Put(R, third);
                }
                else
                {
                    Put(C, chips - (2 * third));
                    Put(L, third);
                    Put(R, third);
                }
                break;
        }
        return [.. pans];
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

    /// <summary>The header's text is Windows-1252, as the format says: Hülsbeck, Östensson and the like.</summary>
    private static readonly Encoding HeaderText = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    private static string TextField(byte[] bytes, int offset)
    {
        int end = offset;
        while (end < offset + 32 && end < bytes.Length && bytes[end] != 0)
            end++;

        return HeaderText.GetString(bytes, offset, end - offset).TrimEnd();
    }
}

/// <summary>Where a SID is heard: to the left, in the middle, or to the right.</summary>
public enum SidPan : byte
{
    Center,
    Left,
    Right,
}
