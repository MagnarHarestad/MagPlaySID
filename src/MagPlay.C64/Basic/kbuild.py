# Builds the KERNAL routines: the bytes it sets, as (address, bytes) runs.
import sys, json, os
from asm import Asm
WAITS = ['CLRW','SCRW','QTW','CHW','WRW','CRW','CTW0','DNW','UPW','RTW','LFW','HMW','RVW','RVW2','LOW','UPPW','LKW','DLW','INW','COLW','IRQW','SCNW','CLRCW','UDW','ERRW']
def build(waits=None):
    w = {k: 0 for k in WAITS}
    here = os.path.dirname(os.path.abspath(__file__))
    if waits is None and os.path.exists(os.path.join(here, 'kwaits.json')):
        waits = json.load(open(os.path.join(here, 'kwaits.json')))
    w.update(waits or {})
    a = Asm(w, {}, [])
    mem = a.run(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'kernal.mac')).read().upper())
    return a, mem
def runs(mem):
    out, cur = [], None
    for k in sorted(mem):
        if cur and k == cur[0] + len(cur[1]): cur[1].append(mem[k])
        else:
            cur = (k, [mem[k]]); out.append(cur)
    return out
if __name__ == '__main__':
    a, mem = build()
    with open(sys.argv[1], 'wb') as f:
        for addr, bs in runs(mem):
            f.write(bytes([addr & 255, addr >> 8, len(bs) & 255, len(bs) >> 8]) + bytes(bs))
    print(len(mem), 'bytes in', len(runs(mem)), 'runs; BSTART %04X' % a.syms['BSTART'])
