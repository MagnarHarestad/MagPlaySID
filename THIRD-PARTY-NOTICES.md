# Third-party notices

MagSID Editor contains or is derived from the following third-party software.

## reSID

`src/MagSid.Sid` is a C# translation of reSID, the MOS6581/8580 SID emulator engine by Dag Lem.
It follows reSID's `sid`, `voice`, `wave`, `envelope`, `filter` and `extfilt` classes; it was changed
in translation - only the single-cycle `clock()` path is ported, not the bulk-clocking or windowed-sinc
resampling paths - and is used by the editor for all of its audio.

```
reSID, a MOS6581 SID emulator engine.
Copyright (C) 2004  Dag Lem <resid@nimrod.no>

This program is free software; you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation; either version 2 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
```

The GNU General Public License, version 2, is in [LICENSE](LICENSE).

## NAudio

The editor uses NAudio 2.2.1 (NAudio, NAudio.Core, NAudio.Midi, NAudio.Wasapi, NAudio.WinMM,
NAudio.Asio, NAudio.WinForms) for audio output, audio file export and MIDI input. It is included in
`MagSID-Editor.exe`.

```
Copyright 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
