# Where the C64 has each routine and table: found by its instructions (or bytes, for a table) in the
# C64's ROM, kept only where the finds run in the same order as the port's own.
import json, sys
from dis import T, L
import port
a, ms = port.build(align={})
rom = bytearray(65536)
rom[0xA000:0xC000] = open(r'C:\Censor\demos\bin\win_x86\C64\basic','rb').read()
rom[0xE000:0x10000] = open(r'C:\Censor\demos\bin\win_x86\C64\kernal','rb').read()
def sig(mem, pc, n):
    out = []
    for _ in range(n):
        op = mem[pc]
        if op not in T: return None
        nm, mode = T[op]; ln = L[mode]
        v = mem[pc + 1] if ln == 2 and mode not in ('rel', 'imm') else None
        out.append((op, v)); pc += ln
    return tuple(out)
segs = [(0xA000, 0xC000), (0xE000, 0xE4D3)]
idx = {}
for lo, hi in segs:
    for p in range(lo, hi):
        s = sig(rom, p, 6)
        if s: idx.setdefault(s, []).append(p)
labels = sorted((v, k) for k, v in a.syms.items() if (0xA00C <= v < 0xC000 or 0xE000 <= v < 0xE4D3) and not k.startswith('Q%'))
cand = []
for v, k in labels:
    s = sig(ms, v, 6)
    hits = idx.get(s, []) if s else []
    if len(hits) != 1:
        bs = bytes(ms[v:v + 6]); hits = []
        if any(bs):
            for lo, hi in segs:
                i = rom.find(bs, lo, hi)
                while i >= 0:
                    hits.append(i); i = rom.find(bs, i + 1, hi)
    if len(hits) == 1:
        cand.append((v, k, hits[0]))
# longest increasing run of C64 addresses, in the port's order, and in the same segment
import bisect
def lis(items):
    tails, prev, pos = [], [-1] * len(items), []
    for i, (_, _, c) in enumerate(items):
        j = bisect.bisect_left([items[t][2] for t in tails], c)
        if j: prev[i] = tails[j - 1]
        if j == len(tails): tails.append(i)
        else: tails[j] = i
    out, i = [], tails[-1] if tails else -1
    while i >= 0: out.append(items[i]); i = prev[i]
    return out[::-1]
keep = []
for lo, hi in segs:
    keep += lis([c for c in cand if lo <= c[0] < hi + 0x2000 and lo <= c[2] < hi])
m = {k: c for v, k, c in keep}
m.update(json.load(open('align_manual.json')))
json.dump(m, open('align.json', 'w'), indent=0, sort_keys=True)
print(len(cand), 'found,', len(keep), 'kept')
