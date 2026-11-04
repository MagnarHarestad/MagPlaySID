# Third-party notices

MagPlaySID contains or is derived from the following third-party software.

## reSID

`src/MagSid.Sid` is a C# translation of reSID, the MOS6581/8580 SID emulator engine by Dag Lem.
It follows reSID's `sid`, `voice`, `wave`, `envelope`, `filter` and `extfilt` classes; it was changed
in translation - only the single-cycle `clock()` path is ported, not the bulk-clocking or windowed-sinc
resampling paths - and is used by MagPlaySID for all of its audio.

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

## Shine

`src/MagPlaySID/Export/Mp3Encoder.cs` and `Mp3Tables.cs`, the encoder behind **Export sound as
MP3**, are a C# translation of Shine, the fixed-point MP3 encoder (https://github.com/toots/shine),
with its Huffman tables and analysis window. It was changed in translation: mono only, the smallest
Huffman table tried first, the input scaled so the sound comes out as loud as it went in, and a
frame's spare bits written as ancillary data. Shine's bit stream code credits Jean-Georges Fritsch,
C-Cube Microsystems, and P. Everett.

Shine is free software under the GNU Library General Public License, version 2:

```
This library is free software; you can redistribute it and/or modify it under the terms of the GNU
Library General Public License as published by the Free Software Foundation; either version 2 of
the License, or (at your option) any later version.

This library is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
Library General Public License for more details.
```

As section 3 of that license allows, MagPlaySID applies the terms of the GNU General Public License,
version 2 - in [LICENSE](LICENSE) - to its translation.

## NAudio

MagPlaySID uses NAudio.Core 2.2.1 for its audio buffers and FFT. It is included in the
MagPlaySID executable.

```
Copyright 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
