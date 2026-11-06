namespace MagPlay.C64;

/// <summary>A SID envelope's times, in milliseconds, by their four bits: attack, and decay and
/// release, which are three times as long.</summary>
public static class EnvelopeTimes
{
    public static readonly int[] AttackMs = [2, 8, 16, 24, 38, 56, 68, 80, 100, 250, 500, 800, 1000, 3000, 5000, 8000];
    public static readonly int[] DecayMs = [6, 24, 48, 72, 114, 168, 204, 240, 300, 750, 1500, 2400, 3000, 9000, 15000, 24000];
}

/// <summary>
/// When a SID's voices have all gone quiet, by their envelopes' own times: a voice with its gate off
/// once its release has run its course, one with its gate on and nothing to sustain once its attack
/// and decay have, and the whole chip once its volume is turned to nothing. Its volume changing - a
/// fade, or a digi played through it - is sound too. Heard from the writes alone, so a C64 that only
/// records knows it as well as one that plays.
/// </summary>
internal sealed class Quiet
{
    private static readonly int[] AttackMs = EnvelopeTimes.AttackMs, DecayMs = EnvelopeTimes.DecayMs;

    private readonly byte[] _registers = new byte[0x19];
    private readonly bool[] _gate = new bool[3];

    /// <summary>The cycle each voice's gate last went on or off.</summary>
    private readonly long[] _edge = new long[3];

    private long _volumeChanged;

    public void Write(long cycle, int register, int value)
    {
        if (register > 0x18)
            return;
        if (register is 4 or 11 or 18)
        {
            int voice = register / 7;
            bool gate = (value & 1) != 0;
            if (gate != _gate[voice])
                (_gate[voice], _edge[voice]) = (gate, cycle);
        }
        else if (register == 0x18 && ((value ^ _registers[0x18]) & 0x0F) != 0)
        {
            _volumeChanged = cycle;
        }
        _registers[register] = (byte)value;
    }

    /// <summary>The cycle from which nothing of the chip is heard, at <paramref name="now"/>; null
    /// while something still is.</summary>
    public long? Since(long now, double clockHz)
    {
        long Ms(int ms) => (long)(ms * clockHz / 1000);
        if ((_registers[0x18] & 0x0F) == 0)
            return _volumeChanged;

        long since = _volumeChanged;
        for (int voice = 0; voice < 3; voice++)
        {
            int attackDecay = _registers[voice * 7 + 5], sustainRelease = _registers[voice * 7 + 6];
            long quiet;
            if (!_gate[voice])
                quiet = _edge[voice] + Ms(DecayMs[sustainRelease & 0x0F]);
            else if (sustainRelease >> 4 == 0)
                quiet = _edge[voice] + Ms(AttackMs[attackDecay >> 4]) + Ms(DecayMs[attackDecay & 0x0F]);
            else
                return null;
            since = Math.Max(since, quiet);
        }
        return since <= now ? since : null;
    }
}
