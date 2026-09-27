# MagSID

A SID music player for the Commodore 64, and a Windows tracker for writing music with it.

By Magnar Harestad, with Robert Lundquist (Krikkit) and Fredrik Ternell (Swallow) of Censor Design.

## The two halves

**`MAGSIDv7_LowMemory`** — the player itself, written in c6510 assembly. A low-memory-footprint
rewrite of a GoatTracker-style player: the whole thing is around 460 bytes of code, and a finished
song assembles to roughly 700–1100 bytes including the player. Songs live in `SongData/*.s` as
plain text, and each `export_<song>.s` driver assembles one of them into a standalone `.sid`.

**`MagSID_Editor`** — a C# tracker that edits those song files directly and exports `.sid`. It
reproduces the assembly player exactly, note for note, so a song sounds in the editor as it will on
the C64. That is not a coincidence to be maintained by hand: `MagSid.Playback.SongSequencer` is a
deliberate port of `Code/musicplayer.s`, and `MagSid.Core.SongSizeCalculator` mirrors the Lua
emitter in `Startup/Tables.s` closely enough that its byte counts are pinned against real assembler
output in `SongSizeTests`.

## Layout

The folder structure looks odd on purpose. The editor locates the assembler and the include library
by walking up three levels from the player (`../../../bin/win_x86/c6510.exe` and
`../../../include/`), so the repository mirrors the working tree it came from rather than
rearranging it and rewriting those paths:

```
bin/win_x86/c6510.exe          the c6510 assembler
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

The player assembles with the c6510 binary included here. From
`z-archive/Magnar/MAGSIDv7_LowMemory`:

```
..\..\..\bin\win_x86\c6510.exe -p ..\..\..\include\ -s out.prg $1000 + export_bellaciao.s -y out.sym
```

`make.bat` does the same for the full demo build and then launches VICE, which is not included
here — install it separately if you want that path.

## Not included

- **VICE** (`x64`), for running the assembled demo.
- **`u64-windows-amd64.exe`**, the Ultimate64 upload tool used by `u64file.bat` / `u64image.bat`.
  It is a third-party binary an order of magnitude larger than the rest of this repository put
  together, and nothing needs it in order to build.

Both are only needed to *run* what this builds, not to build it.
