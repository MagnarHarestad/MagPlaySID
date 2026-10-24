namespace MagSid.Editor.App;

/// <summary>
/// The About window's document: what the editor is, how a song is put together, and every key
/// binding - as runs of text, each in one style. The Windows window and the Mac and Linux one
/// write the same document; <see cref="Platform"/> holds the few words that differ between them.
/// </summary>
public static class AboutDocument
{
    public enum Style { Title, Subtitle, Heading, Body, Mono }

    public sealed record Run(Style Style, string Text);

    /// <param name="CommandKey">The key the shortcuts are written with: Ctrl, or Cmd on a Mac.</param>
    /// <param name="Menu">The menu that holds the settings and the instrument files.</param>
    /// <param name="MenuBar">The menu bar, as a key table.</param>
    /// <param name="BankFolder">Where the user's own bank sounds go.</param>
    /// <param name="AudioCredit">The libraries that play the sound and read MIDI.</param>
    public sealed record Platform(string CommandKey, string Menu, string MenuBar, string BankFolder, string AudioCredit);

    public static readonly Platform Windows = new(
        "Ctrl",
        "Menu",
        "  Menu              About, New, Open, Import MIDI, Save, Save As,\n" +
        "                    Load Instrument, Save Instrument, Export to .sid,\n" +
        "                    Load and Play .sid, Show High Hex-Values,\n" +
        "                    Swap Spacebar Behavior, Exit\n" +
        "  Signal Path       Frame-by-frame trace of one instrument\n" +
        "  Digi to SID-osc   Turn a .wav into ring-modulated instruments\n" +
        "  Optimize          Shrink the song without changing how it sounds\n" +
        "  MIDI              Pick a MIDI input, forget knob bindings\n" +
        "  DAW / Retro       Switch view\n\n",
        "%APPDATA%\\MagSid.Editor\\Bank",
        "Audio and MIDI: NAudio by Mark Heath.");

    /// <param name="version">The editor's version - from the project's &lt;Version&gt;, so the
    /// number lives in one place.</param>
    public static IReadOnlyList<Run> Build(Version? version, Platform platform)
    {
        var runs = new List<Run>();
        void Add(Style style, string text) => runs.Add(new Run(style, WithCommandKey(text, style, platform.CommandKey)));
        void Heading(string text) => Add(Style.Heading, text + "\n");
        void Body(string text) => Add(Style.Body, text);
        void Mono(string text) => Add(Style.Mono, text);

        Add(Style.Title, "MagSID Editor\n");
        Add(Style.Subtitle, $"Version {version?.Major}.{version?.Minor}  ·  Magnar Harestad\n\n");

        Body("A tracker for the MAGSIDv7 player on the Commodore 64. It edits Patterns.s song " +
             "files and exports finished .sid files. Two views edit the same song: Retro, a " +
             "classic tracker grid, and DAW, an arrangement with a piano roll. Switch between them " +
             "from the menu bar.\n\n");

        // ------------------------------------------------------------------------------------
        Heading("Everywhere");
        Mono("  F1          Play from the start of the song\n" +
             "  F2          Play from the current position\n" +
             "  F3          Loop the pattern under the cursor\n" +
             "  F4          Stop\n" +
             "  Space       Toggle Edit / Jam  (or Play / Stop, see below)\n" +
             "  F5          Toggle Edit / Jam\n" +
             "  F9          Export to .sid\n" +
             "  F10         Open\n" +
             "  F11         Save As\n" +
             "  Ctrl+Z / Y  Undo / Redo (200 steps)\n\n");
        Body("Edit writes the notes you play into the pattern. Jam only plays them. Switching " +
             "mode also silences every voice. The toolbar shows the mode and which key toggles " +
             "it; clicking it toggles too.\n\n" +
             $"{platform.Menu} → Swap Spacebar Behavior makes Space play and stop the song (from the start, " +
             "like F1). Edit / Jam then stays on F5.\n\n" +
             "Song Length is the exported size. Rastertime is what the player costs per frame in " +
             "raster lines, average / worst, measured by running its own 6502 code over one pass " +
             "of the song; hover it for cycles and where the worst frame is. While the song plays, " +
             "the first figure is live: the dearest of the frames just heard.\n\n");

        Heading("The menu bar");
        Mono(platform.MenuBar);

        Heading("Playing notes");
        Mono("  Q 2 W 3 E R 5 T 6 Y 7 U     octave + 1\n" +
             "  Z S X D C V G B H N J M     current octave\n" +
             "  C C#D D#E F F#G G#A A#B\n\n" +
             "  '  /  Shift+'    Octave down / up\n" +
             "  Return           Note off (\"---\")\n" +
             "  Numpad 1 / 2     Slide up / down: then two hex digits (0-9, A-F) for the rate per\n" +
             "                   frame, e.g. 120 or 210. 100 stops it. Esc cancels\n\n");
        Body("A held key sustains with the instrument's full program; up to three at once, one " +
             "per SID voice. A MIDI keyboard plays the same way once chosen under MIDI.\n\n");

        // ------------------------------------------------------------------------------------
        Heading("Retro view");
        Body("Each channel has four fields:\n\n");
        Mono("  CH    Song order position (\"01→\"), RST01 marks the loop-back point\n" +
             "  PAT   Pattern number, and loop markers LS / LE04\n" +
             "  NOT   Note, \"...\" hold, \"---\" note off, 1xx / 2xx slide (orange)\n" +
             "  INS   Instrument number, 1-16 (two quick keys make two digits)\n\n");
        Body("A note off (\"---\") releases the gate and stops the wavetable; pulse sweep, note " +
             "program, vibrato and slide run on through the release.\n\n");
        Body("LS / LE loops are drawn unrolled, as they play: the first pass as written, each " +
             "later pass below it as a linked copy - violet, with a rail and \"↻2/4\" where it " +
             "starts. A row is then a tick on every channel, so the channels line up wherever " +
             "the song is stopped or started. Notes, instruments and slides typed in a linked pass " +
             "go into the pattern itself, so every pass changes (a dashed box marks the same row " +
             "in the other passes); inserting, deleting and loop changes are made in pass 1. A " +
             $"loop with no count is unrolled to where the longest channel restarts. {platform.Menu} → " +
             "Unroll Loops switches back to drawing a loop's rows once, here and in the DAW.\n\n");
        Mono("  Arrows             Move between rows and fields\n" +
             "  Tab / Shift+Tab    Next / previous channel\n" +
             "  Home / End         First / last row\n" +
             "  PgUp / PgDn        16 rows up / down\n" +
             "  +  /  -            Select next / previous instrument\n" +
             "  Insert             Insert a hold row\n" +
             "  Delete             Delete the row\n" +
             "  Backspace          Clear the note, or the loop marker\n" +
             "  Shift+End          Cut the pattern off after the cursor\n" +
             "  Ctrl+L / Ctrl+E    Loop start / loop end\n" +
             "  Shift+C / Shift+V  Copy / paste the whole pattern\n" +
             "  Click CH header    Mute / unmute the channel\n\n");
        Body("LE04 plays the looped section four times; a bare LE loops forever. On an LE row, " +
             "digits in the PAT field set the count.\n\n");

        Body("In the CH field:\n\n");
        Mono("  Digits               Pattern at this position (one past the last creates it),\n" +
             "                       or the loop target on the RST row\n" +
             "  Insert               Add a position\n" +
             "  Delete               Remove the position\n" +
             "  Ctrl+Up / Ctrl+Down  Move the position\n\n");

        Heading("Retro instruments");
        Body("The list shows all 16 slots and how many are used. The + over it adds an instrument, and " +
             "so does clicking \"+ New instrument\" in the first empty slot or pressing Insert; the " +
             "arrow beside + adds one from the sound bank - the demo songs' piano, bass, strings, " +
             "synth lead and the rest - or from a file. The other two buttons duplicate and delete " +
             "(Delete). A new instrument opens its name for typing; edit the Name column to rename " +
             "later. Right-click still has Add, Copy and Delete. The editor below edits the selected " +
             "instrument directly. \"Open bank folder\" on the arrow shows where an instrument saved " +
             "as Piano.json, Bass.json... replaces that bank sound.\n\n" +
             $"{platform.Menu} → Save Instrument writes the selected instrument to a .json file, and Load " +
             "Instrument adds one or more such files as new instruments - in either view.\n\n");
        Mono("  A/D, S/R           Envelope, one hex byte each\n" +
             "  Pulse, Sweep       Pulse width and its change per frame\n" +
             "  Cont.              Keep the pulse sweep running across notes\n" +
             "  No HR              Skip the hard restart - legato notes\n" +
             "  Soft HR            When this instrument's note is replaced, clear only the gate\n" +
             "                     and keep its waveform instead of cutting it with $08 - no\n" +
             "                     click. Costs the player 10 bytes, and only if used\n" +
             "  Vibrator, D/S,     Vibrato on/off, delay/speed byte, depth byte\n" +
             "  Depth\n" +
             "  Program grid       One frame per row: wavetable on the left, filter on the\n" +
             "                     right. Right-click for Add frame / Remove / Up / Down\n" +
             "  After the last     Loop, Hold or Root (settle on the row's own note)\n" +
             "  note\n\n");
        Body("Wave values: $11 triangle, $21 saw, $41 pulse, $81 noise; add $02 for sync and " +
             $"$04 for ring (ring is heard on triangle only). {platform.Menu} → Show High Hex-Values shows " +
             "the GoatTracker forms ($80+ notes, 9x filter modes); both forms can be typed.\n\n");

        // ------------------------------------------------------------------------------------
        Heading("DAW view");
        Mono("  ARRANGE / INSTRUMENT   The two pages\n" +
             "  AUTOSCROLL             Let the roll follow playback. A clip clicked while\n" +
             "                         playing stays until its channel reaches the next clip\n" +
             "  OCT                    Octave the typing keys play in\n\n");
        Body("Notes are written into the piano roll only in Edit mode, and only after you clicked " +
             "the roll or its beat bar. Anywhere else they just sound.\n\n");

        Heading("Instrument strip");
        Mono("  Click              Select (on Arrange it also re-voices the selected note)\n" +
             "  + New instrument   The row after the last instrument: adds one, named as you type,\n" +
             "                     and opens the instrument page from Arrange\n" +
             "  F2 / Double-click  Rename\n" +
             "  Right-click        Add, Copy, Delete\n\n");

        Heading("Arrange");
        Body("A clip is as wide as the time it plays - an LS/LE loop counted once per pass - so " +
             "the lanes line up the way the song sounds. While the song plays, each lane has its " +
             "own red playhead (a shorter lane has looped round its RST by itself), and the " +
             "project cursor rides along the longest lane.\n\n");
        Body("Loops are unrolled here as in Retro: a clip shows its loop's later passes (\"↻2/4\"), " +
             "and the piano roll draws one column per tick, the later passes as linked copies - a " +
             "violet wash, lighter dashed notes. Notes, slides and recording in a linked pass go " +
             "into the pattern itself; inserting, deleting and loop markers are made in pass 1. " +
             "The roll's playhead and a take follow the pass actually playing.\n\n");
        Mono("  Click ROW ruler    Put the project cursor (the F2 flag) there; right-click\n" +
             "                     puts it back at the start\n" +
             "  Drag in the ruler  Set the left and right locators (L ... R)\n" +
             "  CYCLE / Numpad /   Loop the L ... R range while playing - every channel where\n" +
             "                     the song really has it, with no gap at the jump\n" +
             "  F2                 Play from the project cursor, the other channels wherever\n" +
             "                     the song really has them - also while playing\n" +
             "  F4                 Stop: the cursor stays where the song stopped, so F2 carries\n" +
             "                     on from there. F4 again: back to where playback started\n" +
             "  Click a clip       Show it in the piano roll\n" +
             "  Two digits         Set the selected clip's pattern\n" +
             "  Drag a clip's end  Change the pattern's length (later clips follow)\n" +
             "  Ctrl+Left / Right  Move the clip\n" +
             "  Insert             Duplicate the clip\n" +
             "  Shift+Insert       Insert a clip with a new, empty pattern (the next number)\n" +
             "  Delete             Remove the clip (a channel keeps at least one)\n" +
             "  Right-click        The same as a menu, on a clip or a lane's RST block\n" +
             "  RST block          After the last clip: where the channel restarts (entry and\n" +
             "                     pattern). Hover or select it to see the target clip.\n" +
             "                     Double-click, Enter or two digits to set it. Insert adds a\n" +
             "                     clip before it (Shift: a new pattern), Delete removes the\n" +
             "                     last clip\n" +
             "  Ctrl+R             Channel restarts at this clip\n" +
             "  Click CH name      Mute / unmute the channel\n" +
             "  Arrows             Move between clips and channels\n\n");

        Heading("Piano roll tools");
        Mono("  Object Selection   Click a note, Ctrl+click to add, drag a box to select\n" +
             "                     several. Drag to move them, drag an end to resize them\n" +
             "  Draw               Click and drag - the note lasts as long as the drag\n" +
             "  Erase              Click or drag across notes\n" +
             "  Trim               Click inside a note to cut off the rest\n" +
             "  Split              Click inside a note to split it there\n" +
             "  Glue               Click a note to join it to the next\n" +
             "  Line               Drag to draw a run of notes\n\n");
        Body("With notes selected:\n\n");
        Mono("  Ctrl+A    Select every note in the pattern\n" +
             "  Ctrl+X    Cut\n" +
             "  Ctrl+C    Copy\n" +
             "  Ctrl+V    Paste at the selected row - click the beat bar to pick it\n" +
             "  Delete    Remove\n" +
             "  Escape    Deselect\n\n");

        Heading("Piano roll keys");
        Mono("  Right-click        Delete the note\n" +
             "  Drag a note's end  Resize it\n" +
             "  Ctrl+click         Note off (Draw tool)\n" +
             "  Up / Down          Transpose the selected note (it plays while held)\n" +
             "  Left / Right       Move it a beat\n" +
             "  Ctrl+Left / Right  Shorten / lengthen it\n" +
             "  Return             Note off on the selected row\n" +
             "  Insert             Insert a row\n" +
             "  Delete             Delete the note, or the row if it is empty\n" +
             "  Shift+Delete       Delete the row\n" +
             "  Shift+End          Cut the pattern off after the selected row\n" +
             "  Ctrl+L / Ctrl+E    Loop start before / loop end after the selected row - added\n" +
             "                     as a row of its own, so no note gets shorter\n" +
             "  Digits on an LE    Its loop count (two digits = one number, 0 = forever)\n" +
             "  Shift+C / Shift+V  Copy / paste the whole pattern\n" +
             "  BEATS / ROWS       Grid in bars and beats, or in rows\n\n");
        Body("The beat bar between the header and the grid selects a row without writing a note " +
             "- use it to place markers, note offs or a paste on an empty row. Loop markers show " +
             "there: click to select (type an LE's count, or Up / Down), right-click to remove.\n\n");

        Heading("Recording");
        Body("REC, next to the pattern's name, plays the song from the selected clip's place in it " +
             "- the other channels play whatever really sounds against that clip - and records " +
             "the MIDI keyboard (or the computer keyboard's piano keys) into its pattern, with the " +
             "selected instrument. A key lands on the nearest row and lasts until it is let go, " +
             "where a \"---\" stops it; one channel plays one note, so a new key ends the last. " +
             "Notes are added on top of what is there, every time the song plays the clip's " +
             "pattern on that channel. REC again, or Stop, ends the take - one undo step.\n\n");
        Body("How a take starts, from the toggles above the arrange lanes:\n\n");
        Mono("  PRE-ROLL   On by default: the song starts a bar (16 rows) before the punch-in,\n" +
             "             so the music leads in. Nothing played then is written\n" +
             "  CLICK      Counts that bar in with a metronome beep on each beat, the first of\n" +
             "             the bar higher - mixed in after the SID, so no voice is taken. With\n" +
             "             PRE-ROLL off, a bar of clicks alone comes before the song\n" +
             "  PUNCH      With the locators set: notes are written only between L and R; a\n" +
             "             key held at R is let go there. With CYCLE too, every lap of L ... R\n" +
             "             records on top of the last\n\n");

        Heading("Slide lane");
        Body("With the Draw tool:\n\n");
        Mono("  Click / drag       Draw the slide rate on those rows: above the line 1xx up,\n" +
             "                     below it 2xx down, farther out is faster\n" +
             "  Right-click / drag Back to 0 (no slide)\n" +
             "  Hover              Shows the value a click would write\n\n");
        Body("With Object Selection:\n\n");
        Mono("  Click / drag       Select a run of rows in the lane\n" +
             "  Ctrl+C / Ctrl+X    Copy / cut the slide rates there\n" +
             "  Delete             No slide over the selection\n" +
             "  Ctrl+V             Paste from the selected row - click the lane or the beat\n" +
             "                     bar to pick it, in this pattern or another\n" +
             "  Escape             Deselect\n\n");
        Body("The other tools leave the lane alone. A paste puts each row's rate down at the same " +
             "distance from where it is aimed; rows past the pattern's end are cut off, and the rows " +
             "after it keep the slide they had. Ctrl+V pastes whichever was copied last, notes or " +
             "slides.\n\n");
        Body("The lane is the pattern's 1xx / 2xx rows seen as a level on every row. Only the rows " +
             "where the rate changes become slide rows - bright bars with their value under them - " +
             "and each costs two bytes, so a run drawn at one level is a single row. A note's own " +
             "row and a note off take no slide (a dot); the release after a note off does. The " +
             "notes above show the glide the player will play, frame by frame, ending in a dot at " +
             "the pitch reached.\n\n");

        Heading("Instrument page");
        Mono("  Click the name        Rename\n" +
             "  Waveform tab          Click: that waveform only. Right-click: combine.\n" +
             "                        With a wavetable step selected it changes that step\n" +
             "                        (click the step again or Escape to go back)\n" +
             "  Silence               $09: no waveform, test bit on - a drum's usual first step\n" +
             "  Waveform strip        reSID's own output for the waveform and pulse width, on\n" +
             "                        the SID chosen in the toolbar - turn Pulse to see it\n" +
             "  Sync / Ring           Modulate against the previous voice\n" +
             "  Knob                  Drag up / down (Shift: fine), wheel, double-click to\n" +
             "                        reset, right-click for Learn MIDI / Forget\n" +
             "  CONT / NO HR / SOFT HR  See Retro instruments\n" +
             "  Envelope graph        While the song plays, a dot follows the instrument's\n" +
             "                        envelope as the SID plays it (the loudest voice using it)\n" +
             "  Program lanes         Click + to add, wheel or Up / Down to change (Shift: x16),\n" +
             "                        right-click or Delete to remove. Insert copies the\n" +
             "                        selected step in right after it (the rest move along);\n" +
             "                        with none selected it adds a step at the end\n" +
             "  Wavetable step        Double-click to type the control byte in hex ($41, 11),\n" +
             "                        $01-$FF\n" +
             "  Note program step     Double-click to type the note (C-4, D#5, C4). Enter keeps,\n" +
             "                        Escape cancels\n" +
             "  LOOP / HOLD / ROOT    What happens after the last note step (FROM: loop start)\n" +
             "  REC (note program)    The chord held on the MIDI keyboard becomes the note\n" +
             "                        program, low to high, looping - an arpeggio. Each key\n" +
             "                        down rewrites it from the keys held; letting go keeps it\n" +
             "  Filter step           Click a step to edit it, double-click to type the cutoff\n" +
             "                        in hex ($40 or 40). SETUP makes it a setup record\n" +
             "                        with mode, volume, resonance, routing and sweep\n" +
             "  Cutoff graph          The filter program played frame by frame, sweep included.\n" +
             "                        Scaled to its highest cutoff (shown top left). While the\n" +
             "                        song plays, a dot shows the frame and the real cutoff\n" +
             "                        (a running sweep can put it off the drawn curve).\n" +
             "                        Drag a step up / down to change its cutoff (Shift:\n" +
             "                        fine) - one undo step per drag\n\n");

        // ------------------------------------------------------------------------------------
        Heading("Tools");
        Body("Optimize removes dead loop markers, turns repeated rows into loops where that saves " +
             "bytes, makes a pattern the song order repeats in a row loop itself instead (when the " +
             "order bytes saved beat the loop's three), and trims program steps the song never " +
             "reaches. It shows what it will do " +
             "first, and Ctrl+Z undoes it.\n\n" +
             "Signal Path traces one instrument frame by frame - register writes, gate, envelope " +
             "and output - and plays it while a marker follows along.\n\n" +
             "Digi to SID-osc turns a .wav into ring-modulated instruments, the classic C64 " +
             "speech trick. Load a clip, adjust the settings, Audition, then Add to Song.\n\n" +
             "Import MIDI makes a new song from a .mid file. Choose a track for each of the three " +
             "voices: Melody keeps the highest note of a chord, Bass the lowest, and Drums + bass " +
             "puts kick, snare and the bass line on one voice the way the demo songs' channel 1 " +
             "does - the KickBass settles on the bass note, hi-hats only if ticked. Chords reads the " +
             "chord of each beat from the part that carries the harmony and plays it as an arpeggio " +
             "instrument: major or minor, a seventh only when it is really there, and an inversion " +
             "is the same chord - so C-E-G, E-G-C and G-C-E share one instrument. A chord is struck " +
             "again where the part plays, and with Chord gate-off its gate goes off 3 rows after it " +
             "is struck - 4 rows, with a longer release ($B, or $C past 16 rows), for chords that " +
             "usually ring longer than 8 - done in the chord's wavetable, so it costs no pattern " +
             "bytes. Lead and bass keep the MIDI file's own note ends. " +
             "A part without a program plays as a piano, a bass part as a bass. With GM sound bank " +
             "on, a part's General MIDI sound becomes an instrument from the demo songs, by family - " +
             "piano, organ, guitar, bass, strings, brass, flute, synth lead and so on - without " +
             "vibrato unless Keep vibrato is ticked, and with a filter only on channel 1. A file " +
             $"named after a family's sound (Piano.json, Bass.json...) in {platform.BankFolder} " +
             "replaces it. Notes snap to a " +
             "16th or 32nd grid at one speed, and repeated bars are stored once; the dialog shows " +
             "the size and what had to give before anything is replaced. Run Optimize afterwards.\n\n");
        Mono("  Voiced threshold %   How sure pitch detection must be\n" +
             "  Cutoff min / max     Filter range for the formant sweep\n" +
             "  Segment ms           Retrigger interval - shorter buzzes, longer blurs\n" +
             "  Carrier waveform     Auto, or pin one waveform\n\n");

        Heading("Export and the SID player");
        Body("Export to .sid builds a PSID file with the real MAGSIDv7 player, at the load " +
             "address you choose. Patterns no channel plays are left out.\n\n" +
             "Load and Play .sid opens the SID player: spectrum, one scope per voice, and the " +
             "played / total time.\n\n");
        Mono("  Space          Pause / continue\n" +
             "  Escape         Close\n" +
             "  Export audio   Save as WAV, MP3 or FLAC; the length field sets how long\n\n");

        Heading("Credits");
        Body("MagSID and its MAGSIDv7 player were written by Magnar Harestad. Thanks to " +
             "Robert Lundquist (Krikkit) and Fredrik Ternell (Swallow) of Censor Design for " +
             "technical advice and tools.\n\n" +
             "SID emulation: reSID by Dag Lem, translated to C#.\n" +
             $"{platform.AudioCredit}\n\n" +
             "Exported .sid files, and the player inside them, may be used freely.\n");

        return runs;
    }

    /// <summary>The shortcuts written with another command key. In a key table the description
    /// column stays where it was: a shorter key name gives its letters back as spaces after the
    /// keys, at the gap where the description starts.</summary>
    private static string WithCommandKey(string text, Style style, string key)
    {
        if (key == "Ctrl")
            return text;
        if (style != Style.Mono)
            return text.Replace("Ctrl+", key + "+");

        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int count = (line.Length - line.Replace("Ctrl+", "").Length) / "Ctrl+".Length;
            if (count == 0)
                continue;

            line = line.Replace("Ctrl+", key + "+");
            int indent = line.Length - line.TrimStart().Length;
            int gap = line.IndexOf("  ", indent, StringComparison.Ordinal);
            int pad = count * ("Ctrl".Length - key.Length);
            lines[i] = gap < 0 || pad <= 0 ? line : line.Insert(gap, new string(' ', pad));
        }

        return string.Join("\n", lines);
    }
}
