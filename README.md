# MagSID

A SID music player for the Commodore 64, and a Windows tracker for writing music with it.

By Magnar Harestad, with Robert Lundquist (Krikkit) and Fredrik Ternell (Swallow) of Censor Design.

## The two halves

**`MAGSIDv7_LowMemory`** — the player itself, written in c6510 assembly. A low-memory-footprint
rewrite of a GoatTracker-style player: 462 bytes of code with neither optional effect, 604 with both
vibrato and slide, and a finished song assembles to roughly 700–1100 bytes including the player.
Songs live in `SongData/*.s` as plain text, and each `export_<song>.s` driver assembles one of them
into a standalone `.sid`.

Nothing has to assemble this in order to use the editor any more. What this half is for now is being
the reference the C# port is checked against — it is the source of truth, not a build step.

**`MagSID_Editor`** — a C# tracker that edits those song files directly and exports `.sid`. It
reproduces the assembly player exactly, note for note, so a song sounds in the editor as it will on
the C64. That is not a coincidence to be maintained by hand: `MagSid.Playback.SongSequencer` is a
deliberate port of `Code/musicplayer.s`, and `MagSid.Core.SongSizeCalculator` mirrors the Lua
emitter in `Startup/Tables.s` closely enough that its byte counts are pinned against real assembler
output in `SongSizeTests`.

**`MagSid.Assembler`** — the `.sid` export, in C#. A copy of `MagSID-Editor.exe` exports on its own:
no c6510, no include library, no player sources beside it. The project has no filesystem or process
calls at all.

Not an assembler for text. The sources could not be fed to an off-the-shelf 6502 assembler anyway —
`Startup/Tables.s` is a Lua program and the macro library is written in c6510's own dialect — so
instead this is a byte emitter with labels, a line-for-line port of the player, and five emitters
mirroring what `Tables.s` generates. The player uses 34 mnemonics in 7 addressing modes, which is
small enough to emit directly, and because the tables are laid down at known addresses the player's
absolute references into them resolve without any relocation step.

The load address is the caller's choice, `$1000` or otherwise: the labels are resolved against it
rather than patched afterwards.

### Why any of that is trustworthy

Nothing here is verified by review. Every layer is compared against the real assembler:

- `OpcodeTableTests` assembles one instruction per entry in the opcode table through c6510, each in
  its own four-byte slot so a disagreement about one length cannot cascade, and compares the bytes.
- `PlayerImageTests` compares the emitted player against `Code/musicplayer.s` for every song.
- `SongImageTests` compares the **complete image** — player and song data — against c6510 for every
  song, and for BellaCiao at `$2000` and `$4400`.

Two fixtures come with those, because the real songs only reach two of the four feature
combinations: five are bare, BellaCiao has vibrato, and nothing in `SongData` uses SLIDE at all. A
gated feature that no song enables is how a broken vibrato declaration once passed a green suite.

Comparing whole images and not just their lengths is the point. Reversing the note dictionary's build
order — arpeggios before pattern rows, which is the one ordering decision `Tables.s` bothers to
explain — produces an image of exactly the right length with every note wrong, and the length
cross-checks stay green through it. Only the byte comparison catches that, and it is the failure mode
that would ship.

These tests fail loudly when c6510 is missing rather than skipping. A silently skipped differential
test reports green while checking nothing, which is how a broken export driver got through before.

The old c6510 path is still in `SidExporter` behind `useExternalAssembler`, as the cross-check that
keeps the two honest.

## Layout

The folder structure looks odd on purpose. The tests locate the assembler and the include library by
walking up from the player, so the repository mirrors the working tree it came from rather than
rearranging it and rewriting those paths:

```
bin/win_x86/c6510.exe          the c6510 assembler — needed by the tests, not to run the editor
include/                       its shared macro library (6510.s, word.s, utils.s, ...)
z-archive/Magnar/
  globalmacros.s               included by the player as ..\Globalmacros.s
  MAGSIDv7_LowMemory/          the player, plus SongData/ and tools/
  MagSID_Editor/               the C# editor
```

## Building

The editor needs the .NET 8 SDK. From `z-archive/Magnar/MagSID_Editor`:

```
dotnet build MagSid.sln
dotnet test MagSid.sln
```

To distribute it, one file is enough — its users need the .NET 8 runtime, nothing else:

```
dotnet publish src/MagSid.Editor.App/MagSid.Editor.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The player still assembles on its own, which is what the differential tests do. From
`z-archive/Magnar/MAGSIDv7_LowMemory`:

```
..\..\..\bin\win_x86\c6510.exe -p ..\..\..\include\ -s out.prg $1000 + export_bellaciao.s -y out.sym
```

## Not included

**VICE.** `vsid.exe` backs the editor's "Play in VSID", and `x64` runs an assembled `.prg`. Install
VICE separately if you want either; everything else works without it.

The demo this player was written for is not here either — this repository is the player, the songs
and the editor. Its parts (`source.s`, the `Startup/` IRQ and loader code, `Code/musicshower.s`) stay
in the Censor SVN tree.
