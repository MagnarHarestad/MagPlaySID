# opcode -> (mnemonic, mode)
T = {}
def m(name, **kw):
    for mode, c in kw.items(): T[c] = (name, mode)
from asm import OPS
for name, modes in OPS.items():
    for mode, c in modes.items(): T[c] = (name, mode)
L = dict(imp=1, acc=1, imm=2, zp=2, zpx=2, zpy=2, abs=3, absx=3, absy=3, ind=3, indx=2, indy=2, rel=2)
def dis(mem, pc, n, base=0):
    out = []
    for _ in range(n):
        op = mem[pc - base]
        if op not in T: out.append((pc, '???', op)); pc += 1; continue
        name, mode = T[op]
        ln = L[mode]
        arg = [mem[pc - base + k] for k in range(1, ln)]
        v = arg[0] | (arg[1] << 8) if ln == 3 else (arg[0] if ln == 2 else None)
        if mode == 'rel': v = (pc + 2 + (v - 256 if v > 127 else v)) & 0xFFFF
        out.append((pc, name, mode, v))
        pc += ln
    return out
