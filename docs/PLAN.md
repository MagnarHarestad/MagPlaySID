# MagPlaySID - plan

A small app that plays C64 `.sid` tunes with the MagSID player's visualisation - spectrum, a
scope per voice - and shows each tune as a tracker would: its notes, instruments and effects in
rows that scroll with the music. Read-only: a way to see how a tune is built, not to edit it.

Started 2026-10-26 from MagSID-Xplatform (Avalonia), so it runs on Windows, macOS and Linux. The
MagSID editor projects stay in the tree; the app is `src/MagPlaySID`.

## How it fits together

- **The sound is the original.** The tune's own 6502 code runs on the emulated C64 and SID, as
  MagSID's Load and Play .sid does. Nothing the view does changes what is heard.
- **The view is a transcription.** The register writes the tune makes are turned into a score;
  the score plays silently beside the tune, and its row events scroll the grid. Row 0 is the
  frame the first note starts on.
- **"Complete" is measured, not claimed.** A score is complete when playing it gives the same
  register writes as the tune, at the same cycles. Every step below reports that agreement on
  the benchmark, so "supports every effect" means a number going up, tune by tune.
- **Any player, any machine.** The goal is an ultimate player: whatever player made a tune -
  GoatTracker, Hubbard's, Laxity's, DMC, a demo coder's own - its tune can be written as a
  score. PAL and NTSC, a call a frame, multispeed, CIA timing, funk tempo and digis alike.

## Steps

### 0. The app, with today's transcription

The player window with a read-only Retro view underneath, filled by MagSID's Import SID. It shows
what MagSID's format can say - notes, instruments with wave/note/filter programs, pulse,
vibrato, slides - and loses what it cannot: volume changes, tempo changes, effects per row.

### 1. A score format with every effect, and a C# player for it - done

`src/MagPlay.Score`, tested effect by effect in `tests/MagPlay.Score.Tests`. What it settled:

- **Time is CPU cycles.** A score names its machine (PAL 985,248 Hz, NTSC 1,022,727 Hz) and the
  cycles between player calls: a frame, a frame divided for multispeed, a CIA timer's value -
  which a Call rate effect changes mid-song. Rows, tables and effects count in calls.
- **Out comes a timeline**: every write with the cycle it happens on. That is what is compared
  with the original, so digis - written thousands of times a second - are placed where they fall.
- **Digis** are samples streamed to a register at their own rate, started by an effect;
  $D418's volume nibble by default, any register for the pulse and test-bit techniques.
- **Raw steps** write registers outright, in order, within a call. Tested: fifty calls of random
  writes come out exactly as written - so any tune can be expressed, before a single musical
  effect is recognised.

What the format holds:

Not MagSID's song format, which is shaped by a C64 memory budget. A superset, built like a
tracker's and free of memory limits, played by a C# player frame by frame:

- **Song:** an order list per voice, with transposition and repeats; patterns of rows; tempo
  per row, funk tempo, per-voice tempo; multispeed (2x-16x calls a frame).
- **Row:** note (or hold, or gate off / gate on), instrument, and an effect column - several per
  row if a tune needs them.
- **Effects** - the union of what C64 trackers and players do:
  portamento up/down, tone portamento (to note), vibrato (speed, depth, delay), arpeggio,
  set AD / SR, set waveform / control byte, set wave/pulse/filter table position, filter
  control (resonance, routing), filter cutoff, filter mode, master volume, tempo, funk tempo,
  gate timer, hard restart (none, gate-only, ADSR, test bit), legato / tie, note delay,
  note cut, detune.
- **Instruments:** AD, SR, hard restart kind and timing, gate timer, and pointers into tables.
- **Tables**, a step a frame, each step with an optional delay and a jump:
  wave (control byte with relative or absolute note), pulse (set / modulate), filter (set /
  modulate / mode), speed (vibrato and portamento speeds).
- **The fallback that makes it complete:** table steps that set a register value outright -
  an exact frequency, pulse width, cutoff, control byte, AD, SR. Anything a player does can be
  written that way; the transcriber reaches for it only where nothing more musical fits.

The editors whose effects this has to cover, as references:

| Editor / player | What it adds to check against |
|---|---|
| **GoatTracker 2** | Commands 1xx-Fxx, wave/pulse/filter/speed tables, gate timer, hard restart ADSR, funk tempo, per-channel tempo (F80+), order-list transpose and repeat. Its `gplay.c` is the model to follow. |
| **CheeseCutter** | Chord tables, per-instrument pulse and filter programs, multispeed, its own command set. |
| **SID-Duzz'It** | Slides, portamento, vibrato, arpeggio, filter sweeps per instrument. |
| **Rob Hubbard's driver** | Notes with durations rather than rows, per-instrument pulse modulation, vibrato, drums (noise plus a falling pitch), "skydive", octave arpeggios, portamento in the tracks. |
| **JCH / Laxity NewPlayer, DMC, Future Composer** | The other common players; Laxity's drums and arpeggios, DMC's sound tables. |

### 2. A C64 for every tune - done

The emulator MagSID has calls a PSID's play routine once a frame and records each frame's writes.
It refuses RSID tunes and tunes without a play address, and runs no timers or interrupts - which
is how most digis and many CIA-timed tunes play. For any tune: CIA 1 and 2 timers, IRQ and NMI,
the VIC's raster interrupt, enough of the KERNAL for RSID tunes, and every write recorded with
its cycle. Without it those tunes can be played as a score but not checked against the original.

`src/MagPlay.C64`: `C64`, `Cia`, `Vic`, `Kernal` (a stand-in ROM at the real entry points),
`SidTune` (loads a `.sid`, starts it as a SID player would, runs it). The 6510 is MagSID's own
`Mos6502` given a memory bus: with one, it counts cycles, takes IRQ and NMI, and makes each
access on the cycle the real chip does - fetches from the first cycle, the data on the last, a
read-modify-write's two writes, the dummy reads. Interrupts are polled on an instruction's
second-last cycle, the NMI edge is latched, and CLI/SEI/PLP act one instruction late.

Checked against every `.sid` on this machine (471):

- 460 PSIDs give MagSID's recorder's writes exactly, write for write over 20 seconds. Friday
  does too, once its init has cleared four SID mirrors ($D400-$D481), which the old recorder
  never saw. Three LMan tunes are 2SID tunes; the header's second SID is now its own chip
  (`ExtraSidWrites`), and their first SID agrees.
- All 9 RSIDs run, which the old recorder refused: ThcMOD and LMan digis at ~30,000 writes a
  second. ThcMOD cancels NMI jitter by jumping through CIA 1's timer registers into one of nine
  pages, and ends its NMI with an RTI in CIA 2's serial register, whose dummy read acknowledges
  it. That only works with every access on its cycle; it does now, never leaving the nine pages.
- The driver sits in the first free page from $0400, as in libsidplayfp: two tunes clear the
  cassette buffer at $0334 in init.

Left out: the cycles the VIC steals on bad lines and for sprites, and a CIA timer's start delay
(both timers of a pair share it, so the jitter tricks above don't see it). No tune here has shown
the difference yet. 2SID and 3SID tunes are recorded per chip but not yet played as such.

### 2b. Prove the player against GoatTracker

GoatTracker `.sng` files convert to the score directly - the `.sng` reader is already in
`tools/ImportBench`. Playing the score and the `.sid` GoatTracker exported must then give the same
register writes frame by frame. That checks the player before any transcription depends on it,
on Magnar's own 115 songs.

### 3. Transcribe any `.sid` into the score - first version in

Lossless by construction: a tune's writes go into raw steps first, which reproduce it exactly;
then a musical form - a note, a wave program, a vibrato, a slide, a tempo - replaces raw steps
wherever playing it gives the same writes. What is left raw is what nothing more musical
describes, and the share of it is the measure of how much of a tune is understood. Import
SID's analysis - notes, sounds, the grid, glides, re-strikes - is where the musical forms come
from. Scored by register agreement and by the heard pitch, on the 202 tunes and the 71
GoatTracker songs, and on tunes from each player in the table above.

First milestone, `src/MagPlay.Transcribe`:

- **Calls are the tune's IRQs.** The C64 tags each SID write with what it was written from: a
  call (an IRQ, not nested in another), an NMI, or the main program. A `Recording` is the calls'
  cycles, each call's own writes, and the writes between calls - init, NMI digis.
- **The raw layer** (`Score.Raw`): writes between calls kept on their cycles, and per call the
  registers the music gets wrong, written as the tune wrote them, on their cycles. The score's
  calls fall on the tune's: evenly spaced within 64 cycles of jitter, or listed (8 tunes).
- **The transcriber plays the score as it builds it**, and in each call writes raw whatever the
  music gets wrong - so whatever the music says, the score plays the tune back.
- **Agreement** is measured by replaying, not by trusting the construction: per call and
  register, the values it takes (a write that changes nothing left out, as on the SID); between
  calls, every write on its cycle. All 471 local `.sid` files agree exactly, all raw and with music.
- **The music, v1** (`Music`): a note per gate-on, as long as it lasts; the tune's own note table,
  from the frequencies it holds; an instrument per way a note is played - envelope, wave program
  (control byte, pitch in semitones from the note), pulse program (width, sweeps of even steps),
  and the tune's hard restart taken out as gate timer and envelope; a filter program from the
  first call (sweeps of even steps, routing, mode, volume) - `Score.FilterProgram`.

Measured on the 471, 20 seconds each: the music makes 95.4% of the calls' register changes on
average (median 98.5%, one in ten under 89.8%). The rest is raw - and, counted apart, the extra
values a player writes to a register on the way within a call, such as MagSID's player writing a
note's frequency and then its vibrato. Tunes that play from an NMI or their main loop (the LMan
RSIDs, Hi Fi Sky) have no calls yet: all raw.

Instruments are shared where notes play alike, and how alike is measured too - score items
(instruments, rows, table steps, raw writes) per 100 register changes, on the tunes without
writes between calls:

| | Instruments for 74,893 notes | Items per 100 changes |
|---|---|---|
| An instrument per way a note is played | 29,237 | 35.5 |
| A pitch kept in every form that fits the notes - semitones, a note, an offset from the note (`PitchKind.Offset`), a frequency - and the pulse carried over from the note before (an instrument without a pulse program leaves the running one, as GoatTracker's pointer 0) | 27,002 | 34.0 |
| A note's first call keeping the voice as it was (`FirstControl` now holds the pitch and pulse to the next call); pulse widths set or moved, whichever fits | 15,687 | 26.4 |
| The envelope a note starts with set on its row (5xx, 6xx) when not its instrument's; a held width set by the row (`SetPulse`) | 14,329 | 25.7 |

What still makes a note's instrument its own, measured: a pitch that moves another way - a
vibrato or slide whose depth follows the note, an arpeggio of its own - a third of the cases;
another envelope within the note or its hard restart, a fifth.

**Rows on the tune's grid.** Import SID's grid fitting - the longest row length, or funk pair,
that puts nine notes in ten on a row - run on the transcriber's own note starts
(`SidTranscriber.SuggestGrid(starts)`, a small addition to the shared engine): on Import's own
note analysis, which also counts pitch changes under a held gate, 56 tunes found no grid; on the
gate-ons, 6. The phase is the one that delays the notes least - on Import's, a player that starts
some notes a call early had them delayed by a row less one, 31% of all notes delayed; now 6.7%.
A note off its row is delayed into it (ND), a row two notes start in is split into rows of their
own lengths (708 such rows on the 471), and with funk tempo every row says its length. The hard
restart now comes its gate timer before the next note wherever that starts - later in the row,
past held rows, delayed. Grids found: rows of 6 for 149 tunes, 12 for 51, 7 for 46, 8 for 40;
funk pairs for 73.

**Patterns where the music repeats.** Each voice's rows are cut into patterns of 4, 8, 16, 32
or 64 grid rows - whichever makes the fewest pattern rows and order entries - and a stretch that
plays again is its pattern again: transposed when only its notes moved, repeated in its order
entry when it comes again straight after. On the 471: 15,713 patterns hold 71,977 rows for the
233,665 played (31%), in 36,142 order entries; score items per 100 changes 25.7 to 24.1.

Next: pitch effects the player runs (vibrato, slides), loops in programs, and calls for tunes
that play outside an IRQ.

### 4. The Retro view of a score - first version in

Note, instrument and effect columns per voice, scrolling with the music; the instrument and table
of the selected voice beside them, their current step lit.

`src/MagPlaySID/ScoreView.cs`, fed by `Transcription`: the tune recorded on the C64 for its measured
length, transcribed (step 3), and the score played once silently into a `ScoreTimeline` - the
cycle of every call, each voice's order entry, pattern, row, instrument and wave and pulse step
after it, the filter program's step, and the rows in the order they play
(`ScorePlayer.PositionOf`, `FilterStep`). The view goes by the tune's cycle - the seconds heard
times the machine's clock - so it scrolls smoothly between calls, rows placed by when they play:
a row split off its grid row is as short as it is, marked where it is too short to write. Each
voice shows pattern row, note (transposed as its order entry says), instrument and effects, a
line and the pattern's number at each pattern's start; a click on a voice shows its instrument -
envelope, first control, hard restart - and its wave and pulse programs beside the rows, and the
filter program below, the step sounding lit. A three-minute tune is ready about three seconds
after it starts, most of it recording.

The app no longer uses Import SID or the editor's tracker grid, and so no longer references the
editor projects. The sound is still MagSID's PSID engine, so RSIDs are refused before the view
gets them; playing the recording itself - any tune, digis included - is the step after.

## Rules carried over from MagSID

Measure, don't guess: a change goes in when the benchmark says it helps. Stage files explicitly.
The engine projects are shared with MagSID; changes to them are kept small and named, so the two
can still be compared.
