# BASIC for BASIC tunes

Some RSIDs are BASIC programs: HVSC has 589 of them, from Jeroen Tel's *Final Countdown* to type-in
listings from magazines. They play at the speed BASIC V2 runs on a C64, so to play them at their
tempo the emulated C64 needs a BASIC that costs, statement for statement, what the C64's costs.

Commodore's ROMs cannot be shipped. The C64's BASIC V2, though, is a version of Microsoft's 6502
BASIC, whose source Microsoft published under the MIT license (`m6502.asm`, `LICENSE-Microsoft.txt`).
This folder builds that source, with the C64's changes, into a ROM for the emulator:

- `asm.py` assembles the subset of MACRO-10 the source is written in: macros, conditionals, octal in
  the math package, six-character symbols. `WAIT n` and `^H` hex are additions, for `kernal.mac`.
- `port.py` and `patches.py` apply the changes, each with a comment: the page-zero layout, the
  vectors at $0300, ISCNTC, GOTO comparing the whole line number, TI, TI$ and ST through the
  KERNAL, the PRINT path, RND's five-byte constants, SYS, the start-up (`tail.mac`, `glue.mac`).
- `align.json` says where the C64 has each routine; the assembler puts each one there. That makes
  every branch that crosses a page cross it as on a C64 - a cycle each - and lets machine code that
  calls BASIC's routines by address find them. `align.py` made it from a C64 ROM image, which only
  that script needs: the build reads the JSON.
- `kernal.mac` is MagPlaySID's own: the KERNAL routines a BASIC program uses - the interrupt with
  the jiffy clock and keyboard scan, the screen editor, STOP, GET, the clock, the jump table - at
  the C64's addresses. Each path takes the C64's cycles; the `WAIT`s in `kwaits.json` make up the
  difference. Only BASIC tunes get these; other tunes keep the stand-in in `Kernal.cs`.

`python build_roms.py` writes `Basic.bin` and `BasicKernal.bin`, which MagPlay.C64 embeds.

How close it is, measured on the emulator against the C64's own ROMs (used only locally, to
measure): the same SID writes, every write, for 20 seconds, in all but a handful of the 589 tunes
that read the clock, the raster, voice 3 or the ROM's own bytes; and with the VIC's bad lines, the
tempo of *Final Countdown* within 0.01% of VICE's.
