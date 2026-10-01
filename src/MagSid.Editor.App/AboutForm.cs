using System.Drawing.Text;

namespace MagSid.Editor.App;

/// <summary>
/// The About window: what the editor is, how a song is put together, and every key binding.
///
/// Built as formatted text in a read-only RichTextBox rather than a pile of Labels, so the
/// whole document can be written in one place and scrolls as one. It carries the tracker's own
/// dark palette so it reads as part of the app rather than a stock Windows dialog.
/// </summary>
public sealed class AboutForm : Form
{
    // Shared with every other dark window in the app via EditorPalette so they cannot drift
    // into near-miss shades of each other.
    private static readonly Color ColorBackground = EditorPalette.Background;
    private static readonly Color ColorTitle = EditorPalette.Title;
    private static readonly Color ColorSubtitle = EditorPalette.Subtitle;
    private static readonly Color ColorHeading = EditorPalette.Heading;
    private static readonly Color ColorBody = EditorPalette.Body;
    private static readonly Color ColorMono = EditorPalette.Mono;

    private static readonly Font TitleFont = new("Segoe UI", 18f, FontStyle.Bold);
    private static readonly Font SubtitleFont = new("Segoe UI", 9.5f, FontStyle.Regular);
    private static readonly Font HeadingFont = new("Segoe UI", 12f, FontStyle.Bold);
    private static readonly Font BodyFont = new("Segoe UI", 10f, FontStyle.Regular);
    private static readonly Font MonoFont = new("Consolas", 10f, FontStyle.Regular);

    private readonly RichTextBox _text = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        BackColor = ColorBackground,
        TabStop = false,
        Margin = new Padding(0),
    };

    public AboutForm()
    {
        Text = "About MagSID Editor";
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 780);
        MinimumSize = new Size(520, 400);
        BackColor = ColorBackground;
        DarkTheme.ApplyTitleBar(this);

        // Styled explicitly: a default WinForms button on this dark background renders as dark
        // grey text on dark grey and is essentially invisible.
        var close = new Button
        {
            Text = "Close",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(8),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0x28, 0x28, 0x3C),
            ForeColor = ColorBody,
            Padding = new Padding(14, 4, 14, 4),
            UseVisualStyleBackColor = false,
        };
        close.FlatAppearance.BorderColor = Color.FromArgb(0x4A, 0x4A, 0x6E);
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x36, 0x36, 0x50);
        close.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            BackColor = ColorBackground,
            Padding = new Padding(8, 4, 8, 8),
        };
        buttons.Controls.Add(close);

        var padding = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 12, 8), BackColor = ColorBackground };
        padding.Controls.Add(_text);

        Controls.Add(padding);
        Controls.Add(buttons);

        // Escape closes, and the button holds focus so the read-only text doesn't sit there
        // blinking a caret at content nobody can edit.
        AcceptButton = close;
        CancelButton = close;

        BuildDocument();
        Shown += (_, _) => close.Focus();
    }

    private void BuildDocument()
    {
        _text.Clear();

        Write("MagSID Editor\n", TitleFont, ColorTitle);
        Write("Version 1.0  ·  Magnar Harestad\n\n", SubtitleFont, ColorSubtitle);

        Body("A tracker for the MAGSIDv7 player on the Commodore 64. It edits " +
             "SongData/Patterns.s directly and exports finished .sid files.\n\n");

        Heading("The menu bar");
        Mono("  Menu              About, New, Open, Save, Save As,\n" +
             "                    Export to .sid, Play Last Export in VSID, Exit\n" +
             "  Signal Path       Frame-by-frame trace of one instrument — see below\n" +
             "  Optimize          Shrink the song without changing a note — see below\n" +
             "  Digi to SID-osc   Turn a .wav into ring-modulated instruments — see below\n\n");
        Body("Some of those have keys of their own, which work anywhere in the window:\n\n");
        Mono("  F10      Open a song\n" +
             "  F11      Save As\n" +
             "  F9       Export to .sid\n" +
             "  Ctrl+Z   Undo\n" +
             "  Ctrl+Y   Redo\n\n");
        Body("Undo covers everything that changes the song — notes, loop markers, instruments, " +
             "the song order, the speed — and goes back 200 steps. It stops playback first, " +
             "since the whole song is swapped back to how it was.\n\n");

        Heading("Seeing which program steps are used");
        Body("While a song plays, the Wave and Filter columns each show a bright cell walking down "
             + "them - where that program is right now - over a dimmer trail of every step already "
             + "played. The two run at their own pace and stop at their own last step, so each is "
             + "tinted in its own column. The bright one is the playhead; the trail is what "
             + "matters afterwards, because "
             + "after a pass through the song the steps still dark are ones nothing ever played. "
             + "Those are bytes in the "
             + "exported .sid doing nothing, and the program can be shortened by that much. It "
             + "clears when playback stops.\n\n");

        Heading("Optimize");
        Body("Removes dead LS markers, turns runs of identical rows into loops where that really " +
             "costs fewer bytes, and shortens any instrument program whose tail nothing ever " +
             "reaches. It reports what it will do before changing anything, and Ctrl+Z undoes it.\n\n" +
             "To find those unused tails it plays the song through itself — silently, with no SID " +
             "and no audio device, in a couple of milliseconds — carrying on until every channel " +
             "has been all the way round its own order list. So it sees every row of every " +
             "pattern, and you do not have to have listened to the song first.\n\n" +
             "Trimming a tail is safe because a program that runs out simply holds its last step: " +
             "the player leaves the waveform register alone once a WAVE program ends, and the " +
             "filter keeps its final cutoff. A looping note program is never trimmed — it cycles, " +
             "so an unreached step means the loop never came round.\n\n");

        Heading("How a song is built");
        Body("A song is made of instruments, patterns, and one song order per channel.\n\n" +
             "The three SID channels each have their own independent song order. They do not " +
             "have to be the same length — channel 1 can play a 16-row pattern while channel 3 " +
             "loops a 2-row one. Patterns live in a shared pool: a pattern played by two " +
             "channels is the same data, stored once.\n\n");

        Heading("Instruments");
        Body("The Instruments panel always shows all 16 slots, not just however many exist - a " +
             "song can never hold more (a pattern row names its instrument with a single byte, " +
             "room for exactly sixteen), so an unused slot just sits there dimmed, with its own " +
             "# left blank, rather than the list resizing as instruments come and go - the " +
             "numbers actually printed are a direct count of how many are in use. Right-click a " +
             "row for Add, Copy or Delete; Add's numbers are assigned automatically, never typed, " +
             "and Copy clones the selected instrument (every field and program) onto a fresh " +
             "number, landing you on the new copy ready to rename or tweak.\n\n" +
             "Clicking a row loads it directly into the editor beneath the list - there is no " +
             "separate dialog at all any more. Everything there applies to the instrument the " +
             "moment it commits (leaving a field, finishing a grid cell, ticking a checkbox) " +
             "rather than waiting for a Save.\n\n");
        Body("Attack/Decay, Sustain/Release, Pulse width, its per-frame Sweep, continuous and " +
             "No HR all sit on one row (name is edited in the Instruments list itself, not " +
             "repeated here) - Waveform isn't a field of its own any more, it's just row 0 of " +
             "the program grid below (see next).\n\n");
        Body("The program grid is one frame per row, wavetable and filter together: Wave, Note " +
             "and Hex are that frame's wavetable step, Filter, Mode+Vol, Res+Voice, Cutoff and " +
             "Sweep are that frame's filter step. Both halves can be blank independently - a " +
             "blank Wave ends the wavetable program right there, the way a drum or a bass " +
             "transient does after its own fixed frame or two; a blank Filter just means no " +
             "filter action that frame, not an end. \"After the last note\" decides what " +
             "happens once the wavetable/note program runs out: Loop cycles it for as long as " +
             "the note is held, Hold stays on the last note, Root settles onto the row's own " +
             "note - which is how a drum's fixed attack ends on a bass note that follows the " +
             "pattern. A filter step is one-shot: Sweep steps the cutoff, Setup configures " +
             "mode, resonance and a starting cutoff, with its own sweep rate that keeps running " +
             "after the program ends. The SID has one filter, shared by all three voices - " +
             "whichever instrument last triggered with a filter program owns it.\n\n" +
             "Right-click a row for Add frame/Remove/Up/Down - they move a whole row, wave and " +
             "filter data together, so there is no way to reorder just one program's steps on " +
             "its own.\n\n");
        Body("Show High Hex-Values (Menu) redraws the grid's Hex and Mode+Vol columns with bit 7 " +
             "forced on for display, matching GoatTracker's own convention - a fixed wavetable " +
             "note shown as $80+index there, filter mode habitually written 9x/bx/dx/fx. Both " +
             "columns accept that high form as input too: $80+index is the same note as the " +
             "plain index, and a filter mode typed with bit 7 set is the same mode and volume " +
             "as without it. This player has no use for the SID's own bit 7 in that register " +
             "(3OFF, disconnecting voice 3) - typing the GoatTracker-habit value is exactly as " +
             "correct as typing the low one, and the high form is what the setting shows either " +
             "way. A cell settles on the low form the moment you type it, whichever form that " +
             "was, so the display always has something to show rather than sometimes doing " +
             "nothing.\n\n");

        Body("Two settings change how a retriggered note behaves rather than its sound:\n\n");
        Mono("  Cont. (continuous)       A new note leaves the pulse sweep running from wherever\n" +
             "                          it already is, instead of restarting it at this\n" +
             "                          instrument's own width — a shimmer that carries across\n" +
             "                          notes rather than resetting on every attack.\n\n" +
             "  No HR                    Every new note briefly resets the envelope for a clean,\n" +
             "                          punchy retrigger. One kind of instrument — an unusually\n" +
             "                          slow decay — can have that reset misfire on real SID\n" +
             "                          hardware and freeze the next note silent instead.\n" +
             "                          Checking this skips the reset entirely — no envelope\n" +
             "                          restart, no gate pulse, nothing but the frequency change\n" +
             "                          — so the note is a genuine legato, picking up on the new\n" +
             "                          pitch wherever the envelope already was.\n\n");
        Body("Vibrator switches vibrato on for the instrument, and is the switch for the player " +
             "too: a song where no instrument has it is assembled without any vibrato code or " +
             "tables at all. D/S is one hex byte - delay in the top digit (in units of four " +
             "frames, counted from the note's start) and speed in the low digit (frames per " +
             "direction, minus one). Depth is a second byte, $01-$ff: how far the SID frequency " +
             "moves each frame, so the pitch traces a triangle around the note. It is saved as " +
             "VIB $ds $depth on the instrument's line.\n\n");

        Heading("The view");
        Body("Each channel has four fields:\n\n");
        Mono("  CH    Song order — \"01→\" is the position in the list,\n" +
             "        \"RST01\" marks where the list loops back to\n" +
             "  PAT   Which pattern plays, plus loop markers LS and LE04\n" +
             "  NOT   Note, \"...\" to hold, \"---\" for note off\n" +
             "  INS   Instrument number\n\n");
        Body("Row numbers on the left run continuously through the whole chain, not per pattern.\n\n" +
             "The row you are editing stays on the middle line and the music scrolls past it, " +
             "which is the same line playback puts the sounding row on — so your eye stays in " +
             "one place whether the song is playing or being edited. Near the start of a song " +
             "that leaves the rows above row 0 blank rather than sliding the cursor up.\n\n");

        Heading("Moving around");
        Mono("  Arrow keys         Move between rows and fields\n" +
             "  Tab / Shift+Tab    Switch channel\n" +
             "  Home / End         First / last row of this channel's chain\n" +
             "  '  /  Shift+'      Octave down / up\n\n");
        Body("The apostrophe follows your Windows keyboard layout.\n\n");

        Heading("Swap Spacebar Behavior");
        Body("A menu switch, directly under Show High Hex-Values. Unchecked - the default - " +
             "Space toggles Edit/Jam and the transport stays on F1-F4. Checked, Space plays and " +
             "stops the song instead - playing from the top, exactly as F1 and the toolbar's Play " +
             "button do - and Edit/Jam moves to F5.\n\n" +
             "Which one feels right depends on which tracker you came from, so neither is " +
             "treated as the correct answer. The setting is remembered between sessions, and the " +
             "toolbar indicator always names the key that is currently live, so a Space pressed " +
             "out of habit never does something unannounced.\n\n" +
             "Both keys work from anywhere in the window, the DAW view included. Space is left " +
             "alone while a field that needs it has focus - the speed spinner, a text box, a " +
             "button - where a space is a space.\n\n");

        Heading("Entering notes");
        Mono("  Space   Toggle Edit / Jam   (F5 instead, when Space has been swapped)\n" +
             "  +  /  -   Step the selected instrument\n\n");
        Body("Edit writes a typed note into the pattern at the cursor. Jam plays it live and " +
             "leaves the pattern alone - for browsing or auditioning without any risk of " +
             "overwriting what's there. The same switch governs the DAW view: in Jam, notes " +
             "played from the MIDI keyboard sound but are not written into the piano roll.\n\n" +
             "Whichever key it sits on, the toggle doubles as a panic button: switching modes " +
             "silences every voice instantly, rather than letting whatever was jammed or " +
             "auditioned ring out its own release.\n\n" +
             "The toolbar indicator names the key that is live - '* EDIT . Space' or " +
             "'o JAM . F5' - and clicking it flips the mode as well.\n\n" +
             "+ and - move the instrument that a fresh note picks up - the same thing as " +
             "clicking a different row in the Instruments panel, without leaving the pattern to " +
             "do it. Clicking a row there does the reverse: focus jumps back to the pattern so " +
             "typing continues right away.\n\n");
        Body("Piano keys across two octaves:\n\n");
        Mono("  Q 2 W 3 E R 5 T 6 Y 7 U     octave +1\n" +
             "  Z S X D C V G B H N J M     selected octave\n" +
             "  C C#D D#E F F#G G#A A#B\n\n");
        Body("Return or CapsLock enters a note off (\"---\"). In the INS field, instrument " +
             "numbers are typed in hex; two keys pressed in quick succession combine into a " +
             "two-digit value.\n\n" +
             "In Jam mode a piano key sustains for as long as it's held, running the " +
             "instrument's full wavetable, arpeggio and filter program the same way a real note " +
             "in the song would - not just its first step. Up to three keys can be held at " +
             "once, one per SID voice; a fourth steals whichever held note has been down " +
             "longest, the same limit chording on the real chip runs into.\n\n" +
             "Edit mode auditions the same way, released the same way on key-up - but its own " +
             "release is capped at $A regardless of the instrument's own value, since Edit types " +
             "and retriggers rapidly and a genuinely long release (up to $F, tens of seconds) " +
             "would still be ringing out one keystroke's tail when the next one started.\n\n");

        Heading("Patterns and loops");
        Mono("  Insert             Insert a hold row\n" +
             "  Delete             Remove the row\n" +
             "  Backspace          Erase the note, or the loop marker in the Marker column\n" +
             "  Shift+End          Cut the pattern off at the cursor\n" +
             "  Ctrl+L / Ctrl+E    Set loop start / loop end\n" +
             "  Shift+C / Shift+V  Copy / paste the whole pattern\n\n");
        Body("Ctrl+E toggles: pressing it on a row that already has an LE takes it off, and " +
             "pressing it again puts it back with the same repeat count, so an LE04 does not " +
             "quietly come back as an endless LE.\n\n");
        Body("LS marks where a loop begins and LE where it ends. A number after LE is how many " +
             "times the section plays in total — LE04 means four times, then the song moves on. " +
             "Without a number the section loops forever.\n\n" +
             "With the cursor on the PAT field, digits set the pattern number; on a row carrying " +
             "an LE they set the loop count instead. The field always edits whatever the cell is " +
             "showing.\n\n");

        Heading("Song order");
        Body("With the cursor in the CH field:\n\n");
        // Spelled Up/Down rather than as arrow glyphs: Consolas has no ↑/↓, so they fall back to
        // another font at a different width and knock the whole column out of line.
        Mono("  Digits               Change the pattern at this position,\n" +
             "                       or the loop target on the RST row\n" +
             "  Insert               Add a position (appends on the RST row)\n" +
             "  Delete               Remove the position\n" +
             "  Ctrl+Up / Ctrl+Down  Move the position\n\n");
        Body("Clicking in the CH field jumps to that position.\n\n" +
             "New patterns are created by typing the number. Type one past the last pattern and " +
             "it is created with four empty rows. There is no separate add button.\n\n" +
             "Removing a position does not remove the pattern. A song order holds references " +
             "only — the pattern data stays, and typing the number again brings it back.\n\n");

        Heading("Playback");
        Body("The song plays through a C# implementation of the SID chip. The view follows " +
             "along: each channel scrolls at its own pace, but whatever is sounding right now " +
             "always sits on the same horizontal line.\n\n");
        Mono("  F1   Play from the beginning of the song\n" +
             "  F2   Play from the current position\n" +
             "  F3   Loop just the pattern under the cursor, on all three channels\n" +
             "  F4   Stop\n" +
             "  F5   Toggle Edit / Jam   (only needed when Space has been swapped)\n" +
             "  ▶ Play (the toolbar button)   Same as F1\n\n");
        Body("These work from anywhere in the window, the DAW view included.\n\n" +
             "F2 carries on from exactly where playback last stopped — each channel picking up " +
             "its own pattern, its own row, and its place in any loop it was inside. That detail " +
             "matters: the three channels have their own song orders and their patterns are " +
             "different lengths, so they are never level with each other once a song has been " +
             "running. Resuming them from one shared point puts the parts out of time. Move the " +
             "cursor first and F2 plays from there instead, starting every channel at that song " +
             "position. F1 always goes back to the top.\n\n" +
             "F3 is for iterating on a sound without playing up to it first: it loops what's " +
             "currently on screen — each channel's own pattern at the cursor's row, from that " +
             "pattern's own start — through its own small player, separate from the song's " +
             "position and the view's own scrolling.\n\n");

        Heading("Export");
        Body("Export to .sid builds the song with the real assembler and wraps the result as a " +
             "PSID file — the same player code that runs on real hardware, not an approximation.\n\n" +
             "Patterns no channel plays any more are pruned automatically on export. They stay in " +
             "your song, but they do not follow it into the C64 binary, where they would only " +
             "cost memory.\n\n" +
             "Play Last Export in VSID plays the most recently exported file in VICE.\n\n");

        Heading("Signal Path");
        Body("A frame-by-frame trace of what one instrument actually does to the chip: the " +
             "register writes, the gate, the envelope, the oscillator, and the mixed output. " +
             "The numbers are read out of the same emulation that plays the song — not " +
             "re-derived from the instrument's settings — so it shows what really happens, " +
             "including the parts that surprise you.\n\n" +
             "The instrument is traced in a small one-note song of its own, with the other two " +
             "channels silent. That keeps a trace reproducible and stops another channel " +
             "grabbing the shared filter mid-trace.\n\n" +
             "Trace also plays what it draws, on the note picked beside it, with a marker " +
             "running along the trace so you can hear and see the same frame at once — a drum " +
             "moving from noise into pulse, an arpeggio, a filter sweep, all visible frame by " +
             "frame rather than just heard. Jam mode plays the same full program by ear while " +
             "you hold a key; this window is for watching it happen, for a fixed span, with the " +
             "other two channels silenced so nothing else can grab the shared filter mid-trace.\n\n");

        Heading("Digi to SID-osc");
        Body("Turns a .wav into instruments that approximate it with ring modulation — the " +
             "long-established trick behind spoken-word intros on the C64. It is not sample " +
             "playback: the SID cannot do that, and neither can this player.\n\n" +
             "Load a .wav and it is analysed at the player's own 50Hz frame rate for pitch, " +
             "voicing and the dominant resonance (formant) of each frame. The clip is then cut " +
             "into short segments, and for each one the tool renders dozens of candidate " +
             "instruments through the real SID emulation and keeps whichever actually measures " +
             "closest. Only the candidates that win a segment end up in the finished library, " +
             "which is why it stays a handful of instruments rather than one per segment.\n\n");
        Mono("  Voiced threshold %   How sure the pitch detection must be to call a frame\n" +
             "                       voiced rather than noise\n" +
             "  Cutoff min / max     The filter range the formant sweep is mapped into\n" +
             "  Segment ms           How often the carrier retriggers — short enough and it\n" +
             "                       buzzes, long enough and syllables blur together\n" +
             "  Carrier waveform     Auto searches every waveform; the rest pin it to one\n\n");
        Body("The graph shows the source waveform above the rendered reSID output, so the two " +
             "can be compared before you listen. Audition plays the result and Add to Song " +
             "appends the instruments and patterns to the open song.\n\n" +
             "Expect to iterate. This is a perceptual problem, not a conversion, and the " +
             "settings that work depend on the recording.\n\n");

        Heading("Credits");
        Body("The MAGSIDv7 player was written by Magnar Harestad, with help from " +
             "Robert Lundquist (Krikkit) and Fredrik Ternell (Swallow) of Censor Design.\n");

        _text.SelectionStart = 0;
        _text.ScrollToCaret();
    }

    private void Heading(string text) => Write(text + "\n", HeadingFont, ColorHeading);

    private void Body(string text) => Write(text, BodyFont, ColorBody);

    private void Mono(string text) => Write(text, MonoFont, ColorMono);

    /// <summary>Appends a run and then formats exactly that range. Setting SelectionFont
    /// *before* AppendText looks like it should work but doesn't reliably: AppendText applies
    /// the formatting already in force at the insertion point, so runs silently inherit the
    /// previous one's font - which is how the key tables ended up rendering half in Consolas
    /// and half in the proportional body font.</summary>
    private void Write(string text, Font font, Color color)
    {
        int start = _text.TextLength;
        _text.AppendText(text);
        _text.Select(start, text.Length);
        _text.SelectionFont = font;
        _text.SelectionColor = color;
        _text.Select(_text.TextLength, 0);
    }
}
