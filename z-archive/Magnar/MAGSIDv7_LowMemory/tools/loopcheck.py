r"""Diffs a converted song against its old-MAGSID source by playing both.

    python tools\loopcheck.py "SNG-Magnar - Megastyle.s" SongData\Megastyle.s

The two players disagree about several things - what a finished LE does to the loop anchor,
what a length nibble means in ticks, whether a $00 row byte is "keep the pitch" or "instrument
0, length 0" - and a conversion bug in any of them is audible but nearly invisible in the files.
So rather than compare the two texts, this walks each the way its own player would and diffs the
rows that actually come out: note, instrument and tick count, 400 rows deep.

Instruments are compared for *consistency* rather than equality, since they are renumbered
densely on the way across.

Every audible fault in the ported songs was located with this after being heard. Reading the
files instead produced three wrong guesses in a row.
"""
import re
import sys

SEMI = {"cw": "C-", "cb": "C#", "dw": "D-", "db": "D#", "ew": "E-", "fw": "F-",
        "fb": "F#", "gw": "G-", "gb": "G#", "aw": "A-", "ab": "A#", "bw": "B-"}
NOTE_RE = re.compile(r"^([a-g][wb])([0-7])$")
LIMIT = 400


def old_tokens(path, pattern):
    """The pattern as a token list: ('note', name), ('row', inst, len), ('fe', n), ('ff',)."""
    out, started, expect_row = [], False, False
    for raw in open(path, encoding="utf-8", errors="replace").read().split("\n"):
        t = raw.split("//")[0].strip()
        if re.match(r"^pa\d+:", t):
            started = t.startswith(pattern + ":")
            t = t.split(":", 1)[1].strip()
            if not t:
                continue
        if not started or not t:
            continue
        m = NOTE_RE.match(t)
        if m:
            out.append(("note", f"{SEMI[m.group(1)]}{m.group(2)}"))
            expect_row = True
            continue
        if not t.lower().startswith(".byte"):
            continue
        vals = []
        for tok in t[5:].split(","):
            tok = tok.strip()
            h = re.match(r"^\$([0-9a-fA-F]+)$", tok)
            d = re.match(r"^(\d+)$", tok)
            if h:
                vals.append(int(h.group(1), 16))
            elif d:
                vals.append(int(d.group(1)))
        if not vals:
            continue
        if vals[0] == 0xFE:
            out.append(("fe", vals[1]))
        elif vals[0] == 0xFF:
            out.append(("ff",))
            break
        elif vals[0] == 0 and not expect_row:
            # A leading $00 says "keep the current pitch". The byte after it is the row byte
            # whatever its value - which is how "$00" can also mean instrument 0, length 0, as
            # Breakdance's kick does. Only position tells the two apart.
            out.append(("keep",))
            expect_row = True
        else:
            out.append(("row", vals[0] >> 4, vals[0] & 0x0F))
            expect_row = False
    return out


def play_old(tokens):
    """The old player: a finished loop moves the anchor to just past itself."""
    rows, i, anchor, times, pending, cur = [], 0, 0, 0, None, "C-4"
    while len(rows) < LIMIT and i < len(tokens):
        kind = tokens[i]
        if kind[0] == "note":
            cur = pending = kind[1]
            i += 1
        elif kind[0] == "keep":
            pending = cur
            i += 1
        elif kind[0] == "fe":
            if kind[1] == 0:
                anchor = i + 1
                i += 1
            elif times != kind[1]:
                times += 1
                i = anchor
            else:
                times = 0
                anchor = i + 1
                i += 1
        elif kind[0] == "ff":
            i = 0
        else:
            # In ticks on both sides: the old length nibble N means N+1 ticks, and a run of
            # MAGSIDv7 row lines is one tick each.
            rows.append((pending or cur, kind[1] + 1, kind[2] + 1))
            pending = None
            i += 1
    return rows


def play_new(path, pattern):
    """MAGSIDv7: LS anchors and resets the counter, LE loops back to it. Lengths come out in
    ticks - one authored row line is one tick."""
    lines, grab = [], False
    for raw in open(path, encoding="utf-8").read().split("\n"):
        t = raw.strip()
        if t.startswith("PAT"):
            grab = t == f"PAT{pattern:02d}:"
            continue
        if grab:
            if t == "END:":
                break
            if t:
                lines.append(t)

    rows, i, anchor, times, holds, last = [], 0, 0, 0, 0, None
    while len(rows) < LIMIT and i <= len(lines):
        if i == len(lines):
            i = 0                                  # the order list loops this pattern forever
            anchor, times = 0, 0
            continue
        t = lines[i]
        if t == "LS":
            anchor, times = i + 1, 0
            i += 1
        elif t.startswith("LE"):
            count = int(t[2:]) if len(t) > 2 else None
            times += 1
            if count is None or times < count:
                i = anchor
            else:
                times = 0
                i += 1
        elif t.startswith('"...'):
            holds += 1
            i += 1
        else:
            m = re.match(r'^"([^"]+)",\s*(\d+)$', t)
            if last is not None:
                rows[-1] = (rows[-1][0], rows[-1][1], holds + 1)
            holds = 0
            rows.append((m.group(1), int(m.group(2)), 1))
            last = len(rows) - 1
            i += 1
    return rows


src, gen = sys.argv[1], sys.argv[2]
for n in (1, 2, 3):
    a = play_old(old_tokens(src, f"pa{n}"))
    b = play_new(gen, n)
    limit = min(len(a), len(b)) - 1          # the last row's length is still open

    # Instruments are renumbered densely on the way across - the old table can leave gaps, and
    # two of its entries can share a row - so compare the numbering for *consistency* rather
    # than equality: the same old instrument must always become the same new one.
    mapping, clash = {}, None
    for k in range(limit):
        if mapping.setdefault(a[k][1], b[k][1]) != b[k][1]:
            clash = clash if clash is not None else k

    bad = next((k for k in range(limit) if (a[k][0], a[k][2]) != (b[k][0], b[k][2])), None)
    if bad is None and clash is not None:
        bad = clash

    if bad is None:
        print(f"pattern {n}: {limit} rows agree")
    else:
        print(f"pattern {n}: diverges at row {bad}")
        for k in range(max(0, bad - 3), min(limit, bad + 4)):
            mark = "  <<<" if k == bad else ""
            print(f"    {k:4d}  old={a[k]}   new={b[k]}{mark}")
