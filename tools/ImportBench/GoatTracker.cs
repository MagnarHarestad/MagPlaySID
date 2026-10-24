using System.Text;

/// <summary>Just enough of a GoatTracker .sng (GTS3-5) to know when its notes play.</summary>
internal static class GoatTracker
{
    public enum EventKind { Note, Off }

    public sealed record Event(int Voice, int Frame, EventKind Kind, int Note, int Instrument, bool Legato);

    public sealed class Song
    {
        public List<byte[][]> Orders = [];
        public List<(byte Ad, byte Wave, string Name)> Instruments = [];
        public List<byte[]> Patterns = [];
        public byte[][] Left = new byte[4][], Right = new byte[4][];

        public string InstrumentName(int n) => n >= 1 && n <= Instruments.Count ? Instruments[n - 1].Name : "?";

        /// <summary>The song's notes in frames from its row 0, timed as GoatTracker's player does
        /// (gplay.c), all three channels at once: tempo 6 or instrument 63's AD, Fxx for all
        /// channels below $80 and for its own above, funktempo 9/6 or from Exy for all. A row's
        /// length is reloaded after its row is read, as gplay.c does.</summary>
        public List<Event> Events(int sub, int frameLimit)
        {
            var events = new List<Event>();
            int start = 5;
            if (Instruments.Count >= 63 && Instruments[62].Ad >= 2 && Instruments[62].Wave == 0)
                start = Instruments[62].Ad - 1;
            int[] funk = [8, 5];
            var channels = Enumerable.Range(0, 3).Select(v => new Channel { Order = Orders[sub][v], Tempo = start, Instrument = 1 }).ToArray();
            for (int frame = 0; frame < frameLimit; frame++)
            {
                for (int v = 0; v < 3; v++)
                {
                    var c = channels[v];
                    if (c.Dead)
                        continue;
                    if (c.Left > 0)
                    {
                        c.Left--;
                        continue;
                    }
                    if (!Fetch(v, c, frame))
                    {
                        c.Dead = true;
                        continue;
                    }
                    c.Left = Reload(c) - 1;
                }
            }
            return events;

            int Reload(Channel c)
            {
                if (c.Tempo >= 2)
                    return c.Tempo + 1;
                int t = funk[c.Tempo & 1] + 1;
                c.Tempo ^= 1;
                return t;
            }

            bool Fetch(int v, Channel c, int frame)
            {
                for (int guard = 0; guard < 300; guard++)
                {
                    if (c.Pattern is null)
                    {
                        if (c.Pos >= c.Order.Length)
                            return false;
                        byte o = c.Order[c.Pos];
                        if (o == 0xFF)
                        {
                            if (c.Pos + 1 >= c.Order.Length)
                                return false;
                            c.Pos = c.Order[c.Pos + 1];
                            continue;
                        }
                        if (o >= 0xD0 && o <= 0xDF)
                        {
                            c.Repeat = o - 0xD0 + 1;
                            c.Pos++;
                            continue;
                        }
                        if (o >= 0xE0)
                        {
                            c.Transpose = o - 0xF0;
                            c.Pos++;
                            continue;
                        }
                        if (o >= Patterns.Count)
                            return false;
                        c.Pattern = Patterns[o];
                        c.Row = 0;
                    }
                    if (c.Row * 4 + 3 >= c.Pattern.Length || c.Pattern[c.Row * 4] == 0xFF)
                    {
                        c.Pattern = null;
                        if (--c.Repeat <= 0)
                        {
                            c.Repeat = 1;
                            c.Pos++;
                        }
                        continue;
                    }

                    byte note = c.Pattern[c.Row * 4], ins = c.Pattern[c.Row * 4 + 1], cmd = c.Pattern[c.Row * 4 + 2], dat = c.Pattern[c.Row * 4 + 3];
                    c.Row++;
                    if (ins != 0)
                        c.Instrument = ins;
                    if (cmd == 0xF)
                    {
                        int t = dat & 0x7F;
                        if (t >= 3)
                            t--;
                        if (dat >= 0x80)
                            c.Tempo = t;
                        else
                            foreach (var other in channels)
                                other.Tempo = t;
                    }
                    else if (cmd == 0xE)
                    {
                        if (dat != 0 && dat - 1 < Left[3].Length)
                            funk = [Left[3][dat - 1] - 1, Right[3][dat - 1] - 1];
                        foreach (var other in channels)
                            other.Tempo = 0;
                    }
                    if (note >= 0x60 && note <= 0xBC)
                        events.Add(new Event(v, frame, EventKind.Note, note - 0x60 + c.Transpose, c.Instrument, cmd == 3));
                    else if (note == 0xBE)
                        events.Add(new Event(v, frame, EventKind.Off, 0, 0, false));
                    return true;
                }
                return false;
            }
        }

        private sealed class Channel
        {
            public byte[] Order = [];
            public byte[]? Pattern;
            public int Pos, Row, Repeat = 1, Transpose, Tempo, Instrument, Left;
            public bool Dead;
        }
    }

    public static Song Read(byte[] d)
    {
        string id = Encoding.ASCII.GetString(d, 0, 4);
        if (id is not ("GTS3" or "GTS4" or "GTS5"))
            throw new InvalidDataException("not GTS3-5: " + id);
        int p = 100;
        int B() => d[p++];
        byte[] Take(int n)
        {
            var r = d[p..(p + n)];
            p += n;
            return r;
        }

        var song = new Song();
        int subs = B();
        // Some files say one subtune fewer than they store (Viva Las Vegas: 1, and 2 stored): read
        // on until what follows is the instrument block - a count, then a printable name.
        bool Instruments(int at) => at + 26 <= d.Length && d[at] <= 63
            && d[at + 10] >= 32 && d[at + 10] < 127
            && d.Skip(at + 10).Take(16).All(x => x == 0 || (x >= 32 && x < 127));
        for (int s = 0; s < subs || (s < 32 && !Instruments(p)); s++)
        {
            var lists = new byte[3][];
            for (int c = 0; c < 3; c++)
                lists[c] = Take(B() + 1);
            song.Orders.Add(lists);
        }
        int instruments = B();
        for (int i = 0; i < instruments; i++)
        {
            var r = Take(9);
            var name = Encoding.Latin1.GetString(Take(16)).Split('\0')[0];
            song.Instruments.Add((r[0], r[2], name));
        }
        for (int t = 0; t < 4; t++)
        {
            int n = B();
            song.Left[t] = Take(n);
            song.Right[t] = Take(n);
        }
        int patterns = B();
        for (int i = 0; i < patterns; i++)
            song.Patterns.Add(Take(B() * 4));
        return song;
    }
}
