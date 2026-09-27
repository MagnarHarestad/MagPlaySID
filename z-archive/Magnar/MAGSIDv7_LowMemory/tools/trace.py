"""Reachability trace of a PSID's player code, for sizing other players against this one.

    python tools\\trace.py Lightforce_2006.sid

Follows control flow from init and play rather than disassembling linearly, so tables sitting
between routines are not counted as code. Instruction lengths cover all 256 opcodes, illegals
included, because a wrong length here would desynchronise the whole trace.

Two blind spots worth stating rather than hiding. Bytes reached only through self-modifying code
or an indirect jump are not followed, so the code figure is a lower bound. And the data figure
is a whole finished tune for a released .sid, which is not comparable to this project's test
songs - only the code and zero-page figures are.
"""
import sys

LEN = [
    1,2,1,2, 2,2,2,2, 1,2,1,2, 3,3,3,3,
    2,2,1,2, 2,2,2,2, 1,3,1,3, 3,3,3,3,
    3,2,1,2, 2,2,2,2, 1,2,1,2, 3,3,3,3,
    2,2,1,2, 2,2,2,2, 1,3,1,3, 3,3,3,3,
    1,2,1,2, 2,2,2,2, 1,2,1,2, 3,3,3,3,
    2,2,1,2, 2,2,2,2, 1,3,1,3, 3,3,3,3,
    1,2,1,2, 2,2,2,2, 1,2,1,2, 3,3,3,3,
    2,2,1,2, 2,2,2,2, 1,3,1,3, 3,3,3,3,
    2,2,2,2, 2,2,2,2, 1,2,1,2, 3,3,3,3,
    2,2,1,2, 2,2,2,2, 1,3,1,3, 3,3,3,3,
    2,2,2,2, 2,2,2,2, 1,2,1,2, 3,3,3,3,
    2,2,1,2, 2,2,2,2, 1,3,1,3, 3,3,3,3,
    2,2,2,2, 2,2,2,2, 1,2,1,2, 3,3,3,3,
    2,2,1,2, 2,2,2,2, 1,3,1,3, 3,3,3,3,
    2,2,2,2, 2,2,2,2, 1,2,1,2, 3,3,3,3,
    2,2,1,2, 2,2,2,2, 1,3,1,3, 3,3,3,3,
]

BRANCH = {0x10, 0x30, 0x50, 0x70, 0x90, 0xB0, 0xD0, 0xF0}
STOP = {0x60, 0x40, 0x00, 0x02, 0x12, 0x22, 0x32, 0x42, 0x52, 0x62, 0x72,
        0x92, 0xB2, 0xD2, 0xF2}          # RTS, RTI, BRK and the JAM opcodes

if len(sys.argv) != 2:
    print(__doc__)
    raise SystemExit(2)

d = open(sys.argv[1], "rb").read()
be16 = lambda o: (d[o] << 8) | d[o + 1]
data_offset = be16(6)
init, play = be16(10), be16(12)

# PSID allows two ways of saying where the data loads, and both are in use here. A header
# loadAddress of 0 means "the first two bytes of the C64 data are the load address, C64 file
# style" - that is what Lightforce does. A non-zero loadAddress means the data starts straight
# away with no embedded word - that is what this project's own exporter writes. Assuming the
# first form unconditionally reads two data bytes as an address and shifts everything after it
# by two, which silently produces a trace of nothing at all.
load = be16(8)
if load == 0:
    load = d[data_offset] | (d[data_offset + 1] << 8)
    body = d[data_offset + 2:]
else:
    body = d[data_offset:]

mem = bytearray(0x10000)
mem[load:load + len(body)] = body
lo, hi = load, load + len(body)

code = bytearray(0x10000)        # 1 = this byte belongs to an instruction
queue = [init, play]
seen = set()

while queue:
    pc = queue.pop()
    while True:
        if pc in seen or not (lo <= pc < hi):
            break
        seen.add(pc)
        op = mem[pc]
        n = LEN[op]
        for i in range(n):
            if lo <= pc + i < hi:
                code[pc + i] = 1

        if op == 0x20:                                   # JSR
            queue.append((mem[pc + 2] << 8) | mem[pc + 1])
        elif op == 0x4C:                                 # JMP abs
            pc = (mem[pc + 2] << 8) | mem[pc + 1]
            continue
        elif op == 0x6C or op in STOP:                   # JMP (ind), RTS/RTI/BRK/JAM
            break
        elif op in BRANCH:
            target = pc + 2 + ((mem[pc + 1] ^ 0x80) - 0x80)
            queue.append(target)

        pc += n

code_bytes = sum(code[lo:hi])
print(f"image     ${lo:04x}-${hi - 1:04x}  {hi - lo} bytes")
print(f"init=${init:04x}  play=${play:04x}")
print(f"code (reachable)          {code_bytes} bytes")
print(f"data (tables + song)      {hi - lo - code_bytes} bytes")

# Where the code actually lives, as runs, so gaps show up as the tables they are.
runs, start = [], None
for a in range(lo, hi + 1):
    is_code = a < hi and code[a]
    if is_code and start is None:
        start = a
    elif not is_code and start is not None:
        runs.append((start, a - 1))
        start = None
print("\ncode runs:")
for a, b in runs:
    print(f"  ${a:04x}-${b:04x}  {b - a + 1}")

# Zero-page footprint. State a player keeps here is outside the image entirely, so comparing
# image sizes alone understates it by exactly the amount that matters most.
ZP_COLS = {0x04, 0x05, 0x06, 0x07, 0x14, 0x15, 0x16, 0x17}
zp = set()
for pc in sorted(seen):
    op = mem[pc]
    if (op & 0x1f) in ZP_COLS and LEN[op] == 2:
        zp.add(mem[pc + 1])
    elif LEN[op] == 3:
        addr = (mem[pc + 2] << 8) | mem[pc + 1]
        if addr < 0x0100:
            zp.add(addr)

print(f"\nzero-page bytes touched   {len(zp)}")
print("  " + " ".join(f"${a:02x}" for a in sorted(zp)))
print(f"\nplayer all-in (code + zero-page state)  {code_bytes + len(zp)}")
