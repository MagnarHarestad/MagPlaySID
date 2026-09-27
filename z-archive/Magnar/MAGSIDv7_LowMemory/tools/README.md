# tools

Two measurement scripts. Both exist because guessing at sizes here goes wrong: counting
instructions by hand misses a branch that changed length, and disassembling a foreign player
linearly counts its tables as code.

## measure.py — where this player's bytes go

```
python tools\measure.py program.sym 12aa
```

Sizes every labelled region by subtracting adjacent addresses in the assembler's symbol file,
then groups them into code, per-channel state and song data. The end address is exclusive and
comes from the assembler's own memory map — the line for the last `M_Music Player` segment, so
re-read it after any build that moves the end.

Add `--detail` to keep local labels (`Voice_Tick:_nextcmd` and friends), which is how you break
a single large routine down internally.

**To measure what a change actually costs**, build both variants and diff the output. Reverting
in place, measuring and restoring from a copy takes about a minute, and beats estimating — an
estimate for the wavetable step was once off by 30 bytes in the optimistic direction.

## trace.py — how big somebody else's player is

```
python tools\trace.py Lightforce_2006.sid
```

Traces reachability from a PSID's `init` and `play` entry points, following JSR/JMP/branch
targets and stopping at RTS/RTI/JAM, then reports code bytes, data bytes, where the code runs
are, and the zero-page footprint.

Read the two caveats in the file's own docstring before quoting a number from it. In short: the
code figure is a lower bound because self-modifying and indirect-jump targets are not followed,
and the data figure is a finished tune, so only code and zero-page compare meaningfully against
this project.

## loopcheck.py — did a converted song survive the conversion?

```
python tools\loopcheck.py "SNG-Magnar - Megastyle.s" SongData\Megastyle.s
```

Plays a song through both players' own loop engines and diffs the rows that come out - note,
instrument and tick count, 400 rows deep in each of the three patterns.

Use it after **any** change to a converter or to the duration/loop semantics. Every audible
fault in the ported songs was found with this once it had been heard; reading the files instead
produced three wrong guesses in a row, and the one time the diff pointed at the converter it
turned out to be a bug in the comparator instead - which it also made obvious.
