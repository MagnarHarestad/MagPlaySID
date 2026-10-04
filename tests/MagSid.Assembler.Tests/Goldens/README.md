# Goldens

What c6510 produced. These files exist because the assembler and the assembly player no longer live
in this repository — see the root README — so there is nothing left to assemble a fresh comparison
against. Freezing its output keeps the verification; only the ability to re-derive it is gone.

Every `.bin` is the complete `$1000` memory image, player and song data, with the two-byte `.prg`
load address stripped. Produced on **2026-09-27** at commit `6221867`, when the differential tests
still ran and were green, by:

```
c6510.exe -p include/ -d v_export_loc=$1000 -s out.prg $1000 + export_<song>.s
```

`bellaciao_2000.bin` and `bellaciao_4400.bin` are the same song assembled at those origins, which is
what pins relocation. `slide_only.bin` and `slide_and_vib.bin` come from the two fixture songs beside
this folder: no real song uses SLIDE, so without them the slide paths and the shared
frequency-modulation block with both effects on would go uncompared.

`lightforce.bin` was reassembled on **2026-10-05**, after Lightforce's Bass lost its filter program
(Kick's identical one already sets the filter up). Same command, with the player, include files and
`c6510.exe` taken from commit `6221867` and `Globalmacros.s` read from the Censor SVN tree. The
procedure was checked first by reproducing the old `lightforce.bin` byte for byte from the old song;
the new one differs from it in a single byte, Bass's `flt_start` entry, `$01` to `$00`.

`opcodes.txt` is the emitter's opcode table, one `mnemonic mode hex` line each, frozen after
`OpcodeTableTests` had checked all 106 of them against c6510 instruction by instruction.

## When one of these has to change

Only when the player itself changes, and then a golden is not something to regenerate from the C# —
that would just record whatever the emitter now does and assert it against itself. Assemble the new
player with c6510 from the Censor SVN tree and replace the file with its output. If that is not
possible, say so in the commit rather than refreshing the goldens from the code under test.
