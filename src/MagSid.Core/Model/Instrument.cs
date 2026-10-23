namespace MagSid.Core.Model;

/// <summary>
/// Mirrors one row of the INSTRUMENTS: block: nr, $waveform, $AD, $SR, plus any of four
/// optional trailing sections - WAVE, ARP, FLT and PW - in any order.
///
/// <see cref="Wavetable"/> and <see cref="Arpeggio"/> are the two halves of one idea, and are
/// what make a drum or a bass transient expressible rather than just an arpeggio: they run in
/// step, one frame each, the first choosing the waveform and the second the pitch. A bass is a
/// frame of noise at a fixed high pitch followed by pulse at <see cref="ArpeggioEnding.Root"/>;
/// a kick is noise into pulse over a falling pitch that then holds.
/// </summary>
public sealed class Instrument
{
    /// <summary>50% duty. The player defaults to this rather than 0 because a pulse voice at
    /// width 0 is simply silent, which is a baffling thing to hand someone who just picked the
    /// pulse waveform.</summary>
    public const int DefaultPulseWidth = 8;

    public int Number { get; set; }

    /// <summary>Optional label, purely for the editor's own benefit - never touches a SID
    /// register or the assembled song. Empty means unnamed.</summary>
    public string Name { get; set; } = string.Empty;

    public byte Waveform { get; set; }
    public byte AttackDecay { get; set; }
    public byte SustainRelease { get; set; }

    /// <summary>
    /// One SID control-register byte per frame - waveform select plus the gate bit, written
    /// straight to $D404. When the list runs out the last value is held, which is how a
    /// program sustains without needing a loop.
    ///
    /// Empty means the one-step program <c>{ Waveform | GATE }</c>: a plain sustained note,
    /// the behaviour every instrument had before wavetables existed.
    /// </summary>
    public List<byte> Wavetable { get; } = [];

    /// <summary>
    /// One step per frame, running in step with <see cref="Wavetable"/>. Empty with an
    /// <see cref="ArpeggioEnding.Loop"/> or <see cref="ArpeggioEnding.Hold"/> ending means the
    /// instrument has no note program at all and simply plays the row's note.
    ///
    /// Every step is a note, and notes are <em>absolute</em>, not transposed from the pattern
    /// row's note. That is a deliberate design decision, not a shortcut: it lets a step be a
    /// single byte drawn from the song's existing note dictionary, and a chord whose intervals
    /// change needs its own instrument in any case. <see cref="ArpeggioEnding.Root"/> is the
    /// escape hatch - it returns the program to whatever the pattern actually played.
    /// </summary>
    public List<NoteName> Arpeggio { get; } = [];

    /// <summary>What happens after the last <see cref="Arpeggio"/> step.</summary>
    public ArpeggioEnding ArpeggioEnding { get; set; } = ArpeggioEnding.Loop;

    /// <summary>
    /// Which step an <see cref="ArpeggioEnding.Loop"/> goes back to. 0 is the whole list, which
    /// is what an arpeggio wants; a later index makes the program play an intro once and then
    /// cycle only its tail, which is what a drum wants - it rings out on its last notes instead
    /// of re-striking its attack. Ignored for the other endings.
    ///
    /// The assembled format has always carried an absolute offset here, so this costs the
    /// player nothing; it was only ever the grammar that could not say it.
    /// </summary>
    public int ArpeggioLoopPoint { get; set; }

    /// <summary>True when this instrument has a note program at all. A bare
    /// <c>ARP ROOT</c> counts: it has no steps but still ends on the row's note.</summary>
    public bool HasArpeggio => Arpeggio.Count > 0 || ArpeggioEnding == ArpeggioEnding.Root;

    /// <summary>One filter step per frame, one-shot - when the program runs out the cutoff
    /// simply stays where it landed. Empty means this instrument leaves the filter alone.
    /// The SID has a single filter, so the last instrument to trigger with a program owns it
    /// across all three channels.</summary>
    public List<FilterStep> Filter { get; } = [];

    /// <summary>Pulse width the note starts at, in 1/16ths (0-15).</summary>
    public int PulseWidth { get; set; } = DefaultPulseWidth;

    /// <summary>Added to the 12-bit pulse width every frame (-128..127). The result wraps
    /// within 12 bits rather than clamping, so a sweep that runs off either end keeps
    /// moving.</summary>
    public int PulseSweep { get; set; }

    /// <summary>
    /// The sweep belongs to the channel rather than this instrument: a new note leaves whatever
    /// the channel's pulse width already is running, instead of restarting it at
    /// <see cref="PulseWidth"/>. This is how the old MAGSID player this format descends from
    /// always worked - its sweep accumulator was per channel, never reset by a note - and it is
    /// opt-in here because most instruments want the opposite: a percussive attack that starts
    /// from the same width every time.
    /// </summary>
    public bool PulseContinuous { get; set; }

    /// <summary>
    /// Skips the player's hard-restart trick on retrigger entirely: no AD/SR reset, no gate
    /// pulse, no oscillator reset - only the frequency changes, so the note is a genuine legato,
    /// picking up on the new pitch wherever the envelope already was (still decaying, already in
    /// sustain, whatever it happened to be doing) rather than re-attacking.
    ///
    /// The trick this opts out of relies on its reset pair's release rate being no slower than
    /// any instrument's own decay/attack it might interrupt - true for most instruments, but one
    /// with an unusually slow decay (a pad-like sustain, say) can have its envelope's rate
    /// counter sitting well past the reset pair's own period at the exact moment of retrigger,
    /// which is a real SID hardware quirk (see VoiceEngine.TriggerNote's own comment) that then
    /// freezes the *next* note silent rather than the reset actually running. That correctness
    /// concern is one reason to opt an instrument out; wanting the legato itself, independent of
    /// any stall risk, is the other.
    /// </summary>
    public bool NoHardRestart { get; set; }

    /// <summary>
    /// When the next note on this voice replaces this instrument's note, its hard restart clears
    /// only the gate and keeps the waveform - GoatTracker's way - instead of writing $08, which
    /// stops the waveform and resets the oscillator mid-sound. The note fades through that one
    /// frame instead of being cut, which is where the click on a cut note comes from.
    ///
    /// Like <see cref="NoHardRestart"/> it belongs to the outgoing instrument: it is this
    /// instrument's note that ends. The envelope reset (AD $00, SR $07) runs either way. An
    /// incoming instrument that wants its oscillator reset puts $09 first in its wavetable.
    /// </summary>
    public bool SoftHardRestart { get; set; }

    /// <summary>
    /// Vibrato's first table byte: delay in the top nibble, speed in the low nibble. Delay is in
    /// units of four frames ($f = 60 frames, 1.2s on PAL) counted from the note's trigger; speed
    /// is how many further frames the pitch keeps moving one way before turning round (0 = it
    /// turns every frame). Meaningful only while <see cref="VibratoDepth"/> is non-zero.
    /// </summary>
    public byte VibratoDelaySpeed { get; set; }

    /// <summary>
    /// Vibrato's second table byte: how far the 16-bit SID frequency moves each frame, in the
    /// direction the speed nibble is currently running. 0 means the instrument has no vibrato at
    /// all - it is the "off" state, so the player needs no separate enable flag.
    /// </summary>
    public byte VibratoDepth { get; set; }

    public bool HasVibrato => VibratoDepth != 0;

    /// <summary>
    /// Takes on <paramref name="other"/>'s whole sound - every setting and program, and its name -
    /// keeping its own number, so the notes that play this instrument play the new sound. Programs
    /// are copied, never shared. Done by reflection so a setting added to the class later is
    /// carried too; InstrumentTests checks that every one is.
    /// </summary>
    public void CopySoundFrom(Instrument other)
    {
        foreach (var property in typeof(Instrument).GetProperties())
        {
            if (property.Name == nameof(Number) || property.GetIndexParameters().Length > 0)
                continue;

            if (property.CanWrite)
            {
                property.SetValue(this, property.GetValue(other));
            }
            else if (property.GetValue(this) is System.Collections.IList mine
                     && property.GetValue(other) is System.Collections.IEnumerable theirs)
            {
                mine.Clear();
                foreach (var item in theirs.Cast<object>().ToList())
                    mine.Add(item);
            }
        }
    }

    /// <summary>GoatTracker's 1xx/2xx slide as an instrument property: a signed rate added to the
    /// 16-bit SID frequency every frame the note sounds (positive up, negative down). 0 means no
    /// slide, which is also why the file grammar cannot spell a rate of 0.</summary>

    /// <summary>True when the pulse settings are the ones an instrument gets by saying
    /// nothing, so the writer can leave the PW section off entirely and a file round-trips
    /// unchanged.</summary>
    public bool HasDefaultPulse =>
        PulseWidth == DefaultPulseWidth && PulseSweep == 0 && !PulseContinuous;

    /// <summary>A plainly audible starting point: triangle with the gate bit set, instant
    /// attack/decay, full sustain and a short release. Used both for a new song's first
    /// instrument and for every one added afterwards, so "the instrument you get" is the same
    /// either way.
    ///
    /// <see cref="ArpeggioEnding.Root"/> here (not the property's own <see cref="ArpeggioEnding"/>
    /// default of <see cref="ArpeggioEnding.Loop"/>) is deliberately scoped to *new*
    /// instruments only: <see cref="MagSid.Core.Serialization.PatternsFileReader"/> constructs a
    /// bare <c>Instrument</c> and never touches this property when a file's own ARP section is absent,
    /// so every already-saved instrument keeps reading as Loop, exactly as it always has - only
    /// what "Add" hands you changed. Setting it on the shared property initializer instead would
    /// have made <see cref="HasArpeggio"/> true for every one of those existing instruments too,
    /// which would have broken round-trip idempotency (a plain instrument would gain an unwanted
    /// <c>ARP ROOT</c> section the moment it was next saved).</summary>
    public static Instrument CreateDefault(int number) => new()
    {
        Number = number,
        Waveform = 0x11,
        AttackDecay = 0x00,
        SustainRelease = 0xF0,
        ArpeggioEnding = ArpeggioEnding.Root,
    };
}
