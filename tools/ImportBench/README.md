# ImportBench

Import SID's fixed benchmark. Every change to the importer runs it before and after, and
only goes in if the numbers say it helps.

```bash
bash tools/ImportBench/bench.sh base     # on the code before the change
bash tools/ImportBench/bench.sh mine     # on the change
python tools/ImportBench/compare.py base mine
```

The two halves run in parallel and take a few minutes.

## The halves

- **`sng`** scores the import against the GoatTracker song the `.sid` was exported from. Each
  of the song's notes is looked for on a row of the import. The columns:
  - `placed`: the song's notes that have a note row;
  - `transp`: on the right note, allowing each instrument one transpose;
  - `asheard`: the same, and an instrument that sounds at one pitch counts as right whatever
    the `.sng` writes;
  - `sidpitch`: the row's note is the pitch the SID itself plays there;
  - `extra`: note rows the `.sng` has no note for.
- **`pitch`** counts the frames where the imported song sounds a tone at the original's pitch.
  It doesn't depend on how the importer reads notes. Frames an import leaves out, because it
  was cut to fit memory, count as not heard: a shorter import is not better for being shorter.

Both import as the dialog does, with `SidImportChoice.Best`. Pass `--suggest` to `bench.sh` to
take `SuggestGrid` as it is instead.

## Lists

The lists are not in the repository; they name local files.

- **`lists/sng.txt`:** a `song.sng|song.sid` pair a line, one version of each song. A corpus
  with 18 versions of one tune scores that tune 18 times.
- **`lists/pitch.txt`:** a `.sid` a line.

Lines that are empty or start with `#` are skipped. Results go to `results/`, which is not in the
repository either.
