"""A correctness-focused 6502 interpreter, scoped to run Lightforce_2006.sid's init/play and
nothing else - not a general emulator. Built specifically to settle the user's "plays at 2x
tempo" report empirically: static disassembly of raw 6502 is easy to misread by hand (branch
targets, which operand a byte lands in), so this executes the real routine and counts how many
real play()-calls actually occur between genuine note triggers ($D400/$D401 writes), which is
the ground truth no amount of eyeballing opcodes can substitute for.

Covers exactly the opcodes this routine uses (documented ones plus SBX, the one undocumented
opcode Rob Hubbard's code relies on for "X -= imm, no borrow into A"). Not a general-purpose
emulator - unsupported opcodes raise loudly rather than silently doing the wrong thing.
"""
import sys


class CPU:
    def __init__(self, mem, on_write=None):
        self.mem = mem
        self.a = self.x = self.y = 0
        self.sp = 0xFF
        self.pc = 0
        self.c = self.z = self.n = self.v = 0
        self.on_write = on_write
        self.cycles = 0

    def read(self, addr):
        return self.mem[addr & 0xFFFF]

    def write(self, addr, value):
        addr &= 0xFFFF
        value &= 0xFF
        self.mem[addr] = value
        if self.on_write:
            self.on_write(addr, value)

    def push(self, value):
        self.write(0x100 + self.sp, value)
        self.sp = (self.sp - 1) & 0xFF

    def pop(self):
        self.sp = (self.sp + 1) & 0xFF
        return self.read(0x100 + self.sp)

    def setzn(self, v):
        v &= 0xFF
        self.z = 1 if v == 0 else 0
        self.n = 1 if v & 0x80 else 0
        return v

    def fetch(self):
        b = self.read(self.pc)
        self.pc = (self.pc + 1) & 0xFFFF
        return b

    def fetch16(self):
        lo = self.fetch()
        hi = self.fetch()
        return lo | (hi << 8)

    # -------------------------------------------------------------- addressing
    def addr_zp(self):
        return self.fetch()

    def addr_zpx(self):
        return (self.fetch() + self.x) & 0xFF

    def addr_zpy(self):
        return (self.fetch() + self.y) & 0xFF

    def addr_abs(self):
        return self.fetch16()

    def addr_abx(self):
        return (self.fetch16() + self.x) & 0xFFFF

    def addr_aby(self):
        return (self.fetch16() + self.y) & 0xFFFF

    def addr_izx(self):
        zp = (self.fetch() + self.x) & 0xFF
        return self.read(zp) | (self.read((zp + 1) & 0xFF) << 8)

    def addr_izy(self):
        zp = self.fetch()
        base = self.read(zp) | (self.read((zp + 1) & 0xFF) << 8)
        return (base + self.y) & 0xFFFF

    # -------------------------------------------------------------- ALU helpers
    def adc(self, v):
        total = self.a + v + self.c
        self.v = 1 if (~(self.a ^ v) & (self.a ^ total) & 0x80) else 0
        self.c = 1 if total > 0xFF else 0
        self.a = self.setzn(total)

    def sbc(self, v):
        self.adc((~v) & 0xFF)

    def cmp(self, reg, v):
        r = (reg - v) & 0x1FF
        self.c = 1 if reg >= v else 0
        self.setzn(r)

    def branch(self, cond):
        off = self.fetch()
        if cond:
            self.pc = (self.pc + ((off ^ 0x80) - 0x80)) & 0xFFFF
            self.cycles += 1

    # -------------------------------------------------------------- step
    def step(self):
        op = self.fetch()
        self.cycles += 2  # rough; exact cycle counts don't matter for this measurement

        def rmw(addr_fn, op_fn):
            addr = addr_fn()
            v = op_fn(self.read(addr))
            self.write(addr, v)

        # --- loads / stores ---
        if op == 0xA9: self.a = self.setzn(self.fetch())
        elif op == 0xA5: self.a = self.setzn(self.read(self.addr_zp()))
        elif op == 0xB5: self.a = self.setzn(self.read(self.addr_zpx()))
        elif op == 0xAD: self.a = self.setzn(self.read(self.addr_abs()))
        elif op == 0xBD: self.a = self.setzn(self.read(self.addr_abx()))
        elif op == 0xB9: self.a = self.setzn(self.read(self.addr_aby()))
        elif op == 0xA1: self.a = self.setzn(self.read(self.addr_izx()))
        elif op == 0xB1: self.a = self.setzn(self.read(self.addr_izy()))
        elif op == 0xA2: self.x = self.setzn(self.fetch())
        elif op == 0xA6: self.x = self.setzn(self.read(self.addr_zp()))
        elif op == 0xB6: self.x = self.setzn(self.read(self.addr_zpy()))
        elif op == 0xAE: self.x = self.setzn(self.read(self.addr_abs()))
        elif op == 0xBE: self.x = self.setzn(self.read(self.addr_aby()))
        elif op == 0xA0: self.y = self.setzn(self.fetch())
        elif op == 0xA4: self.y = self.setzn(self.read(self.addr_zp()))
        elif op == 0xB4: self.y = self.setzn(self.read(self.addr_zpx()))
        elif op == 0xAC: self.y = self.setzn(self.read(self.addr_abs()))
        elif op == 0xBC: self.y = self.setzn(self.read(self.addr_abx()))
        elif op == 0x85: self.write(self.addr_zp(), self.a)
        elif op == 0x95: self.write(self.addr_zpx(), self.a)
        elif op == 0x8D: self.write(self.addr_abs(), self.a)
        elif op == 0x9D: self.write(self.addr_abx(), self.a)
        elif op == 0x99: self.write(self.addr_aby(), self.a)
        elif op == 0x81: self.write(self.addr_izx(), self.a)
        elif op == 0x91: self.write(self.addr_izy(), self.a)
        elif op == 0x86: self.write(self.addr_zp(), self.x)
        elif op == 0x96: self.write(self.addr_zpy(), self.x)
        elif op == 0x8E: self.write(self.addr_abs(), self.x)
        elif op == 0x84: self.write(self.addr_zp(), self.y)
        elif op == 0x94: self.write(self.addr_zpx(), self.y)
        elif op == 0x8C: self.write(self.addr_abs(), self.y)
        # --- transfers ---
        elif op == 0xAA: self.x = self.setzn(self.a)
        elif op == 0x8A: self.a = self.setzn(self.x)
        elif op == 0xA8: self.y = self.setzn(self.a)
        elif op == 0x98: self.a = self.setzn(self.y)
        elif op == 0xBA: self.x = self.setzn(self.sp)
        elif op == 0x9A: self.sp = self.x
        # --- stack ---
        elif op == 0x48: self.push(self.a)
        elif op == 0x68: self.a = self.setzn(self.pop())
        elif op == 0x08:
            flags = (self.n << 7) | (self.v << 6) | 0x30 | (0 << 3) | (self.c and 0)
            flags = (self.n<<7)|(self.v<<6)|0x30|(self.z<<1)|self.c
            self.push(flags)
        elif op == 0x28:
            flags = self.pop()
            self.c = flags & 1; self.z = (flags >> 1) & 1
            self.v = (flags >> 6) & 1; self.n = (flags >> 7) & 1
        # --- inc/dec register ---
        elif op == 0xE8: self.x = self.setzn(self.x + 1)
        elif op == 0xC8: self.y = self.setzn(self.y + 1)
        elif op == 0xCA: self.x = self.setzn(self.x - 1)
        elif op == 0x88: self.y = self.setzn(self.y - 1)
        # --- inc/dec memory ---
        elif op == 0xE6: rmw(self.addr_zp, lambda v: self.setzn(v + 1))
        elif op == 0xF6: rmw(self.addr_zpx, lambda v: self.setzn(v + 1))
        elif op == 0xEE: rmw(self.addr_abs, lambda v: self.setzn(v + 1))
        elif op == 0xFE: rmw(self.addr_abx, lambda v: self.setzn(v + 1))
        elif op == 0xC6: rmw(self.addr_zp, lambda v: self.setzn(v - 1))
        elif op == 0xD6: rmw(self.addr_zpx, lambda v: self.setzn(v - 1))
        elif op == 0xCE: rmw(self.addr_abs, lambda v: self.setzn(v - 1))
        elif op == 0xDE: rmw(self.addr_abx, lambda v: self.setzn(v - 1))
        # --- ALU (A) ---
        elif op == 0x69: self.adc(self.fetch())
        elif op == 0x65: self.adc(self.read(self.addr_zp()))
        elif op == 0x75: self.adc(self.read(self.addr_zpx()))
        elif op == 0x6D: self.adc(self.read(self.addr_abs()))
        elif op == 0x7D: self.adc(self.read(self.addr_abx()))
        elif op == 0x79: self.adc(self.read(self.addr_aby()))
        elif op == 0x61: self.adc(self.read(self.addr_izx()))
        elif op == 0x71: self.adc(self.read(self.addr_izy()))
        elif op == 0xE9: self.sbc(self.fetch())
        elif op == 0xE5: self.sbc(self.read(self.addr_zp()))
        elif op == 0xF5: self.sbc(self.read(self.addr_zpx()))
        elif op == 0xED: self.sbc(self.read(self.addr_abs()))
        elif op == 0xFD: self.sbc(self.read(self.addr_abx()))
        elif op == 0xF9: self.sbc(self.read(self.addr_aby()))
        elif op == 0x29: self.a = self.setzn(self.a & self.fetch())
        elif op == 0x25: self.a = self.setzn(self.a & self.read(self.addr_zp()))
        elif op == 0x35: self.a = self.setzn(self.a & self.read(self.addr_zpx()))
        elif op == 0x2D: self.a = self.setzn(self.a & self.read(self.addr_abs()))
        elif op == 0x3D: self.a = self.setzn(self.a & self.read(self.addr_abx()))
        elif op == 0x39: self.a = self.setzn(self.a & self.read(self.addr_aby()))
        elif op == 0x21: self.a = self.setzn(self.a & self.read(self.addr_izx()))
        elif op == 0x31: self.a = self.setzn(self.a & self.read(self.addr_izy()))
        elif op == 0x09: self.a = self.setzn(self.a | self.fetch())
        elif op == 0x05: self.a = self.setzn(self.a | self.read(self.addr_zp()))
        elif op == 0x15: self.a = self.setzn(self.a | self.read(self.addr_zpx()))
        elif op == 0x0D: self.a = self.setzn(self.a | self.read(self.addr_abs()))
        elif op == 0x1D: self.a = self.setzn(self.a | self.read(self.addr_abx()))
        elif op == 0x19: self.a = self.setzn(self.a | self.read(self.addr_aby()))
        elif op == 0x01: self.a = self.setzn(self.a | self.read(self.addr_izx()))
        elif op == 0x11: self.a = self.setzn(self.a | self.read(self.addr_izy()))
        elif op == 0x49: self.a = self.setzn(self.a ^ self.fetch())
        elif op == 0x45: self.a = self.setzn(self.a ^ self.read(self.addr_zp()))
        elif op == 0x55: self.a = self.setzn(self.a ^ self.read(self.addr_zpx()))
        elif op == 0x4D: self.a = self.setzn(self.a ^ self.read(self.addr_abs()))
        elif op == 0x5D: self.a = self.setzn(self.a ^ self.read(self.addr_abx()))
        elif op == 0x59: self.a = self.setzn(self.a ^ self.read(self.addr_aby()))
        elif op == 0x41: self.a = self.setzn(self.a ^ self.read(self.addr_izx()))
        elif op == 0x51: self.a = self.setzn(self.a ^ self.read(self.addr_izy()))
        # --- compares ---
        elif op == 0xC9: self.cmp(self.a, self.fetch())
        elif op == 0xC5: self.cmp(self.a, self.read(self.addr_zp()))
        elif op == 0xD5: self.cmp(self.a, self.read(self.addr_zpx()))
        elif op == 0xCD: self.cmp(self.a, self.read(self.addr_abs()))
        elif op == 0xDD: self.cmp(self.a, self.read(self.addr_abx()))
        elif op == 0xD9: self.cmp(self.a, self.read(self.addr_aby()))
        elif op == 0xC1: self.cmp(self.a, self.read(self.addr_izx()))
        elif op == 0xD1: self.cmp(self.a, self.read(self.addr_izy()))
        elif op == 0xE0: self.cmp(self.x, self.fetch())
        elif op == 0xE4: self.cmp(self.x, self.read(self.addr_zp()))
        elif op == 0xEC: self.cmp(self.x, self.read(self.addr_abs()))
        elif op == 0xC0: self.cmp(self.y, self.fetch())
        elif op == 0xC4: self.cmp(self.y, self.read(self.addr_zp()))
        elif op == 0xCC: self.cmp(self.y, self.read(self.addr_abs()))
        # --- shifts/rotates ---
        elif op == 0x0A:
            self.c = (self.a >> 7) & 1
            self.a = self.setzn(self.a << 1)
        elif op == 0x06: rmw(self.addr_zp, lambda v: (self._c_from(v, 7), self.setzn(v << 1))[1])
        elif op == 0x16: rmw(self.addr_zpx, lambda v: (self._c_from(v, 7), self.setzn(v << 1))[1])
        elif op == 0x0E: rmw(self.addr_abs, lambda v: (self._c_from(v, 7), self.setzn(v << 1))[1])
        elif op == 0x1E: rmw(self.addr_abx, lambda v: (self._c_from(v, 7), self.setzn(v << 1))[1])
        elif op == 0x4A:
            self.c = self.a & 1
            self.a = self.setzn(self.a >> 1)
        elif op == 0x46: rmw(self.addr_zp, lambda v: (self._c_from(v, 0), self.setzn(v >> 1))[1])
        elif op == 0x56: rmw(self.addr_zpx, lambda v: (self._c_from(v, 0), self.setzn(v >> 1))[1])
        elif op == 0x4E: rmw(self.addr_abs, lambda v: (self._c_from(v, 0), self.setzn(v >> 1))[1])
        elif op == 0x5E: rmw(self.addr_abx, lambda v: (self._c_from(v, 0), self.setzn(v >> 1))[1])
        elif op == 0x2A:
            newc = (self.a >> 7) & 1
            self.a = self.setzn((self.a << 1) | self.c)
            self.c = newc
        elif op == 0x26: rmw(self.addr_zp, self._rol)
        elif op == 0x36: rmw(self.addr_zpx, self._rol)
        elif op == 0x2E: rmw(self.addr_abs, self._rol)
        elif op == 0x3E: rmw(self.addr_abx, self._rol)
        elif op == 0x6A:
            newc = self.a & 1
            self.a = self.setzn((self.a >> 1) | (self.c << 7))
            self.c = newc
        elif op == 0x66: rmw(self.addr_zp, self._ror)
        elif op == 0x76: rmw(self.addr_zpx, self._ror)
        elif op == 0x6E: rmw(self.addr_abs, self._ror)
        elif op == 0x7E: rmw(self.addr_abx, self._ror)
        # --- bit ---
        elif op == 0x24:
            v = self.read(self.addr_zp())
            self.z = 1 if (self.a & v) == 0 else 0
            self.n = (v >> 7) & 1
            self.v = (v >> 6) & 1
        elif op == 0x2C:
            v = self.read(self.addr_abs())
            self.z = 1 if (self.a & v) == 0 else 0
            self.n = (v >> 7) & 1
            self.v = (v >> 6) & 1
        # --- flags ---
        elif op == 0x18: self.c = 0
        elif op == 0x38: self.c = 1
        elif op == 0xD8: pass
        elif op == 0xF8: pass
        elif op == 0x58: pass
        elif op == 0x78: pass
        elif op == 0xB8: self.v = 0
        # --- branches ---
        elif op == 0x10: self.branch(self.n == 0)
        elif op == 0x30: self.branch(self.n == 1)
        elif op == 0x50: self.branch(self.v == 0)
        elif op == 0x70: self.branch(self.v == 1)
        elif op == 0x90: self.branch(self.c == 0)
        elif op == 0xB0: self.branch(self.c == 1)
        elif op == 0xD0: self.branch(self.z == 0)
        elif op == 0xF0: self.branch(self.z == 1)
        # --- jumps/calls ---
        elif op == 0x4C: self.pc = self.addr_abs()
        elif op == 0x6C:
            ptr = self.addr_abs()
            lo = self.read(ptr)
            hi = self.read((ptr & 0xFF00) | ((ptr + 1) & 0xFF))  # 6502 page-wrap bug, faithfully
            self.pc = lo | (hi << 8)
        elif op == 0x20:
            target = self.addr_abs()
            ret = (self.pc - 1) & 0xFFFF
            self.push((ret >> 8) & 0xFF)
            self.push(ret & 0xFF)
            self.pc = target
        elif op == 0x60:
            lo = self.pop()
            hi = self.pop()
            self.pc = ((lo | (hi << 8)) + 1) & 0xFFFF
            return "RTS"
        elif op == 0xEA: pass
        # --- undocumented: ISC (ISB/INS) : INC memory, then SBC that new value from A.
        # MAGSID's own Voice_Tick:.loopend uses the abs,x form to collapse an inc+cmp pair
        # into one 3-byte instruction, so the player can no longer be run here without it. ---
        elif op in (0xE7, 0xF7, 0xEF, 0xFF, 0xFB, 0xE3, 0xF3):
            mode = {0xE7: self.addr_zp, 0xF7: self.addr_zpx, 0xEF: self.addr_abs,
                    0xFF: self.addr_abx, 0xFB: self.addr_aby, 0xE3: self.addr_izx,
                    0xF3: self.addr_izy}[op]
            addr = mode()
            v = (self.read(addr) + 1) & 0xFF
            self.write(addr, v)
            self.sbc(v)
        # --- undocumented: SBX (AXS) #imm : X = (A & X) - imm, sets C like CMP, no decimal ---
        elif op == 0xCB:
            imm = self.fetch()
            t = (self.a & self.x)
            self.c = 1 if t >= imm else 0
            self.x = self.setzn((t - imm) & 0xFF)
        else:
            raise NotImplementedError(f"unsupported opcode ${op:02x} at ${self.pc - 1:04x}")
        return None

    def _c_from(self, v, bit):
        self.c = (v >> bit) & 1

    def _rol(self, v):
        newc = (v >> 7) & 1
        r = self.setzn(((v << 1) | self.c) & 0xFF)
        self.c = newc
        return r

    def _ror(self, v):
        newc = v & 1
        r = self.setzn((v >> 1) | (self.c << 7))
        self.c = newc
        return r

    def call(self, addr, max_steps=200000, on_pc=None):
        """Runs a JSR-equivalent to `addr` until it RTS's back past the caller. Uses a sentinel
        return address on the stack so the RTS is unambiguous. If given, on_pc(cpu) fires right
        before the instruction at each new PC executes - for tracing "execution reached this
        exact address", which is a far more precise signal than watching register writes when
        the same register is legitimately rewritten every frame for other reasons (as $D400 and
        $D404 both are here)."""
        sentinel = 0xFFFF
        self.push((sentinel >> 8) & 0xFF)
        self.push(sentinel & 0xFF)
        self.pc = addr
        for _ in range(max_steps):
            if self.pc == (sentinel + 1) & 0xFFFF:
                return
            if on_pc:
                on_pc(self)
            self.step()
        raise RuntimeError(f"call to ${addr:04x} did not return within {max_steps} steps")
