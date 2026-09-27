# MagSID

A Windows tracker for writing Commodore 64 music, and the SID player that plays it on real hardware.

By Magnar Harestad, with Robert Lundquist (Krikkit) and Fredrik Ternell (Swallow) of Censor Design.

## What this is

A tracker that edits songs as plain text and exports them as `.sid` files, reproducing the C64 player
exactly — note for note, byte for byte. A song sounds in the editor as it will on the machine, and the
exported file is the same image the assembly player would have produced.

The player itself is written in c6510 assembly and lives in the Censor SVN tree, not here. It is a
low-memory-footprint rewrite of a GoatTracker-style player: 462 bytes of code with neither optional
effect, 604 with both vibrato and slide, and a finished song lands at roughly 700–1100 bytes
including the player.

## Layout

```
MagSid.sln
src/
  MagSid.Core/            the song model, file format, sizing, undo, optimiser
  MagSid.Sid/             a reSID port - the chip itself
  MagSid.Playback/        voices, sequencer, jam mode, filter
  MagSid.Assembler/       the .sid export: a 6502 emitter and the player expressed against it
  MagSid.Editor.App/      the WinForms tracker
tests/
SongData/                 the songs, as plain text plus their exported .sid
Digi/                     a sample for the Digi-to-SID-osc feature
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

`SongImageTests` compares the **complete image** — player and song data — against what c6510
produced, for every song and for BellaCiao at `$2000` and `$4400`. Two fixture songs come with it
because the real songs reach only two of the four feature combinations: five are bare, BellaCiao has
vibrato, and nothing in `SongData` uses SLIDE at all.

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

To hand the editor to someone, one file is enough; they need the .NET 8 runtime and nothing more:

```
dotnet publish src/MagSid.Editor.App/MagSid.Editor.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## Optional

**VICE**, for the editor's "Play in VSID" button. Install it anywhere usual and the editor will find
`vsid.exe`; set `VICE_HOME` if it does not. Everything else works without it.

**The player sources**, if you want to assemble the C64 side or regenerate a golden. They are in the
Censor SVN tree under `MAGSIDv7_LowMemory`, together with the c6510 assembler, its include library,
and the Python tools that measure byte cost and trace a player's register writes.
