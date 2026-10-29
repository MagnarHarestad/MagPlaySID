# MagPlaySID

Plays Commodore 64 `.sid` tunes with the MagSID player's visualisation - a spectrum and a scope per
voice - and shows each tune as a tracker would: its notes, instruments and effects in rows that
scroll with the music. Drop a `.sid` on the window, or open one, and it plays. Read-only: a way to
see how a tune is built. Windows, macOS and Linux.

By Magnar Harestad of Censor Design. Built on the engine of the
[MagSID editor](https://github.com/MagnarHarestad/MagSID).

**Website and download: [magnarharestad.github.io/MagPlaySID](https://magnarharestad.github.io/MagPlaySID/)** -
packages for Windows, macOS and Linux on the [releases](https://github.com/MagnarHarestad/MagPlaySID/releases)
page, built by `.github/workflows/magplaysid-release.yml` from a tag `v0.1` and the like (`packaging/magplaysid`).

```bash
dotnet run --project src/MagPlaySID -c Release -- tune.sid
```

Where it is going - a song format with every effect C64 players use, played in C# and checked
against the original frame by frame - is in [docs/PLAN.md](docs/PLAN.md).

## Layout

```
MagPlaySID.sln
src/
  MagPlaySID/              the app: the player window, the tracker rows, the piano
  MagPlay.C64/             a C64 for SID tunes - CIA timers, interrupts, the raster, banked memory
  MagPlay.Score/           the score format tunes are transcribed into, and its C# player
  MagPlay.Transcribe/      any .sid into a score
  MagSid.Core/             from the MagSID editor: the song model and the grid finding transcription uses
  MagSid.Sid/              from the MagSID editor: a reSID port - the chip itself
  MagSid.Playback/         from the MagSID editor: the 6502, the .sid loader and the audio output
  MagSid.Audio.PortAudio/  sound through PortAudio on Windows, macOS and Linux
packaging/magplaysid/      the release packages
docs/                      the website
```

## Building

Needs the .NET 8 SDK.

```bash
dotnet build MagPlaySID.sln
```

## License

MagPlaySID is free software: you can redistribute it and/or modify it under the terms of the GNU
General Public License as published by the Free Software Foundation, either version 2 of the License,
or (at your option) any later version. It is distributed in the hope that it will be useful, but
WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See [LICENSE](LICENSE) for the full text.

Its SID emulation is a C# translation of reSID by Dag Lem, which is licensed under the GPL, version 2
or later - so MagPlaySID is too. The third-party software it includes is credited in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
