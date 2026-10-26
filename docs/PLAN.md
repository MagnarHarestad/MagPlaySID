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
  register writes as the tune, frame by frame. Every step below reports that agreement on the
  benchmark, so "supports every effect" means a number going up, tune by tune.

## Steps

### 0. The app, with today's transcription

The player window with a read-only Retro view underneath, filled by MagSID's Import SID. It shows
what MagSID's format can say - notes, instruments with wave/note/filter programs, pulse,
vibrato, slides - and loses what it cannot: volume changes, tempo changes, effects per row.

### 1. A score format with every effect, and a C# player for it

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

### 2. Prove the player against GoatTracker

GoatTracker `.sng` files convert to the score directly - the `.sng` reader is already in
`tools/ImportBench`. Playing the score and the `.sid` GoatTracker exported must then give the same
register writes frame by frame. That checks the player before any transcription depends on it,
on Magnar's own 115 songs.

### 3. Transcribe any `.sid` into the score

Import SID's analysis - notes, sounds, the grid, glides, re-strikes - writing the new format:
effects where the tune uses effects, tables where it uses tables, raw steps where nothing else
fits. Scored by register agreement and by the heard pitch, on the 202 tunes and the 71
GoatTracker songs, and on tunes from each player in the table above.

### 4. The Retro view of a score

Note, instrument and effect columns per voice, scrolling with the music; the instrument and table
of the selected voice beside them, their current step lit.

## Rules carried over from MagSID

Measure, don't guess: a change goes in when the benchmark says it helps. Stage files explicitly.
The engine projects are shared with MagSID; changes to them are kept small and named, so the two
can still be compared.
