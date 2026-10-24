namespace MagSid.Assembler;

/// <summary>
/// Where each of the player's variables sits in zero page.
///
/// This has to agree with musicplayer.s to the byte. There the addresses are handed out by
/// m_ZP.b/m_CreateZPs, which allocate upward from addr.music.zp in declaration order, with two of
/// the blocks emitted only when the song uses vibrato or slide. So the order of the declarations
/// below is not cosmetic - it is the layout, and a name moved one line changes every instruction
/// that touches it.
///
/// Verified against the symbol files c6510 produces: BellaCiao (vibrato, no slide) puts
/// voice_loop_counter at $48, voice_vib_delay at $60 and filter_delta at $65, and SOS (neither)
/// puts filter_delta at $50 for 49 bytes in total. ZeroPageLayoutTests pins both.
/// </summary>
public sealed class ZeroPageLayout
{
    /// <summary>The three channels' copies of a variable are consecutive, which is all that
    /// "array,x" needs.</summary>
    private const int Channels = 3;

    private int _next;

    public ZeroPageLayout(PlayerFeatures features, int baseAddress = DefaultBaseAddress)
    {
        _next = baseAddress;
        Base = baseAddress;

        ZpLb = Scalar();
        ZpHb = Scalar();
        ZpCh = Scalar();

        SongSpeed = Scalar();

        VoicePatternIdx = Array();
        VoiceEvent = Array();
        VoiceDuration = Array();
        VoiceRowDur = Array();
        VoiceNote = Array();
        VoiceWtPos = Array();
        VoiceArpPos = Array();
        VoiceRoot = Array();
        VoicePwLo = Array();
        VoicePwHi = Array();
        VoiceInst = Array();
        VoicePrevPw = Array();
        VoiceLoopCounter = Array();
        VoiceLastWaveform = Array();

        // Slide and vibrato state, present only when the song uses them - which is what makes the
        // scalars below move between songs.
        if (features.FreqMod)
        {
            VoiceFreqLo = Array();
            VoiceFreqHi = Array();
            VoiceOffLo = Array();
            VoiceOffHi = Array();
        }

        if (features.Vibrato)
        {
            VoiceVibDir = Array();
            VoiceVibCnt = Array();
            VoiceVibDelay = Array();
        }

        // The rate a pattern's slide row set, per voice - only when some pattern has one.
        if (features.Slide)
        {
            VoiceSlide = Array();
        }

        FilterPos = Scalar();
        FilterPhase = Scalar();
        FilterDelta = Scalar();

        // Funk speed's shuffle byte, last - so a song without it keeps every address it had.
        if (features.Funk)
            SongShuffle = Scalar();

        End = _next;
    }

    public const int DefaultBaseAddress = 0x20;

    public int Base { get; }

    /// <summary>One past the last byte used, so <c>End - Base</c> is ZP_Player_Amount.</summary>
    public int End { get; }

    public int ByteCount => End - Base;

    public int ZpLb { get; }
    public int ZpHb { get; }
    public int ZpCh { get; }
    public int SongSpeed { get; }

    public int VoicePatternIdx { get; }
    public int VoiceEvent { get; }
    public int VoiceDuration { get; }
    public int VoiceRowDur { get; }
    public int VoiceNote { get; }
    public int VoiceWtPos { get; }
    public int VoiceArpPos { get; }
    public int VoiceRoot { get; }
    public int VoicePwLo { get; }
    public int VoicePwHi { get; }
    public int VoiceInst { get; }
    public int VoicePrevPw { get; }
    public int VoiceLoopCounter { get; }
    public int VoiceLastWaveform { get; }

    /// <summary>Zero unless the song uses vibrato or slide - reading one of these when the feature
    /// is off would be a port bug, so they are left obviously invalid rather than defaulted.</summary>
    public int VoiceFreqLo { get; }
    public int VoiceFreqHi { get; }
    public int VoiceOffLo { get; }
    public int VoiceOffHi { get; }

    public int VoiceVibDir { get; }
    public int VoiceVibCnt { get; }
    public int VoiceVibDelay { get; }
    public int VoiceSlide { get; }

    /// <summary>Song_Shuffle: zero unless the song has funk speed. music.init clears it, and every
    /// tick flips it with the two speeds' difference.</summary>
    public int SongShuffle { get; }

    public int FilterPos { get; }
    public int FilterPhase { get; }
    public int FilterDelta { get; }

    private int Scalar()
    {
        int address = _next;
        _next += 1;
        return address;
    }

    private int Array()
    {
        int address = _next;
        _next += Channels;
        return address;
    }
}

/// <summary>Which of the optional effects this song needs, and therefore how much of the player is
/// assembled at all. Mirrors v_vibrato / v_slide / v_freqmod in Startup\Tables.s, which are set by
/// scanning the song's INSTRUMENTS block.</summary>
/// <param name="SoftHardRestart">Some instrument has SOFTHR, so the hard restart has to look at
/// the outgoing instrument's flag; without one the test is left out of the player.</param>
public sealed record PlayerFeatures(bool Vibrato, bool Slide, bool SoftHardRestart = false, bool Funk = false)
{
    public static readonly PlayerFeatures None = new(false, false);

    /// <summary>Vibrato and slide share the frequency-modulation block, which is why having both
    /// costs less than the two on their own added together.</summary>
    public bool FreqMod => Vibrato || Slide;
}
