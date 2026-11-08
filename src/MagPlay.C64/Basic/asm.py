# A small assembler for the subset of MACRO-10 that Microsoft's M6502 BASIC source uses.
import re, sys

OPS = {
 # name: {mode: opcode}
}
def op(name, **modes):
    OPS[name] = modes
# modes: imp, acc, imm, zp, zpx, zpy, abs, absx, absy, ind, indx, indy, rel
for n, base in [('ORA',0x00),('AND',0x20),('EOR',0x40),('ADC',0x60),('STA',0x80),('LDA',0xA0),('CMP',0xC0),('SBC',0xE0)]:
    m = dict(indx=base+1, zp=base+5, imm=base+9, abs=base+0xD, indy=base+0x11, zpx=base+0x15, absy=base+0x19, absx=base+0x1D)
    if n == 'STA': del m['imm']
    OPS[n] = m
for n, base in [('ASL',0x00),('ROL',0x20),('LSR',0x40),('ROR',0x60)]:
    OPS[n] = dict(zp=base+6, acc=base+0xA, abs=base+0xE, zpx=base+0x16, absx=base+0x1E)
op('DEC', zp=0xC6, abs=0xCE, zpx=0xD6, absx=0xDE)
op('INC', zp=0xE6, abs=0xEE, zpx=0xF6, absx=0xFE)
op('LDX', imm=0xA2, zp=0xA6, abs=0xAE, zpy=0xB6, absy=0xBE)
op('LDY', imm=0xA0, zp=0xA4, abs=0xAC, zpx=0xB4, absx=0xBC)
op('STX', zp=0x86, abs=0x8E, zpy=0x96)
op('STY', zp=0x84, abs=0x8C, zpx=0x94)
op('CPX', imm=0xE0, zp=0xE4, abs=0xEC)
op('CPY', imm=0xC0, zp=0xC4, abs=0xCC)
op('BIT', zp=0x24, abs=0x2C)
op('JMP', abs=0x4C, ind=0x6C)
op('JSR', abs=0x20)
for n, c in [('BPL',0x10),('BMI',0x30),('BVC',0x50),('BVS',0x70),('BCC',0x90),('BCS',0xB0),('BNE',0xD0),('BEQ',0xF0)]:
    op(n, rel=c)
for n, c in [('BRK',0),('PHP',8),('CLC',0x18),('PLP',0x28),('SEC',0x38),('RTI',0x40),('PHA',0x48),('CLI',0x58),('RTS',0x60),
             ('PLA',0x68),('SEI',0x78),('DEY',0x88),('TXA',0x8A),('TYA',0x98),('TXS',0x9A),('TAY',0xA8),('TAX',0xAA),
             ('CLV',0xB8),('TSX',0xBA),('INY',0xC8),('DEX',0xCA),('CLD',0xD8),('INX',0xE8),('NOP',0xEA),('SED',0xF8)]:
    op(n, imp=c)

class AsmError(Exception): pass

def split_args(s):
    """Split on top-level commas (outside <> and quotes)."""
    out, depth, cur, q = [], 0, '', False
    for ch in s:
        if q:
            cur += ch
            if ch == '"': q = False
            continue
        if ch == '"': q = True; cur += ch; continue
        if ch == '<': depth += 1
        elif ch == '>': depth -= 1
        if ch == ',' and depth == 0:
            out.append(cur); cur = ''
        else:
            cur += ch
    out.append(cur)
    return out

def strip_brackets(s):
    s = s.strip()
    if s.startswith('<') and match_bracket(s, 0) == len(s) - 1:
        return s[1:-1]
    return s

def match_bracket(s, i):
    depth, q = 0, False
    for j in range(i, len(s)):
        ch = s[j]
        if q:
            if ch == '"': q = False
            continue
        if ch == '"': q = True
        elif ch == '<': depth += 1
        elif ch == '>':
            depth -= 1
            if depth == 0: return j
    raise AsmError('unbalanced < in ' + s[i:i+60])

class Asm:
    def __init__(self, presets, overrides, segments, align=None):
        self.align = align or {}
        self.falls = {}
        self.lastop = None
        self.late = {}
        self.presets = presets      # symbol: value set before assembly (switches)
        self.overrides = overrides  # symbol: value that wins over the source's own definition
        self.segments = segments    # (from_addr, to_addr): code reaching from_addr continues at to_addr
        self.syms = {}
        self.macros = {}
        self.listing = []

    def run(self, text):
        self.text = text
        for p in (1, 2):
            self.pas = p
            self.pc = 0
            self.mem = {}
            self.gensym = 0
            self.modes_i = 0
            if p == 1:
                self.labels1 = set()
                self.modes = []
                self.syms = dict(self.presets)
            self.macros = {}
            self.radix = 10
            self.unknown = False
            self.block(text)
        return self.mem

    # ---- statements
    def block(self, text):
        i, n = 0, len(text)
        while i < n:
            # gather one statement: up to newline, unless inside <...>
            j, depth, q = i, 0, False
            stmt = ''
            while j < n:
                ch = text[j]
                if q:
                    stmt += ch
                    if ch == '"' or ch == '\n': q = False
                    j += 1; continue
                if ch == ';' and depth == 0:
                    # comment to end of line
                    while j < n and text[j] != '\n': j += 1
                    continue
                if ch == ';':
                    while j < n and text[j] != '\n': j += 1
                    continue
                if ch == '"': q = True
                elif ch == '<': depth += 1
                elif ch == '>': depth -= 1
                if ch == '\n' and depth <= 0:
                    j += 1; break
                stmt += ch
                j += 1
            i = j
            st = stmt.strip()
            if not st: continue
            m = re.match(r'COMMENT\s*(\S)', st)
            if m:
                d = m.group(1)
                # skip to the closing delimiter in the original text
                start = text.find('COMMENT', i - len(stmt) - 1)
                k = text.index(d, text.index(d, start) + 1)
                i = text.index('\n', k) + 1 if '\n' in text[k:] else n
                continue
            try:
                self.statement(st)
            except AsmError as e:
                raise AsmError(str(e) + ' | in: ' + st[:200]) from None

    def statement(self, st):
        # labels
        while True:
            m = re.match(r'^([A-Z0-9$%.]+)(::?!?)\s*(.*)$', st, re.S)
            if m and not re.match(r'^[A-Z0-9$%.]+\s*==?', st):
                self.define(m.group(1), self.pc)
                st = m.group(3).strip()
                if not st: return
                continue
            break
        m = re.match(r'^([A-Z0-9$%.]+)\s*(==?)\s*(.*)$', st, re.S)
        if m:
            name = m.group(1)
            v = self.eval(m.group(3))
            self.define(name, v, assign=True)
            return
        if st.startswith('.XCREF') or st.startswith('.CREF'):
            return
        m = re.match(r'^([A-Z][A-Z0-9]*)\s*(.*)$', st, re.S)
        word, rest = (m.group(1), m.group(2).strip()) if m else ('', st)
        if word in ('IFE', 'IFN', 'IFDIF', 'IFIDN'):
            parts = split_args(rest)
            if word in ('IFE', 'IFN'):
                cond, body = parts[0], ','.join(parts[1:])
                v = self.eval(cond)
                ok = (v == 0) if word == 'IFE' else (v != 0)
            else:
                raise AsmError('IFDIF outside DT')
            if ok:
                self.block(strip_brackets(body) + '\n')
            return
        if word in ('IFNDEF', 'IFDEF'):
            parts = split_args(rest)
            name = parts[0].strip()
            ok = (name[:6] in self.syms) == (word == 'IFDEF')
            if ok:
                self.block(strip_brackets(','.join(parts[1:])) + chr(10))
            return
        if word in ('IF1', 'IF2'):
            body = rest[rest.index(',') + 1:]
            if (word == 'IF1') == (self.pas == 1):
                self.block(strip_brackets(body) + '\n')
            return
        if word == 'DEFINE':
            m = re.match(r'^([A-Z0-9]+)\s*(\(([^)]*)\))?\s*,\s*(.*)$', rest, re.S)
            name, args, body = m.group(1), m.group(3), m.group(4)
            if name == 'DT':
                return  # built in
            args = [a.strip() for a in args.split(',')] if args else []
            self.macros[name] = (args, strip_brackets(body))
            return
        if word == 'REPEAT':
            parts = split_args(rest)
            cnt = self.eval(parts[0])
            for _ in range(cnt):
                self.block(strip_brackets(','.join(parts[1:])) + '\n')
            return
        if word in ('TITLE', 'SUBTTL', 'PAGE', 'SEARCH', 'SALL', 'XLIST', 'LIST', 'PRINTX', 'PURGE', 'END', 'LALL', 'XALL'):
            if word == 'END' and rest == '':
                return
            if word in ('END',) and rest:
                return
            return
        if word == 'RADIX':
            self.radix = int(rest.split()[0]); return
        if word == 'WAIT':
            # exactly n cycles, X and the flags lost: LDX #k / DEX / BNE back takes 5k+1.
            n = self.eval(rest)
            if n < 0 or n == 1: raise AsmError('WAIT %d' % n)
            if n == 7:
                self.emit(0xEA, 0xEA, 0x24, 0x00); return
            seq = []
            if n >= 7:
                if n > 1290:
                    # LDY #j / LDX #255 / DEX / BNE / DEY / BNE: j*1281+1, X and Y lost
                    j = min(255, (n - 9) // 1281)
                    self.emit(0xA0, j, 0xA2, 0xFF, 0xCA, 0xD0, 0xFD, 0x88, 0xD0, 0xF8)
                    self._instr = False
                    n -= j * 1281 + 1
                    if n == 0: return
                    if n == 1: raise AsmError('WAIT leaves 1')
                    if n == 7:
                        self.emit(0xEA, 0xEA, 0x24, 0x00); return
                    if n < 7:
                        pad0 = {2: [0xEA], 3: [0x24, 0x00], 4: [0xEA, 0xEA], 5: [0xEA, 0x24, 0x00], 6: [0xEA, 0xEA, 0xEA]}
                        self.emit(*pad0[n]); return
                k = (n - 2) // 5
                r = n - (5 * k + 1)
                if k > 255: raise AsmError('WAIT too long')
                if k > 0:
                    here = self.pc + 2
                    seq = [0xA2, k, 0xCA, 0xD0, 0xFD]
                else:
                    r = n
            else:
                r = n
            pad = {0: [], 1: None, 2: [0xEA], 3: [0x24, 0x00], 4: [0xEA, 0xEA], 5: [0xEA, 0x24, 0x00], 6: [0xEA, 0xEA, 0xEA]}
            if r == 1 or r not in pad:
                # take one loop round off for room: 1 -> 6, and so on
                if seq and seq[1] > 1:
                    seq[1] -= 1; r += 5
                else:
                    raise AsmError('WAIT %d cannot be made' % n)
            self.emit(*(seq + pad[r]))
            return
        if word == 'ORG':
            self.pc = self.eval(rest); return
        if word == 'BLOCK':
            self.pc += self.eval(rest); return
        if word == 'ADR':
            v = self.eval(rest)
            self.emit(v & 0xFF, (v >> 8) & 0xFF); return
        if word == 'EXP':
            for e in split_args(rest): self.emit(self.eval(e) & 0xFF)
            return
        if word == 'XWD':
            a, b = split_args(rest)
            self.emit(self.eval(b) & 0xFF); return
        if word in ('DT', 'DC'):
            s = rest.strip()
            if s.startswith('('): s = s[1:-1].strip()
            s = strip_brackets(s)
            assert s[0] == '"' and s[-1] == '"', s
            bs = [ord(c) for c in s[1:-1]]
            if word == 'DC': bs[-1] |= 0x80
            self.emit(*bs); return
        if word in self.macros and word not in OPS:
            self.expand(word, rest); return
        mn = self.mnemonic(word)
        if mn:
            self.instr(*mn, rest); return
        # bare expressions: a byte each
        for e in split_args(st):
            if e.strip(): self.emit(self.eval(e) & 0xFF)

    def mnemonic(self, w):
        if w in OPS: return (w, '')
        for suf in ('DY', 'DX', 'I', 'D'):
            if w.endswith(suf) and w[:-len(suf)] in OPS:
                return (w[:-len(suf)], suf)
        return None

    def expand(self, name, rest):
        args, body = self.macros[name]
        rest = rest.strip()
        if rest.startswith('(') and args:
            # NAME(args) form
            depth = 0
            for k, ch in enumerate(rest):
                if ch == '(': depth += 1
                elif ch == ')':
                    depth -= 1
                    if depth == 0: break
            inner = rest[1:k]
            actual = split_args(inner)
        else:
            actual = split_args(rest) if rest else []
        actual = [strip_brackets(a.strip()) for a in actual]
        self.gensym += 1
        text = body
        for k, a in enumerate(args):
            v = actual[k] if k < len(actual) else ''
            text = re.sub(r'(?<![A-Z0-9$.])' + re.escape(a) + r'(?![A-Z0-9$.])', lambda _m: v, text)
        text = text.replace('%Q', 'Q%%%d' % self.gensym)
        self.block(text + '\n')

    def instr(self, mn, suf, rest):
        self._instr = True
        modes = OPS[mn]
        rest = rest.strip()
        if suf == 'I':
            self.emit(modes['imm'], self.eval(rest) & 0xFF); return
        if suf == 'DY':
            self.emit(modes['indy'], self.eval(rest) & 0xFF); return
        if suf == 'DX':
            self.emit(modes['indx'], self.eval(rest) & 0xFF); return
        if suf == 'D':
            v = self.eval(rest); self.emit(modes['ind'], v & 0xFF, v >> 8); return
        if 'imp' in modes:
            self.emit(modes['imp']); return
        if rest in ('A,', 'A') and 'acc' in modes:
            self.emit(modes['acc']); return
        if 'rel' in modes:
            v = self.eval(rest)
            off = v - (self.pc + 2)
            if self.pas == 2 and not -128 <= off <= 127:
                raise AsmError('branch out of range at %04X to %04X' % (self.pc, v))
            self.emit(modes['rel'], off & 0xFF); return
        idx = None
        rest = rest.rstrip(',').strip() if re.search(r',\s*[XY]\s*,\s*$', rest) else rest
        m = re.match(r'^(.*),\s*([XY])\s*$', rest, re.S)
        if m:
            rest, idx = m.group(1), m.group(2)
        self.unknown = False
        v = self.eval(rest)
        unk = self.unknown
        if self.pas == 1:
            zp = (not unk) and 0 <= v < 256
            self.modes.append(zp)
        else:
            zp = self.modes[self.modes_i]
        self.modes_i += 1
        key = ('zp' if zp else 'abs') + ((idx or '').lower())
        if key not in modes:
            key = 'abs' + (idx or '').lower()
        if key not in modes:
            raise AsmError('mode %s for %s' % (key, mn))
        if key.startswith('zp'):
            self.emit(modes[key], v & 0xFF)
        else:
            self.emit(modes[key], v & 0xFF, (v >> 8) & 0xFF)

    def emit(self, *bs):
        self.lastop = (bs[0] & 0xFF) if getattr(self, '_instr', False) else None
        self._instr = False
        for b in bs:
            for (frm, to) in self.segments:
                if self.pc == frm:
                    self.pc = to
            if self.pc in self.mem and self.pc >= 0x200:
                raise AsmError('overlap at %04X' % self.pc)
            self.mem[self.pc] = b & 0xFF
            self.pc += 1

    def define(self, name, v, assign=False):
        name = name[:6]
        if not assign and name in self.align and v == self.pc:
            target = self.align[name]
            if self.pc < target:
                if self.lastop not in (None, 0x4C, 0x6C, 0x60, 0x40):
                    self.falls.setdefault(name, (self.pc, target, self.lastop))
                self.pc = v = target
            elif self.pc > target:
                self.late.setdefault(name, (self.pc, target))
        if name in self.overrides:
            v = self.overrides[name]
        if self.pas == 1 and not assign and name in self.labels1 and not name.startswith('Q%'):
            raise AsmError('label defined twice: ' + name)
        if self.pas == 1 and not assign: self.labels1.add(name)
        if self.pas == 2 and not assign and self.syms.get(name) != v and not name.startswith('Q%'):
            raise AsmError('phase error %s %s %s' % (name, self.syms.get(name), v))
        self.syms[name] = v

    # ---- expressions
    def eval(self, s):
        self.toks = self.tokenize(s)
        self.ti = 0
        v = self.e_add()
        if self.ti != len(self.toks):
            raise AsmError('bad expression %r' % s)
        return v

    def tokenize(self, s):
        toks, i = [], 0
        s = s.strip()
        while i < len(s):
            ch = s[i]
            if ch in ' \t\n\r': i += 1; continue
            if ch == '^' and i + 1 < len(s) and s[i + 1] in 'ODBH':
                r = {'O': 8, 'D': 10, 'B': 2, 'H': 16}[s[i + 1]]
                j = i + 2
                while j < len(s) and s[j].isalnum(): j += 1
                toks.append(('n', int(s[i + 2:j], r))); i = j; continue
            if ch == '"':
                j = s.index('"', i + 1)
                v = 0
                for c in s[i + 1:j]: v = (v << 7) | ord(c)
                toks.append(('n', v)); i = j + 1; continue
            if ch.isdigit():
                j = i
                while j < len(s) and s[j].isalnum(): j += 1
                txt = s[i:j]
                i = j
                if i < len(s) and s[i] == '.':
                    toks.append(('n', int(txt, 10))); i += 1
                else:
                    v = 0
                    for c in txt: v = v * self.radix + int(c, 36)
                    toks.append(('n', v))
                continue
            if ch.isalpha() or ch in '$%.':
                j = i
                while j < len(s) and (s[j].isalnum() or s[j] in '$%.'): j += 1
                name = s[i:j]
                if name.endswith('##'): name = name[:-2]
                toks.append(('s', name)); i = j
                if s[i:i + 2] == '##': i += 2
                continue
            toks.append(('o', ch)); i += 1
        return toks

    def peek(self):
        return self.toks[self.ti] if self.ti < len(self.toks) else (None, None)

    def e_add(self):
        v = self.e_mul()
        while self.peek() in (('o', '+'), ('o', '-')):
            o = self.toks[self.ti][1]; self.ti += 1
            w = self.e_mul()
            v = v + w if o == '+' else v - w
        return v

    def e_mul(self):
        v = self.e_log()
        while self.peek() in (('o', '*'), ('o', '/')):
            o = self.toks[self.ti][1]; self.ti += 1
            w = self.e_log()
            v = v * w if o == '*' else (v // w if w else 0)
        return v

    def e_log(self):
        v = self.e_un()
        while self.peek() in (('o', '&'), ('o', '!')):
            o = self.toks[self.ti][1]; self.ti += 1
            w = self.e_un()
            v = v & w if o == '&' else v | w
        return v

    def e_un(self):
        t = self.peek()
        if t == ('o', '-'):
            self.ti += 1; return -self.e_un()
        if t == ('o', '+'):
            self.ti += 1; return self.e_un()
        if t == ('o', '<'):
            self.ti += 1
            v = self.e_add()
            if self.peek() != ('o', '>'): raise AsmError('missing >')
            self.ti += 1
            return v
        if t == ('o', '('):
            self.ti += 1
            v = self.e_add()
            self.ti += 1
            return v
        self.ti += 1
        if t[0] == 'n': return t[1]
        if t[0] == 's':
            if t[1] == '.': return self.pc
            if t[1][:6] in self.syms: return self.syms[t[1][:6]]
            if self.pas == 2: raise AsmError('undefined ' + t[1])
            self.unknown = True
            return 0x1000
        raise AsmError('bad token %r' % (t,))

if __name__ == '__main__':
    pass
