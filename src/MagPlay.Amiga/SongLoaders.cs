namespace MagPlay.Amiga;

public sealed partial class Song
{
    /// <summary>The note a sample's base rate is for: C-4 in XM and Scream Tracker, C-5 in Impulse
    /// Tracker - counted from 1 for C-0.</summary>
    public int MiddleNote => Type == SongFormat.It ? 61 : 49;

    // ------------------------------------------------------------------ Scream Tracker 3

    private static Song ParseS3m(byte[] b)
    {
        int orders = Word(b, 0x20), instruments = Word(b, 0x22), patterns = Word(b, 0x24);
        int version = Word(b, 0x28), signedness = Word(b, 0x2A);
        int master = b[0x33];
        bool stereo = (master & 0x80) != 0;
        var order = new List<int>();
        for (int i = 0; i < orders && 0x60 + i < b.Length; i++)
            order.Add(b[0x60 + i]);
        int pointers = 0x60 + orders;
        int panTable = pointers + (instruments * 2) + (patterns * 2);

        // The PCM channels: 0-7 left, 8-15 right; Adlib and disabled ones play nothing.
        int channels = 0;
        var pans = new int[32];
        var used = new bool[32];
        for (int c = 0; c < 32; c++)
        {
            int setting = b[0x40 + c];
            used[c] = setting < 16;
            if (used[c])
                channels = c + 1;
            pans[c] = setting < 8 ? 3 * 256 / 15 : 12 * 256 / 15;
            if (b[0x35] == 252 && panTable + c < b.Length && (b[panTable + c] & 0x20) != 0)
                pans[c] = (b[panTable + c] & 0x0F) * 256 / 15;
            if (!stereo)
                pans[c] = 128;
        }
        channels = Math.Max(1, channels);

        var samples = new List<SongSample>();
        for (int i = 0; i < instruments; i++)
        {
            int at = Word(b, pointers + (i * 2)) * 16;
            var sample = new SongSample();
            samples.Add(sample);
            if (at + 0x50 > b.Length)
                continue;
            sample.FileName = Text(b, at + 1, 12);
            sample.Name = Text(b, at + 0x30, 28);
            if (b[at] != 1)
                continue;
            int memory = (b[at + 0x0D] << 16 | Word(b, at + 0x0E)) * 16;
            int length = Long(b, at + 0x10), loopStart = Long(b, at + 0x14), loopEnd = Long(b, at + 0x18);
            int flags = b[at + 0x1F];
            sample.Volume = Math.Min(64, (int)b[at + 0x1C]);
            sample.BaseRate = Long(b, at + 0x20) is > 0 and var rate ? rate : 8363;
            bool sixteen = (flags & 4) != 0;
            sample.Data = ReadPcm(b, memory, length, sixteen, signedness != 2, delta: false);
            sample.Bytes = length * (sixteen ? 2 : 1);
            if ((flags & 1) != 0 && loopEnd > loopStart && loopStart < sample.Data.Length)
                (sample.LoopStart, sample.LoopEnd, sample.LoopMode) = (loopStart, Math.Min(loopEnd, sample.Data.Length), 1);
        }

        var list = new List<SongPattern>();
        for (int p = 0; p < patterns; p++)
        {
            var pattern = new SongPattern(64, channels);
            for (int i = 0; i < pattern.Cells.Length; i++)
                pattern.Cells[i] = new SongCell(0, 0, SongCell.NoVolume, 0, 0);
            list.Add(pattern);
            int at = Word(b, pointers + (instruments * 2) + (p * 2)) * 16;
            if (at == 0 || at + 2 > b.Length)
                continue;
            at += 2;
            for (int row = 0; row < 64 && at < b.Length;)
            {
                int what = b[at++];
                if (what == 0)
                {
                    row++;
                    continue;
                }
                int channel = what & 31;
                byte note = 0, instrument = 0, volume = SongCell.NoVolume, effect = 0, parameter = 0;
                if ((what & 32) != 0 && at + 1 < b.Length)
                {
                    int n = b[at++];
                    note = n switch { 255 => 0, 254 => SongCell.NoteCut, _ => (byte)(((n >> 4) * 12) + (n & 15) + 1) };
                    instrument = b[at++];
                }
                if ((what & 64) != 0 && at < b.Length)
                    volume = (byte)Math.Min(64, (int)b[at++]);
                if ((what & 128) != 0 && at + 1 < b.Length)
                {
                    effect = b[at++];
                    parameter = b[at++];
                }
                if (channel < channels && used[channel])
                    pattern.Cells[(row * channels) + channel] = new SongCell(note, instrument, volume, effect, parameter);
            }
        }

        return new Song
        {
            Type = SongFormat.S3m,
            Title = Text(b, 0, 28),
            Tracker = (version >> 12) switch { 1 => "Scream Tracker 3", 2 => "Imago Orpheus", 3 => "Impulse Tracker", 4 => "Schism Tracker", 5 => "OpenMPT", _ => "a Scream Tracker 3 compatible" },
            Channels = channels,
            Order = order,
            Patterns = list,
            SampleList = samples,
            InitialSpeed = b[0x31] is > 0 and var speed ? speed : 6,
            InitialTempo = b[0x32] >= 33 ? b[0x32] : 125,
            GlobalVolume = Math.Min(64, (int)b[0x30]) * 2,
            MixVolume = Math.Max(16, master & 0x7F),
            ChannelPan = pans[..channels],
            ChannelVolume = Enumerable.Repeat(64, channels).ToArray(),
            Stereo = stereo,
            Level = 0.54,
        };
    }

    // ------------------------------------------------------------------ FastTracker 2

    private static Song ParseXm(byte[] b)
    {
        int headerSize = Long(b, 60);
        int length = Word(b, 64), restart = Word(b, 66), channels = Math.Clamp(Word(b, 68), 1, 64);
        int patterns = Word(b, 70), instruments = Word(b, 72), flags = Word(b, 74);
        var order = new List<int>();
        for (int i = 0; i < Math.Min(length, 256); i++)
            order.Add(b[80 + i]);
        int at = 60 + headerSize;

        var list = new List<SongPattern>();
        for (int p = 0; p < patterns && at + 9 <= b.Length; p++)
        {
            int header = Long(b, at), rows = Math.Clamp(Word(b, at + 5), 1, 256), packed = Word(b, at + 7);
            var pattern = new SongPattern(rows, channels);
            list.Add(pattern);
            int data = at + header, end = Math.Min(b.Length, data + packed);
            at = data + packed;
            for (int i = 0; i < pattern.Cells.Length && data < end; i++)
            {
                int first = b[data++];
                byte note = 0, instrument = 0, volume = 0, effect = 0, parameter = 0;
                if ((first & 0x80) != 0)
                {
                    if ((first & 1) != 0 && data < end) note = b[data++];
                    if ((first & 2) != 0 && data < end) instrument = b[data++];
                    if ((first & 4) != 0 && data < end) volume = b[data++];
                    if ((first & 8) != 0 && data < end) effect = b[data++];
                    if ((first & 16) != 0 && data < end) parameter = b[data++];
                }
                else
                {
                    note = (byte)first;
                    if (data + 3 < end + 1)
                    {
                        instrument = b[data++];
                        volume = data < end ? b[data++] : (byte)0;
                        effect = data < end ? b[data++] : (byte)0;
                        parameter = data < end ? b[data++] : (byte)0;
                    }
                }
                if (note == 97)
                    note = SongCell.NoteOff;
                else if (note > 96)
                    note = 0;
                pattern.Cells[i] = new SongCell(note, instrument, volume, effect, parameter);
            }
        }
        // Patterns the order names past those in the file are empty: 64 rows.
        while (list.Count < 256 && order.Count > 0 && order.Max() >= list.Count)
            list.Add(new SongPattern(64, channels));

        var samples = new List<SongSample>();
        var instrumentList = new List<SongInstrument>();
        for (int i = 0; i < instruments && at + 29 <= b.Length; i++)
        {
            int size = Long(b, at);
            var instrument = new SongInstrument { Name = Text(b, at + 4, 22) };
            instrumentList.Add(instrument);
            int count = Word(b, at + 27);
            if (count == 0 || size < 33)
            {
                at += Math.Max(size, 29);
                continue;
            }
            int headerLength = Long(b, at + 29);
            var keys = new byte[96];
            Array.Copy(b, at + 33, keys, 0, Math.Min(96, b.Length - at - 33));
            instrument.VolumeEnvelope = XmEnvelope(b, at + 129, b[at + 225], b[at + 227], b[at + 228], b[at + 229], b[at + 233]);
            instrument.PanEnvelope = XmEnvelope(b, at + 177, b[at + 226], b[at + 230], b[at + 231], b[at + 232], b[at + 234]);
            int vibratoType = b[at + 235], sweep = b[at + 236], depth = b[at + 237], rate = b[at + 238];
            instrument.Fadeout = Word(b, at + 239) * 2;
            int headers = at + size;
            int first = samples.Count;
            var lengths = new List<(int Length, bool Sixteen)>();
            for (int s = 0; s < count; s++)
            {
                int h = headers + (s * headerLength);
                int sampleLength = Long(b, h), loopStart = Long(b, h + 4), loopLength = Long(b, h + 8);
                int type = h + 14 < b.Length ? b[h + 14] : 0;
                bool sixteen = (type & 0x10) != 0;
                int unit = sixteen ? 2 : 1;
                sbyte fine = h + 13 < b.Length ? (sbyte)b[h + 13] : (sbyte)0, relative = h + 16 < b.Length ? (sbyte)b[h + 16] : (sbyte)0;
                var sample = new SongSample
                {
                    Name = Text(b, h + 18, 22),
                    Volume = Math.Min(64, h + 12 < b.Length ? (int)b[h + 12] : 64),
                    Pan = h + 15 < b.Length ? b[h + 15] : 128,
                    BaseRate = 8363 * Math.Pow(2, (relative + (fine / 128.0)) / 12),
                    LoopStart = loopStart / unit,
                    LoopEnd = (loopStart + loopLength) / unit,
                    LoopMode = loopLength > 0 ? (type & 3) switch { 1 => 1, 2 or 3 => 2, _ => 0 } : 0,
                    VibratoType = vibratoType,
                    VibratoSweep = sweep,
                    VibratoDepth = depth,
                    VibratoRate = rate,
                    Bytes = sampleLength,
                };
                samples.Add(sample);
                lengths.Add((sampleLength, sixteen));
            }
            at = headers + (count * headerLength);
            for (int s = 0; s < count; s++)
            {
                var (sampleLength, sixteen) = lengths[s];
                var sample = samples[first + s];
                sample.Data = ReadPcm(b, at, sixteen ? sampleLength / 2 : sampleLength, sixteen, signed: true, delta: true);
                at += sampleLength;
                sample.LoopEnd = Math.Min(sample.LoopEnd, sample.Data.Length);
                if (sample.LoopStart >= sample.LoopEnd)
                    sample.LoopMode = 0;
            }
            for (int note = 0; note < 120; note++)
            {
                int key = keys[Math.Min(95, Math.Max(0, note))];
                instrument.SampleOf[note] = key < count ? first + key + 1 : 0;
            }
        }

        return new Song
        {
            Type = SongFormat.Xm,
            Title = Text(b, 17, 20),
            Tracker = Text(b, 38, 20),
            Channels = channels,
            Order = order,
            Restart = restart < order.Count ? restart : 0,
            Patterns = list,
            SampleList = samples,
            Instruments = instrumentList,
            InitialSpeed = Word(b, 76) is > 0 and var speed ? speed : 6,
            InitialTempo = Word(b, 78) is >= 32 and var tempo ? tempo : 125,
            LinearSlides = (flags & 1) != 0,
            Level = 0.29,
            ChannelPan = Enumerable.Repeat(128, channels).ToArray(),
            ChannelVolume = Enumerable.Repeat(64, channels).ToArray(),
        };
    }

    private static SongEnvelope XmEnvelope(byte[] b, int at, int count, int sustain, int loopStart, int loopEnd, int type)
    {
        count = Math.Clamp(count, 0, 12);
        var points = new (int, int)[count];
        for (int i = 0; i < count; i++)
            points[i] = (Word(b, at + (i * 4)), Math.Min(64, Word(b, at + (i * 4) + 2)));
        return new SongEnvelope
        {
            Points = points,
            Enabled = (type & 1) != 0 && count > 0,
            Sustain = (type & 2) != 0,
            SustainStart = Math.Min(sustain, Math.Max(0, count - 1)),
            SustainEnd = Math.Min(sustain, Math.Max(0, count - 1)),
            Loop = (type & 4) != 0,
            LoopStart = Math.Min(loopStart, Math.Max(0, count - 1)),
            LoopEnd = Math.Min(loopEnd, Math.Max(0, count - 1)),
        };
    }

    // ------------------------------------------------------------------ Impulse Tracker

    private static Song ParseIt(byte[] b)
    {
        int orders = Word(b, 0x20), instruments = Word(b, 0x22), sampleCount = Word(b, 0x24), patterns = Word(b, 0x26);
        int version = Word(b, 0x28), compatible = Word(b, 0x2A), flags = Word(b, 0x2C), special = Word(b, 0x2E);
        bool stereo = (flags & 1) != 0, useInstruments = (flags & 4) != 0;
        var order = new List<int>();
        for (int i = 0; i < orders && 0xC0 + i < b.Length; i++)
            order.Add(b[0xC0 + i]);
        int pointers = 0xC0 + orders;
        int Pointer(int index) => Long(b, pointers + (index * 4));

        var samples = new List<SongSample>();
        for (int s = 0; s < sampleCount; s++)
        {
            int at = Pointer(instruments + s);
            var sample = new SongSample();
            samples.Add(sample);
            if (at <= 0 || at + 0x50 > b.Length)
                continue;
            int flag = b[at + 0x12], convert = b[at + 0x2E], pan = b[at + 0x2F];
            sample.FileName = Text(b, at + 4, 12);
            sample.Name = Text(b, at + 0x14, 26);
            sample.GlobalVolume = Math.Min(64, (int)b[at + 0x11]);
            sample.Volume = Math.Min(64, (int)b[at + 0x13]);
            sample.Pan = (pan & 0x80) != 0 ? Math.Min(64, pan & 0x7F) * 4 : -1;
            int length = Long(b, at + 0x30);
            sample.BaseRate = Long(b, at + 0x3C) is > 0 and var rate ? rate : 8363;
            (sample.VibratoRate, sample.VibratoDepth, sample.VibratoSweep, sample.VibratoType) = (b[at + 0x4C], b[at + 0x4D], b[at + 0x4E], b[at + 0x4F]);
            if ((flag & 1) == 0 || length <= 0)
                continue;
            bool sixteen = (flag & 2) != 0, compressed = (flag & 8) != 0;
            int data = Long(b, at + 0x48);
            sample.Data = compressed ? ItUnpack(b, data, length, sixteen, (convert & 4) != 0)
                : ReadPcm(b, data, length, sixteen, (convert & 1) != 0, delta: (convert & 4) != 0);
            sample.Bytes = length * (sixteen ? 2 : 1);
            int loopStart = Long(b, at + 0x34), loopEnd = Long(b, at + 0x38), susStart = Long(b, at + 0x40), susEnd = Long(b, at + 0x44);
            if ((flag & 16) != 0 && loopEnd > loopStart && loopStart < sample.Data.Length)
                (sample.LoopStart, sample.LoopEnd, sample.LoopMode) = (loopStart, Math.Min(loopEnd, sample.Data.Length), (flag & 64) != 0 ? 2 : 1);
            if ((flag & 32) != 0 && susEnd > susStart && susStart < sample.Data.Length)
                (sample.SustainStart, sample.SustainEnd, sample.SustainMode) = (susStart, Math.Min(susEnd, sample.Data.Length), (flag & 128) != 0 ? 2 : 1);
        }

        var instrumentList = new List<SongInstrument>();
        if (useInstruments)
        {
            for (int i = 0; i < instruments; i++)
            {
                int at = Pointer(i);
                var instrument = new SongInstrument();
                instrumentList.Add(instrument);
                if (at <= 0 || at + 0x130 > b.Length)
                    continue;
                instrument.Name = Text(b, at + 0x20, 26);
                for (int n = 0; n < 120; n++)
                {
                    instrument.NoteOf[n] = Math.Min(119, (int)b[at + 0x40 + (n * 2)]);
                    instrument.SampleOf[n] = b[at + 0x41 + (n * 2)];
                }
                if (compatible >= 0x200)
                {
                    instrument.NewNoteAction = b[at + 0x11] & 3;
                    instrument.DuplicateCheck = b[at + 0x12] & 3;
                    instrument.DuplicateAction = b[at + 0x13] % 3;
                    instrument.Fadeout = Math.Min(1024, Word(b, at + 0x14)) * 64;
                    instrument.GlobalVolume = Math.Min(128, (int)b[at + 0x18]);
                    instrument.Pan = (b[at + 0x19] & 0x80) == 0 ? Math.Min(64, (int)b[at + 0x19]) * 4 : -1;
                    if ((b[at + 0x3A] & 0x80) != 0)
                        instrument.FilterCutoff = b[at + 0x3A] & 0x7F;
                    if ((b[at + 0x3B] & 0x80) != 0)
                        instrument.FilterResonance = b[at + 0x3B] & 0x7F;
                    if (at + 0x130 + (82 * 3) <= b.Length)
                    {
                        instrument.VolumeEnvelope = ItEnvelope(b, at + 0x130, signed: false);
                        instrument.PanEnvelope = ItEnvelope(b, at + 0x182, signed: true);
                        instrument.PitchEnvelope = ItEnvelope(b, at + 0x1D4, signed: true);
                    }
                }
                else
                {
                    // Impulse Tracker 1's instruments: a volume envelope of byte nodes.
                    int envelopeFlags = b[at + 0x11];
                    instrument.Fadeout = Math.Min(1024, Word(b, at + 0x14) * 2) * 64;
                    instrument.NewNoteAction = b[at + 0x16] & 3;
                    var points = new List<(int, int)>();
                    for (int n = 0; n < 25 && at + 0x1F2 + (n * 2) + 1 < b.Length; n++)
                    {
                        int tick = b[at + 0x1F2 + (n * 2)];
                        if (tick == 0xFF)
                            break;
                        points.Add((tick, Math.Min(64, (int)b[at + 0x1F3 + (n * 2)])));
                    }
                    instrument.VolumeEnvelope = new SongEnvelope
                    {
                        Points = [.. points],
                        Enabled = (envelopeFlags & 1) != 0 && points.Count > 0,
                        Loop = (envelopeFlags & 2) != 0,
                        Sustain = (envelopeFlags & 4) != 0,
                        LoopStart = b[at + 0x12], LoopEnd = b[at + 0x13],
                        SustainStart = b[at + 0x14], SustainEnd = b[at + 0x15],
                    };
                }
            }
        }

        // The channels its patterns use; the patterns read with the previous values each channel kept.
        var list = new List<SongPattern>();
        var raw = new List<(int Rows, List<(int Row, int Channel, SongCell Cell)> Cells)>();
        int channels = 1;
        for (int p = 0; p < patterns; p++)
        {
            int at = Pointer(instruments + sampleCount + p);
            var cells = new List<(int, int, SongCell)>();
            if (at <= 0 || at + 8 > b.Length)
            {
                raw.Add((64, cells));
                continue;
            }
            int length = Word(b, at), rows = Math.Clamp(Word(b, at + 2), 1, 256);
            int data = at + 8, end = Math.Min(b.Length, data + length);
            var mask = new int[64];
            var last = new SongCell[64];
            for (int c = 0; c < 64; c++)
                last[c] = new SongCell(0, 0, SongCell.NoVolume, 0, 0);
            for (int row = 0; row < rows && data < end;)
            {
                int what = b[data++];
                if (what == 0)
                {
                    row++;
                    continue;
                }
                int channel = (what - 1) & 63;
                if ((what & 0x80) != 0 && data < end)
                    mask[channel] = b[data++];
                int m = mask[channel];
                var previous = last[channel];
                byte note = 0, instrument = 0, volume = SongCell.NoVolume, effect = 0, parameter = 0;
                if ((m & 1) != 0 && data < end)
                {
                    int n = b[data++];
                    note = n switch { < 120 => (byte)(n + 1), 255 => SongCell.NoteOff, 254 => SongCell.NoteCut, _ => SongCell.NoteFade };
                }
                if ((m & 2) != 0 && data < end) instrument = b[data++];
                if ((m & 4) != 0 && data < end) volume = b[data++];
                if ((m & 8) != 0 && data + 1 < end)
                {
                    effect = b[data++];
                    parameter = b[data++];
                }
                if ((m & 16) != 0) note = previous.Note;
                if ((m & 32) != 0) instrument = previous.Instrument;
                if ((m & 64) != 0) volume = previous.Volume;
                if ((m & 128) != 0) (effect, parameter) = (previous.Effect, previous.Parameter);
                last[channel] = new SongCell((m & 1) != 0 ? note : previous.Note, (m & 2) != 0 ? instrument : previous.Instrument,
                                             (m & 4) != 0 ? volume : previous.Volume, (m & 8) != 0 ? effect : previous.Effect,
                                             (m & 8) != 0 ? parameter : previous.Parameter);
                cells.Add((row, channel, new SongCell(note, instrument, volume, effect, parameter)));
                channels = Math.Max(channels, channel + 1);
            }
            raw.Add((rows, cells));
        }
        foreach (var (rows, cells) in raw)
        {
            var pattern = new SongPattern(rows, channels);
            for (int i = 0; i < pattern.Cells.Length; i++)
                pattern.Cells[i] = new SongCell(0, 0, SongCell.NoVolume, 0, 0);
            foreach (var (row, channel, cell) in cells)
                pattern.Cells[(row * channels) + channel] = cell;
            list.Add(pattern);
        }

        var pans = new int[channels];
        var volumes = new int[channels];
        for (int c = 0; c < channels; c++)
        {
            int pan = b[0x40 + c] & 0x7F;
            pans[c] = !stereo || pan > 64 ? 128 : pan * 4;
            volumes[c] = (b[0x40 + c] & 0x80) != 0 ? 0 : Math.Min(64, (int)b[0x80 + c]);
        }

        string message = "";
        if ((special & 1) != 0)
        {
            int length = Word(b, 0x36), offset;
            offset = Long(b, 0x38);
            if (offset > 0 && offset < b.Length)
            {
                var text = new System.Text.StringBuilder();
                for (int i = offset; i < Math.Min(b.Length, offset + length) && b[i] != 0; i++)
                    text.Append(b[i] == 13 ? '\n' : b[i] is >= 32 and < 127 ? (char)b[i] : ' ');
                message = text.ToString().TrimEnd();
            }
        }

        return new Song
        {
            Type = SongFormat.It,
            Title = Text(b, 4, 26),
            Tracker = (version >> 12) switch
            {
                0 or 1 or 2 => $"Impulse Tracker {(version >> 8) & 0xF}.{version & 0xFF:X2}",
                5 => "OpenMPT",
                6 => "BeRoTracker",
                _ => "an Impulse Tracker compatible",
            },
            Channels = channels,
            Order = order,
            Patterns = list,
            SampleList = samples,
            Instruments = instrumentList,
            InitialSpeed = b[0x32] is > 0 and var speed ? speed : 6,
            InitialTempo = b[0x33] >= 31 ? b[0x33] : 125,
            GlobalVolume = Math.Min(128, (int)b[0x30]),
            MixVolume = Math.Min(128, (int)b[0x31]),
            LinearSlides = (flags & 8) != 0,
            OldEffects = (flags & 16) != 0,
            CompatibleGxx = (flags & 32) != 0,
            ChannelPan = pans,
            ChannelVolume = volumes,
            Stereo = stereo,
            Message = message,
            Level = 0.54 * (version is >= 0x214 and < 0x1000 && compatible < 0x214 ? 1 / 3.0 : 1),
        };
    }

    private static SongEnvelope ItEnvelope(byte[] b, int at, bool signed)
    {
        int flags = b[at], count = Math.Clamp((int)b[at + 1], 0, 25);
        var points = new (int, int)[count];
        for (int i = 0; i < count; i++)
        {
            int value = signed ? (sbyte)b[at + 6 + (i * 3)] : Math.Min(64, (int)b[at + 6 + (i * 3)]);
            points[i] = (Word(b, at + 7 + (i * 3)), value);
        }
        int Clamp(int point) => Math.Clamp(point, 0, Math.Max(0, count - 1));
        return new SongEnvelope
        {
            Points = points,
            Enabled = (flags & 1) != 0 && count > 0,
            Loop = (flags & 2) != 0,
            Sustain = (flags & 4) != 0,
            Carry = (flags & 8) != 0,
            Filter = (flags & 0x80) != 0,
            LoopStart = Clamp(b[at + 2]), LoopEnd = Clamp(b[at + 3]),
            SustainStart = Clamp(b[at + 4]), SustainEnd = Clamp(b[at + 5]),
        };
    }

    // ------------------------------------------------------------------ sound data

    /// <summary>Samples as 16-bit: 8-bit ones scaled up, unsigned ones centred, delta-coded ones summed.</summary>
    private static short[] ReadPcm(byte[] b, int at, int length, bool sixteen, bool signed, bool delta)
    {
        if (at < 0 || at >= b.Length || length <= 0)
            return [];
        int available = (b.Length - at) / (sixteen ? 2 : 1);
        length = Math.Min(length, available);
        var data = new short[length];
        int running = 0;
        for (int i = 0; i < length; i++)
        {
            int value = sixteen ? (short)(b[at + (i * 2)] | (b[at + (i * 2) + 1] << 8)) : (sbyte)b[at + i];
            if (!signed)
                value = sixteen ? (ushort)value - 32768 : (byte)value - 128;
            if (delta)
            {
                running = sixteen ? (short)(running + value) : (sbyte)(running + value);
                value = running;
            }
            data[i] = (short)(sixteen ? value : value << 8);
        }
        return data;
    }

    /// <summary>Impulse Tracker 2.14's compressed samples - and 2.15's, delta-coded twice: blocks of
    /// 32768 bytes' worth, each a bit stream whose code width changes as it goes.</summary>
    private static short[] ItUnpack(byte[] b, int at, int length, bool sixteen, bool twice)
    {
        // A broken header's length is no bigger than the bytes after it could unpack to.
        if (at < 0 || at >= b.Length || length <= 0)
            return [];
        length = (int)Math.Min(length, Math.Min(1L << 26, (b.Length - (long)at) * 16));
        var output = new short[length];
        int done = 0;
        while (done < length && at + 2 <= b.Length)
        {
            int blockBytes = Word(b, at);
            int start = at + 2, end = Math.Min(b.Length, start + blockBytes);
            at = start + blockBytes;
            int count = Math.Min(length - done, sixteen ? 0x4000 : 0x8000);
            var bits = new BitReader(b, start, end);
            int width = sixteen ? 17 : 9;
            int last = 0, last2 = 0;
            int top = sixteen ? 16 : 8;
            for (int i = 0; i < count && !bits.Exhausted;)
            {
                int value = bits.Read(width);
                if (width < 7)
                {
                    if (value == 1 << (width - 1))
                    {
                        int next = bits.Read(sixteen ? 4 : 3) + 1;
                        width = next < width ? next : next + 1;
                        continue;
                    }
                }
                else if (width < top + 1)
                {
                    int border = ((sixteen ? 0xFFFF : 0xFF) >> (top + 1 - width)) - (top / 2);
                    if (value > border && value <= border + top)
                    {
                        int next = value - border;
                        width = next < width ? next : next + 1;
                        continue;
                    }
                }
                else if (width >= top + 2)
                {
                    i++;
                    continue;
                }
                else if (value >= 1 << top)
                {
                    width = (value + 1) & 0xFF;
                    continue;
                }
                // A value: sign-extended from its width, added to the last.
                if (width < top)
                {
                    int shift = 32 - width;
                    value = (value << shift) >> shift;
                }
                last = sixteen ? (short)(last + value) : (sbyte)(last + value);
                last2 = sixteen ? (short)(last2 + last) : (sbyte)(last2 + last);
                int sample = twice ? last2 : last;
                output[done + i] = (short)(sixteen ? sample : sample << 8);
                i++;
            }
            done += count;
        }
        return output;
    }

    private sealed class BitReader(byte[] bytes, int at, int end)
    {
        private int _at = at, _bit;

        public bool Exhausted => _at >= end;

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int bit = _at < end ? (bytes[_at] >> _bit) & 1 : 0;
                value |= bit << i;
                if (++_bit == 8)
                {
                    _bit = 0;
                    _at++;
                }
            }
            return value;
        }
    }
}
