# The C64 port: Microsoft's source with the C64's changes patched in.
import re
from asm import Asm
PATCHES = []
def patch(old, new, count=1):
    PATCHES.append((old, new, count))
def source():
    import os
    src = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'm6502.asm'), encoding='latin1').read().upper().replace('\r', '')
    src = re.sub(r'DEFINE\tDT\(Q\),<\nIRPC[^\n]*\n', '', src)
    for old, new, count in PATCHES:
        n = src.count(old)
        if n != count:
            raise Exception('patch anchor found %d times: %r' % (n, old[:80]))
        src = src.replace(old, new)
    return src
def collisions(a, ms):
    # NOTFNS tells EVAL's call of PTRGET by the low byte of its return address alone, as the
    # C64 does: no other call of PTRGET may share it.
    target = a.syms['PTRGET']; mark = (a.syms['ISVRET'] - 1) & 0xFF
    bad = []
    for p in range(0xA000, 0x10000 - 2):
        if ms[p] == 0x20 and ms[p + 1] | ms[p + 2] << 8 == target and p + 3 != a.syms['ISVRET'] and (p + 2) & 0xFF == mark:
            bad.append(p)
    return bad
def build(align=None):
    import json, os
    if align is None:
        here = os.path.dirname(os.path.abspath(__file__))
        align = json.load(open(os.path.join(here, 'align.json')))
    src = source()
    for pad in range(0, 40):
        a = Asm({'ISVPAD': pad}, {'REALIO': 3}, [], align)
        mem = a.run(src)
        ms = bytearray(65536)
        for k, v in mem.items(): ms[k] = v
        if not collisions(a, ms):
            return a, ms
    raise Exception('no padding avoids a PTRGET collision')
import patches  # noqa: registers the patches
