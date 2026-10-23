namespace MagSid.Assembler;

/// <summary>
/// The opcode byte for every (mnemonic, addressing mode) pair this player needs.
///
/// Written out in full for each mnemonic rather than only the modes musicplayer.s happens to use
/// today, because the asymmetries are where a hand-written table goes wrong and they are not
/// guessable: LDX indexes by Y and LDY by X, STX has a zp,y form but no abs,y, STY has zp,x but no
/// abs,x. OpcodeTableTests assembles one instruction per pair below through the real c6510 and
/// compares the bytes, so none of this rests on my recollection of the table.
/// </summary>
internal static class Opcodes
{
    private static readonly Dictionary<(string Mnemonic, AddressMode Mode), byte> Table = new()
    {
        // Load and store. Note LDX/LDY indexing by the opposite register, and that STX/STY have
        // only the one indexed form each.
        { ("lda", AddressMode.Immediate), 0xA9 },
        { ("lda", AddressMode.ZeroPage), 0xA5 },
        { ("lda", AddressMode.ZeroPageX), 0xB5 },
        { ("lda", AddressMode.Absolute), 0xAD },
        { ("lda", AddressMode.AbsoluteX), 0xBD },
        { ("lda", AddressMode.AbsoluteY), 0xB9 },
        { ("lda", AddressMode.IndirectY), 0xB1 },

        { ("ldx", AddressMode.Immediate), 0xA2 },
        { ("ldx", AddressMode.ZeroPage), 0xA6 },
        { ("ldx", AddressMode.ZeroPageY), 0xB6 },
        { ("ldx", AddressMode.Absolute), 0xAE },
        { ("ldx", AddressMode.AbsoluteY), 0xBE },

        { ("ldy", AddressMode.Immediate), 0xA0 },
        { ("ldy", AddressMode.ZeroPage), 0xA4 },
        { ("ldy", AddressMode.ZeroPageX), 0xB4 },
        { ("ldy", AddressMode.Absolute), 0xAC },
        { ("ldy", AddressMode.AbsoluteX), 0xBC },

        { ("sta", AddressMode.ZeroPage), 0x85 },
        { ("sta", AddressMode.ZeroPageX), 0x95 },
        { ("sta", AddressMode.Absolute), 0x8D },
        { ("sta", AddressMode.AbsoluteX), 0x9D },
        { ("sta", AddressMode.AbsoluteY), 0x99 },
        { ("sta", AddressMode.IndirectY), 0x91 },

        { ("stx", AddressMode.ZeroPage), 0x86 },
        { ("stx", AddressMode.ZeroPageY), 0x96 },
        { ("stx", AddressMode.Absolute), 0x8E },

        { ("sty", AddressMode.ZeroPage), 0x84 },
        { ("sty", AddressMode.ZeroPageX), 0x94 },
        { ("sty", AddressMode.Absolute), 0x8C },

        // Arithmetic and logic.
        { ("adc", AddressMode.Immediate), 0x69 },
        { ("adc", AddressMode.ZeroPage), 0x65 },
        { ("adc", AddressMode.ZeroPageX), 0x75 },
        { ("adc", AddressMode.Absolute), 0x6D },
        { ("adc", AddressMode.AbsoluteX), 0x7D },
        { ("adc", AddressMode.AbsoluteY), 0x79 },
        { ("adc", AddressMode.IndirectY), 0x71 },

        { ("and", AddressMode.Immediate), 0x29 },
        { ("and", AddressMode.ZeroPage), 0x25 },
        { ("and", AddressMode.ZeroPageX), 0x35 },
        { ("and", AddressMode.Absolute), 0x2D },
        { ("and", AddressMode.AbsoluteX), 0x3D },
        { ("and", AddressMode.AbsoluteY), 0x39 },
        { ("and", AddressMode.IndirectY), 0x31 },

        { ("ora", AddressMode.Immediate), 0x09 },
        { ("ora", AddressMode.ZeroPage), 0x05 },
        { ("ora", AddressMode.ZeroPageX), 0x15 },
        { ("ora", AddressMode.Absolute), 0x0D },
        { ("ora", AddressMode.AbsoluteX), 0x1D },
        { ("ora", AddressMode.AbsoluteY), 0x19 },
        { ("ora", AddressMode.IndirectY), 0x11 },

        { ("eor", AddressMode.Immediate), 0x49 },
        { ("eor", AddressMode.ZeroPage), 0x45 },
        { ("eor", AddressMode.ZeroPageX), 0x55 },
        { ("eor", AddressMode.Absolute), 0x4D },
        { ("eor", AddressMode.AbsoluteX), 0x5D },
        { ("eor", AddressMode.AbsoluteY), 0x59 },
        { ("eor", AddressMode.IndirectY), 0x51 },

        { ("cmp", AddressMode.Immediate), 0xC9 },
        { ("cmp", AddressMode.ZeroPage), 0xC5 },
        { ("cmp", AddressMode.ZeroPageX), 0xD5 },
        { ("cmp", AddressMode.Absolute), 0xCD },
        { ("cmp", AddressMode.AbsoluteX), 0xDD },
        { ("cmp", AddressMode.AbsoluteY), 0xD9 },
        { ("cmp", AddressMode.IndirectY), 0xD1 },

        // Shifts. The accumulator form is written with no operand at all in the source.
        { ("asl", AddressMode.Accumulator), 0x0A },
        { ("asl", AddressMode.ZeroPage), 0x06 },
        { ("asl", AddressMode.ZeroPageX), 0x16 },
        { ("asl", AddressMode.Absolute), 0x0E },
        { ("asl", AddressMode.AbsoluteX), 0x1E },

        { ("lsr", AddressMode.Accumulator), 0x4A },
        { ("lsr", AddressMode.ZeroPage), 0x46 },
        { ("lsr", AddressMode.ZeroPageX), 0x56 },
        { ("lsr", AddressMode.Absolute), 0x4E },
        { ("lsr", AddressMode.AbsoluteX), 0x5E },

        // Increment and decrement.
        { ("inc", AddressMode.ZeroPage), 0xE6 },
        { ("inc", AddressMode.ZeroPageX), 0xF6 },
        { ("inc", AddressMode.Absolute), 0xEE },
        { ("inc", AddressMode.AbsoluteX), 0xFE },

        { ("dec", AddressMode.ZeroPage), 0xC6 },
        { ("dec", AddressMode.ZeroPageX), 0xD6 },
        { ("dec", AddressMode.Absolute), 0xCE },
        { ("dec", AddressMode.AbsoluteX), 0xDE },

        // ISC (also called ISB/INS) - illegal, and stable: increment memory, then subtract it
        // from A. Saves the separate INC and SBC.
        { ("isc", AddressMode.ZeroPage), 0xE7 },
        { ("isc", AddressMode.ZeroPageX), 0xF7 },
        { ("isc", AddressMode.Absolute), 0xEF },
        { ("isc", AddressMode.AbsoluteX), 0xFF },
        { ("isc", AddressMode.AbsoluteY), 0xFB },
        { ("isc", AddressMode.IndirectY), 0xF3 },

        // ALR - c6510 calls it ASR - illegal, and stable: AND #imm, then LSR A, with the flags LSR
        // leaves. One byte less than the pair.
        { ("asr", AddressMode.Immediate), 0x4B },

        // Jumps. Only the absolute form of JMP is needed; indirect would be a different mode.
        { ("jmp", AddressMode.Absolute), 0x4C },
        { ("jsr", AddressMode.Absolute), 0x20 },

        // Branches.
        { ("bpl", AddressMode.Relative), 0x10 },
        { ("bmi", AddressMode.Relative), 0x30 },
        { ("bcc", AddressMode.Relative), 0x90 },
        { ("bcs", AddressMode.Relative), 0xB0 },
        { ("bne", AddressMode.Relative), 0xD0 },
        { ("beq", AddressMode.Relative), 0xF0 },

        // Implied.
        { ("clc", AddressMode.Implied), 0x18 },
        { ("dex", AddressMode.Implied), 0xCA },
        { ("inx", AddressMode.Implied), 0xE8 },
        { ("iny", AddressMode.Implied), 0xC8 },
        { ("pha", AddressMode.Implied), 0x48 },
        { ("pla", AddressMode.Implied), 0x68 },
        { ("rts", AddressMode.Implied), 0x60 },
        { ("tax", AddressMode.Implied), 0xAA },
        { ("tay", AddressMode.Implied), 0xA8 },
        { ("tya", AddressMode.Implied), 0x98 },
    };

    /// <summary>Every pair in the table, for the test that checks them against c6510.</summary>
    internal static IEnumerable<(string Mnemonic, AddressMode Mode, byte Opcode)> All =>
        Table.Select(e => (e.Key.Mnemonic, e.Key.Mode, e.Value));

    internal static byte? Find(string mnemonic, AddressMode mode) =>
        Table.TryGetValue((mnemonic, mode), out byte opcode) ? opcode : null;
}
