# MagSID

A Windows tracker for writing Commodore 64 music, and the SID player that plays it on real hardware.

By Magnar Harestad. Thanks to Robert Lundquist (Krikkit) and Fredrik Ternell (Swallow) of Censor Design
for technical advice and tools.

**Website and download: [magnarharestad.github.io/MagSID](https://magnarharestad.github.io/MagSID/)**

## What this is

A tracker that edits songs as plain text and exports them as `.sid` files, reproducing the C64 player
exactly — note for note, byte for byte. A song sounds in the editor as it will on the machine, and the
exported file is the same image the assembly player would have produced.

The player is a low-memory-footprint rewrite of a GoatTracker-style player, written in c6510 assembly:
462 bytes of code with neither optional effect, 574 with vibrato, 533 with slides and 608 with both,
plus 10 bytes when an instrument uses soft hard restart. Code for an effect is only assembled when a
song uses it. A finished song lands at roughly 750–1250 bytes including the player.

## Using the editor

### Requirements

- Windows 10 or 11, 64-bit
- The [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) for x64 — Windows
  offers to fetch it the first time the editor starts if it is missing

Nothing else: no assembler, no emulator. Export to `.sid` and playing a `.sid` back are built in.

### Installing

`MagSID-Editor.exe` is the whole program — put it in any folder and run it. There is no installer.

The exe is not code-signed, so Windows SmartScreen may say it comes from an unknown publisher. Choose
**More info → Run anyway**.

Settings — MIDI device, knob bindings, view preferences — are kept in
`%APPDATA%\MagSid.Editor\settings.json`.

### Getting started

- Songs are plain-text `.s` files. Open one from `SongData` with **Menu → Open** (F10).
- There are two views of the same song: **Retro**, a classic tracker grid, and **DAW**, an
  arrangement with a piano roll and an instrument page. The menu bar switches between them.
- F1 plays from the top, F2 from the cursor (in the DAW, from the project cursor), F4 stops. Space
  or F5 switches between Edit and Jam mode — **Menu → Swap Spacebar Behavior** gives Space the
  transport instead.
- A MIDI keyboard is chosen under **MIDI**. The computer keyboard plays notes too, the classic
  tracker way: `ZSXDCVGBHNJM` is one octave and `Q2W3ER5T6Y7U` the next.
- **Menu → Export to .sid** (F9) writes a PSID file. **Menu → Load and Play .sid** plays one, and
  exports it as WAV, MP3 or FLAC.
- **Menu → Import MIDI** makes a new song from a `.mid` file: bass and drums on channel 1, the
  chords as arpeggio instruments on channel 2, the lead on channel 3, and each part's General MIDI
  sound mapped to an instrument from the demo songs. `midifiles/` has 23 free tunes to try it on.
- The **sound bank** holds 25 sounds from the demo songs - instruments, drums and arpeggios at
  three speeds. In the DAW, **SOUNDBANK** on the instrument page shows them as cards: click one to
  hear it, double-click or **+** to add it. In Retro it is under the instrument list's **▾** menu.

**Menu → About** lists every key and control, view by view.

## Layout

```
MagSid.sln
src/
  MagSid.Core/            the song model, file format, sizing, undo, optimiser
  MagSid.Sid/             a reSID port - the chip itself
  MagSid.Playback/        voices, sequencer, jam mode, filter, the .sid player
  MagSid.Assembler/       the .sid export: a 6502 emitter and the player expressed against it
  MagSid.Editor.App/      the WinForms editor
tests/
SongData/                 the songs, as plain text
Digi/                     a sample for the Digi-to-SID-osc feature
midifiles/                23 public-domain and Creative Commons tunes for Import MIDI
```

## The three ports, and what keeps them honest

Three pieces of C# each mirror something in the assembly player, and all three would drift silently
if nothing checked them. So each is pinned to real assembler output rather than reviewed:

- **`MagSid.Playback.SongSequencer`** mirrors `Code/musicplayer.s` — this is what makes in-editor
  playback match the machine.
- **`MagSid.Core.SongSizeCalculator`** mirrors the Lua emitter in `Startup/Tables.s`. Its byte counts
  are pinned against real c6510 builds in `SongSizeTests`, which has caught song edits three times
  and a renamed file once.
- **`MagSid.Assembler`** produces the exported image: a byte emitter with labels, a line-for-line
  port of the player, and five emitters mirroring the tables `Tables.s` generates. 34 mnemonics in 7
  addressing modes is all the player uses, which is small enough to emit directly.

**Playing the exported file back** is a separate answer to a separate question. `MagSid.Playback`
carries a 6502 interpreter and a PSID loader, so the editor plays a `.sid` by *executing* it —
`SidFilePlaybackTests` then checks that those bytes, run on the CPU, write the same SID registers as
`SongSequencer` over 1500 frames of every song. Goldens prove the export is the right bytes; this
proves those bytes make the right music. Neither alone would.

`SongImageTests` compares the **complete image** — player and song data — against what c6510
produced, for seven songs and for BellaCiao at `$2000` and `$4400`. Two fixture songs come with it
because those seven reach only two of the four feature combinations: six are bare and BellaCiao has
vibrato. Songs written since — Clarity, Outlaws and Fairlighty, which use vibrato, slides and soft
hard restart — are checked by running their image on the 6502 against `SongSequencer`, frame by
frame.

Comparing whole images and not just their lengths is the point. Reversing the note dictionary's build
order — arpeggios before pattern rows — yields an image of exactly the right length with every note
wrong, and the length cross-checks stay green through it. Only the byte comparison catches that, and
it is the failure mode that would ship.

Those reference images are frozen in `tests/MagSid.Assembler.Tests/Goldens/`, captured while the
assembler was still in this repository. See the README beside them before changing one.

## Building

Needs the .NET 8 SDK. Nothing else — no assembler, no include library, no player sources.

```
dotnet build MagSid.sln
dotnet test MagSid.sln
```

To hand the editor to someone, one file is enough; they need the .NET 8 Desktop Runtime and nothing
more:

```
dotnet publish src/MagSid.Editor.App/MagSid.Editor.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## Optional

**The player sources**, if you want to assemble the C64 side or regenerate a golden. The c6510 source
is in the Censor SVN tree under `MAGSIDv7_LowMemory` as it stood at commit `6221867`, together with
the c6510 assembler, its include library, and the Python tools that measure byte cost and trace a
player's register writes. Every change made to the player since — pattern slides, soft hard restart,
and a note off that lets the release run on — is in `tests/MagSid.Assembler.Tests/Goldens/player.patch`,
against that version.

## License

Exported `.sid` files - the player code and the song data in them - may be used freely, in any
release, commercial or not.

MagSID Editor is free software: you can redistribute it and/or modify it under the terms of the GNU
General Public License as published by the Free Software Foundation, either version 2 of the License,
or (at your option) any later version. It is distributed in the hope that it will be useful, but
WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See [LICENSE](LICENSE) for the full text.

The editor's SID emulation is a C# translation of reSID by Dag Lem, which is licensed under the GPL,
version 2 or later - so the editor is too. It also includes NAudio, under the MIT license. Both are
credited in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The MIDI files in `midifiles/` are not part of the program and keep their own licences - public
domain or Creative Commons - listed with credits in [midifiles/README.md](midifiles/README.md).
