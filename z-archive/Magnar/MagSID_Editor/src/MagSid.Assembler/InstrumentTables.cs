using MagSid.Core.Model;

namespace MagSid.Assembler;

/// <summary>
/// The per-instrument tables and the three shared program pools, exactly as Startup\Tables.s builds
/// them.
///
/// Each pool starts with a lone $00 at offset 0, which is the sentinel the player tests against:
/// voice_wt_pos / voice_arp_pos / filter_pos of zero means "nothing running", so no instrument may
/// ever point there. Identical programs share one entry, which is where most of the saving is - a
/// song's eight arpeggio instruments are usually four distinct programs.
/// </summary>
public sealed class InstrumentTables
{
    /// <summary>One byte per instrument, indexed by instrument number - 1. The pattern stream picks
    /// an instrument with $C0..$CF, so these are 0-based with no dummy entry.</summary>
    public required byte[] AttackDecay { get; init; }

    public required byte[] SustainRelease { get; init; }
    public required byte[] WavetableStart { get; init; }
    public required byte[] ArpeggioStart { get; init; }
    public required byte[] FilterStart { get; init; }
    public required byte[] PulseInit { get; init; }
    public required byte[] PulseAdd { get; init; }

    public required byte[] WavetablePool { get; init; }
    public required byte[] ArpeggioPool { get; init; }
    public required byte[] FilterPool { get; init; }

    /// <summary>Emitted only for a song that uses the effect, matching the player's own gates.</summary>
    public required byte[] VibratoDelaySpeed { get; init; }

    public required byte[] VibratoDepth { get; init; }
    public required byte[] SlideRate { get; init; }

    public static InstrumentTables Build(Song song, NoteDictionary notes)
    {
        var instruments = song.Instruments.OrderBy(i => i.Number).ToList();
        var wavetable = BuildWavetablePool(instruments);
        var arpeggio = BuildArpeggioPool(instruments, notes);
        var filter = BuildFilterPool(instruments);

        return new InstrumentTables
        {
            AttackDecay = instruments.Select(i => i.AttackDecay).ToArray(),
            SustainRelease = instruments.Select(i => i.SustainRelease).ToArray(),
            WavetableStart = wavetable.Starts,
            WavetablePool = wavetable.Pool,
            ArpeggioStart = arpeggio.Starts,
            ArpeggioPool = arpeggio.Pool,
            FilterStart = filter.Starts,
            FilterPool = filter.Pool,
            PulseInit = instruments.Select(PulseInitByte).ToArray(),
            PulseAdd = instruments.Select(i => (byte)(i.PulseSweep & 0xFF)).ToArray(),
            VibratoDelaySpeed = instruments.Select(i => i.HasVibrato ? i.VibratoDelaySpeed : (byte)0).ToArray(),
            VibratoDepth = instruments.Select(i => i.VibratoDepth).ToArray(),
            SlideRate = instruments.Select(i => (byte)i.SlideRate).ToArray(),
        };
    }

    /// <summary>Bit 6 is continuous (a new note leaves the channel's sweep running) and bit 5 is
    /// NOHR (skip the hard-restart AD/SR reset). Neither has anything to do with pulse width; this
    /// is simply where the free bits were.</summary>
    private static byte PulseInitByte(Instrument instrument)
    {
        int width = instrument.PulseWidth;
        if (instrument.PulseContinuous)
            width += 0x40;
        if (instrument.NoHardRestart)
            width += 0x20;

        return (byte)width;
    }

    /// <summary>One SID control-register byte per frame, terminated by $00 to hold the last
    /// waveform. An instrument with no WAVE section gets the one-step program {waveform | gate} -
    /// the gate bit is what starts the note.</summary>
    private static (byte[] Starts, byte[] Pool) BuildWavetablePool(List<Instrument> instruments)
    {
        var pool = new List<byte> { 0x00 };
        var offsets = new Dictionary<string, int>();
        var starts = new byte[instruments.Count];

        for (int i = 0; i < instruments.Count; i++)
        {
            var instrument = instruments[i];
            var program = instrument.Wavetable.Count > 0
                ? instrument.Wavetable.ToList()
                : [(byte)(instrument.Waveform | 0x01)];
            program.Add(0x00);

            starts[i] = (byte)Share(pool, offsets, Key(program), program);
        }

        Require(pool.Count, "wavetable pool", "voice_wt_pos");
        return (starts, pool.ToArray());
    }

    /// <summary>How a program ends is the whole difference between an arpeggio and a drum: Loop ends
    /// $00 plus the offset to resume at (its own start, or its loop point), Hold ends $00 plus the
    /// last step's offset, and Root ends with a bare $80 and no terminator at all - the player reads
    /// $80 as "the row's own note" without advancing past it.</summary>
    private static (byte[] Starts, byte[] Pool) BuildArpeggioPool(List<Instrument> instruments, NoteDictionary notes)
    {
        var pool = new List<byte> { 0x00 };
        var offsets = new Dictionary<string, int>();
        var starts = new byte[instruments.Count];

        for (int i = 0; i < instruments.Count; i++)
        {
            var instrument = instruments[i];
            if (!instrument.HasArpeggio)
            {
                starts[i] = 0x00;
                continue;
            }

            string key = string.Join(" ", instrument.Arpeggio.Select(n => n.ToString()))
                         + "|" + instrument.ArpeggioEnding + "|" + instrument.ArpeggioLoopPoint;

            if (offsets.TryGetValue(key, out int shared))
            {
                starts[i] = (byte)shared;
                continue;
            }

            int start = pool.Count;
            foreach (var step in instrument.Arpeggio)
                pool.Add((byte)notes.IdOf(step));

            if (instrument.ArpeggioEnding == ArpeggioEnding.Root)
            {
                pool.Add(0x80);
            }
            else
            {
                int resume = instrument.ArpeggioEnding == ArpeggioEnding.Hold
                    ? pool.Count - 1                      // stay on the last step
                    : start + instrument.ArpeggioLoopPoint;

                if (resume <= 0)
                {
                    throw new SongTooLargeException(
                        $"Instrument {instrument.Number:00}: a Hold ending needs a note before it.");
                }

                pool.Add(0x00);
                pool.Add((byte)resume);
            }

            offsets[key] = start;
            starts[i] = (byte)start;
        }

        Require(pool.Count, "arpeggio pool", "voice_arp_pos");
        return (starts, pool.ToArray());
    }

    /// <summary>A plain cutoff step is one byte; $FF introduces a four-byte setup record
    /// (mode+volume, resonance+routing, sweep rate, cutoff); $00 ends a program and holds the last
    /// cutoff. $00 and $FF being taken is why a cutoff has to stay inside $01..$FE.</summary>
    private static (byte[] Starts, byte[] Pool) BuildFilterPool(List<Instrument> instruments)
    {
        var pool = new List<byte> { 0x00 };
        var offsets = new Dictionary<string, int>();
        var starts = new byte[instruments.Count];

        for (int i = 0; i < instruments.Count; i++)
        {
            var instrument = instruments[i];
            if (instrument.Filter.Count == 0)
            {
                starts[i] = 0x00;
                continue;
            }

            var bytes = new List<byte>();
            foreach (var step in instrument.Filter)
            {
                RequireCutoff(instrument, step.Cutoff);
                if (step.IsSetup)
                {
                    bytes.Add(0xFF);
                    bytes.Add(step.ModeVolume!.Value);
                    bytes.Add(step.ResonanceRouting!.Value);
                    bytes.Add((byte)(step.SweepRate & 0xFF));
                }

                bytes.Add(step.Cutoff);
            }

            bytes.Add(0x00);
            starts[i] = (byte)Share(pool, offsets, Key(bytes), bytes);
        }

        Require(pool.Count, "filter pool", "filter_pos");
        return (starts, pool.ToArray());
    }

    private static void RequireCutoff(Instrument instrument, byte cutoff)
    {
        if (cutoff is >= FilterStep.MinCutoff and <= FilterStep.MaxCutoff)
            return;

        throw new SongTooLargeException(
            $"Instrument {instrument.Number:00}: a filter cutoff must be " +
            $"${FilterStep.MinCutoff:X2}..${FilterStep.MaxCutoff:X2}, got ${cutoff:X2} - " +
            "$00 ends a program and $FF introduces a setup record.");
    }

    private static string Key(IEnumerable<byte> program) => string.Join(",", program);

    private static int Share(List<byte> pool, Dictionary<string, int> offsets, string key, List<byte> program)
    {
        if (offsets.TryGetValue(key, out int existing))
            return existing;

        int offset = pool.Count;
        pool.AddRange(program);
        offsets[key] = offset;
        return offset;
    }

    private static void Require(int poolBytes, string what, string positionVariable)
    {
        if (poolBytes > 256)
        {
            throw new SongTooLargeException(
                $"The {what} is {poolBytes} bytes - {positionVariable} is a single byte, so 256 is the ceiling.");
        }
    }
}
