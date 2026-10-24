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

`lightforce.bin` and `magnificent.bin` were reassembled again on **2026-10-08**, after Lead1 changed
in both songs (Lightforce's gained a wavetable, a note program and NOHR; Magnificent's a new ADSR).
Same player and procedure; the other nine came out byte for byte as they were, which is the check.

`GateOff\` holds the same eleven images for the player whose hard restart keeps the waveform and
clears only the gate (`lda voice_last_waveform,x / and #$fe` in place of `lda #$08`) - the setting
`MagSid.Core.HardRestart.KeepsWaveform` selects, and which folder the tests compare against. Made on
**2026-10-07** with the same commands, the player from commit `6221867` edited in a scratch copy only
for that one instruction. The procedure was first checked by rebuilding all eleven originals byte for
byte; every GateOff image is exactly two bytes longer.

`slide_only.bin` and `slide_and_vib.bin` were reassembled on **2026-10-08**, when slide moved from an
instrument setting to pattern rows (`"1xx"` up, `"2xx"` down, a `$D3` command and its rate). The
player and song reader from commit `6221867` were ported to that in a scratch copy, and the
fixtures rewritten to slide rows. The nine images without a slide came out byte for byte as before.

All eleven were reassembled on **2026-10-13**, when a note off stopped stopping the voice: it now
parks `voice_wt_pos` on `wt_hold`, the wavetable pool's closing `$00`, so pulse, note program,
vibrato and slide run on through the release (`lda #wt_hold-wt_data` for `lda #0`, same size). Each
image differs from the one before in that single immediate byte at `$1011`.

All eleven, and the eleven in `GateOff\`, were reassembled on **2026-10-23**, when the player shrank
by 4 bytes, 8 with vibrato: `Voice_Tick`'s fetch indexes the row tables with Y instead of borrowing X,
`sty voice_arp_pos,x` replaces `tya`/`sta`, ALR (`asr #imm` in c6510, `$4B`) replaces `and #imm`/`lsr`
where the vibrato is armed, and a redundant `ldy voice_inst,x` goes where it turns round. Same
procedure; the eleven old images were first rebuilt byte for byte. Old and new were then run for 5000
frames each in the 6502 emulator and wrote the same SID registers, values and order, every frame.

`GateOff\` had not followed the 2026-10-08 and 2026-10-13 reassemblies - it matched the player neither
before nor after that change, which went unnoticed because the tests read it only with
`KeepsWaveform` on. It was rebuilt from the same player with its one-instruction edit, then checked
against the emitter with `KeepsWaveform` switched on: all 25 image tests passed.

`funk_bellaciao.bin` and `funk_slide.bin` (and their `GateOff\` twins) were added on **2026-10-24**
with funk speed: `var v_Song_Funk = N` beside `v_Song_Speed` makes the ticks alternate between the two
speeds. `ONbeat.PlaySong` reloads from `lda Song_Shuffle / eor #v_Song_Speed^v_Song_Funk / sta
Song_Shuffle / eor #v_Song_Funk / sta Song_Speed` in place of `move8 #v_Song_Speed, Song_Speed` - six
bytes more - and `Song_Shuffle` is one zero-page byte after `filter_delta`, cleared by music.init.
`Tables.s` emits `v_Song_Funk` from its include-time scan, as it does `v_slide`. The fixtures beside
this folder are BellaCiao and slide_only.s with a funk speed added. Same procedure; all eleven images
without funk speed, and the eleven in `GateOff\`, came out byte for byte as before.

`player.patch` beside this file is every change the scratch player carries - pattern slides, SOFTHR,
the release and funk speed - against commit `6221867`'s `Code/musicplayer.s` and `Startup/Tables.s` (apply with
`patch -p0 --binary`; the sources are CRLF). Rebuilding any golden means applying it first.

`opcodes.txt` is the emitter's opcode table, one `mnemonic mode hex` line each, frozen after
`OpcodeTableTests` had checked all 106 of them against c6510 instruction by instruction. `asr Immediate
4B` (ALR) joined it on 2026-10-23, after c6510 assembled `asr #$12` as `4B 12`.

## When one of these has to change

Only when the player itself changes, and then a golden is not something to regenerate from the C# —
that would just record whatever the emitter now does and assert it against itself. Assemble the new
player with c6510 from the Censor SVN tree and replace the file with its output. If that is not
possible, say so in the commit rather than refreshing the goldens from the code under test.
