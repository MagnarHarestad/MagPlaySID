# Builds the ROM images MagPlay.C64 embeds for BASIC tunes, from Microsoft's 6502 BASIC (m6502.asm,
# MIT licensed) with the C64's changes (patches.py, tail.mac, glue.mac) and the KERNAL routines
# written for this player (kernal.mac):
#   Basic.bin        the BASIC ROM, $A000-$BFFF
#   BasicKernal.bin  what goes over the KERNAL's $E000-$FFFF: BASIC's last part and the routines,
#                    as runs of [address lo, hi, length lo, hi, bytes]
# Run it with Python 3 from anywhere: python build_roms.py
# align.py recomputes align.json - where the C64 has each routine - and needs a C64 BASIC ROM
# image to read the addresses from; the build itself does not.
import os, sys
here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, here)
os.chdir(here)
import port, kbuild
a, ms = port.build()
if a.late or a.falls:
    raise SystemExit('routines out of place: %r %r' % (a.late, a.falls))
open('Basic.bin', 'wb').write(bytes(ms[0xA000:0xC000]))
mem = {k: ms[k] for k in range(0xE000, a.syms['ROMEND'])}
k, kmem = kbuild.build()
for addr in kmem:
    if addr in mem:
        raise SystemExit('the KERNAL routines overlap BASIC at %04X' % addr)
mem.update(kmem)
with open('BasicKernal.bin', 'wb') as f:
    for addr, bs in kbuild.runs(mem):
        f.write(bytes([addr & 255, addr >> 8, len(bs) & 255, len(bs) >> 8]) + bytes(bs))
print('Basic.bin and BasicKernal.bin built; BASIC to %04X, start at %04X' % (a.syms['ROMEND'], k.syms['BSTART']))
