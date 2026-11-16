// Nuked OPL3
// Copyright (C) 2013-2020 Nuke.YKT
//
// This file is part of Nuked OPL3.
//
// Nuked OPL3 is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as
// published by the Free Software Foundation, either version 2.1
// of the License, or (at your option) any later version.
//
// Nuked OPL3 is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public License
// along with Nuked OPL3. If not, see <https://www.gnu.org/licenses/>.
//
// Nuked OPL3 emulator, version 1.8 (commit 765ec96), translated to C# for MagPlaySID: the same
// arithmetic, sample for sample - checked against the C original. Pointers between slots and
// channels are indices into one array of outputs, index 0 being the chip's constant zero.
//
//  Thanks:
//      MAME Development Team(Jarek Burczynski, Tatsuyuki Satoh):
//          Feedback and Rhythm part calculation information.
//      forums.submarine.org.uk(carbon14, opl3):
//          Tremolo and phase generator calculation information.
//      OPLx decapsulated(Matthew Gambrell, Olli Niemitalo):
//          OPL2 ROMs.
//      siliconpr0n.org(John McMaster, digshadow):
//          YMF262 and VRC VII decaps and die shots.

namespace MagPlay.C64;

/// <summary>The YMF262 (OPL3) - and the OPL2 it plays in compatible mode - as Nuked OPL3 emulates it.</summary>
public sealed class NukedOpl3
{
    private const int ChTwoOp = 0, ChFourOp = 1, ChFourOp2 = 2, ChDrum = 3;
    private const int EgkNorm = 0x01, EgkDrum = 0x02;
    private const int EgAttack = 0, EgDecay = 1, EgSustain = 2, EgRelease = 3;
    private const int RsmFrac = 10;

    /// <summary>The chip's native rate: 14.318 MHz over 288.</summary>
    public const int NativeRate = 49716;

    private static readonly ushort[] LogSinRom =
    [
        0x859, 0x6c3, 0x607, 0x58b, 0x52e, 0x4e4, 0x4a6, 0x471, 0x443, 0x41a, 0x3f5, 0x3d3, 0x3b5, 0x398, 0x37e, 0x365,
        0x34e, 0x339, 0x324, 0x311, 0x2ff, 0x2ed, 0x2dc, 0x2cd, 0x2bd, 0x2af, 0x2a0, 0x293, 0x286, 0x279, 0x26d, 0x261,
        0x256, 0x24b, 0x240, 0x236, 0x22c, 0x222, 0x218, 0x20f, 0x206, 0x1fd, 0x1f5, 0x1ec, 0x1e4, 0x1dc, 0x1d4, 0x1cd,
        0x1c5, 0x1be, 0x1b7, 0x1b0, 0x1a9, 0x1a2, 0x19b, 0x195, 0x18f, 0x188, 0x182, 0x17c, 0x177, 0x171, 0x16b, 0x166,
        0x160, 0x15b, 0x155, 0x150, 0x14b, 0x146, 0x141, 0x13c, 0x137, 0x133, 0x12e, 0x129, 0x125, 0x121, 0x11c, 0x118,
        0x114, 0x10f, 0x10b, 0x107, 0x103, 0x0ff, 0x0fb, 0x0f8, 0x0f4, 0x0f0, 0x0ec, 0x0e9, 0x0e5, 0x0e2, 0x0de, 0x0db,
        0x0d7, 0x0d4, 0x0d1, 0x0cd, 0x0ca, 0x0c7, 0x0c4, 0x0c1, 0x0be, 0x0bb, 0x0b8, 0x0b5, 0x0b2, 0x0af, 0x0ac, 0x0a9,
        0x0a7, 0x0a4, 0x0a1, 0x09f, 0x09c, 0x099, 0x097, 0x094, 0x092, 0x08f, 0x08d, 0x08a, 0x088, 0x086, 0x083, 0x081,
        0x07f, 0x07d, 0x07a, 0x078, 0x076, 0x074, 0x072, 0x070, 0x06e, 0x06c, 0x06a, 0x068, 0x066, 0x064, 0x062, 0x060,
        0x05e, 0x05c, 0x05b, 0x059, 0x057, 0x055, 0x053, 0x052, 0x050, 0x04e, 0x04d, 0x04b, 0x04a, 0x048, 0x046, 0x045,
        0x043, 0x042, 0x040, 0x03f, 0x03e, 0x03c, 0x03b, 0x039, 0x038, 0x037, 0x035, 0x034, 0x033, 0x031, 0x030, 0x02f,
        0x02e, 0x02d, 0x02b, 0x02a, 0x029, 0x028, 0x027, 0x026, 0x025, 0x024, 0x023, 0x022, 0x021, 0x020, 0x01f, 0x01e,
        0x01d, 0x01c, 0x01b, 0x01a, 0x019, 0x018, 0x017, 0x017, 0x016, 0x015, 0x014, 0x014, 0x013, 0x012, 0x011, 0x011,
        0x010, 0x00f, 0x00f, 0x00e, 0x00d, 0x00d, 0x00c, 0x00c, 0x00b, 0x00a, 0x00a, 0x009, 0x009, 0x008, 0x008, 0x007,
        0x007, 0x007, 0x006, 0x006, 0x005, 0x005, 0x005, 0x004, 0x004, 0x004, 0x003, 0x003, 0x003, 0x002, 0x002, 0x002,
        0x002, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000,
    ];

    private static readonly ushort[] ExpRom =
    [
        0x7fa, 0x7f5, 0x7ef, 0x7ea, 0x7e4, 0x7df, 0x7da, 0x7d4, 0x7cf, 0x7c9, 0x7c4, 0x7bf, 0x7b9, 0x7b4, 0x7ae, 0x7a9,
        0x7a4, 0x79f, 0x799, 0x794, 0x78f, 0x78a, 0x784, 0x77f, 0x77a, 0x775, 0x770, 0x76a, 0x765, 0x760, 0x75b, 0x756,
        0x751, 0x74c, 0x747, 0x742, 0x73d, 0x738, 0x733, 0x72e, 0x729, 0x724, 0x71f, 0x71a, 0x715, 0x710, 0x70b, 0x706,
        0x702, 0x6fd, 0x6f8, 0x6f3, 0x6ee, 0x6e9, 0x6e5, 0x6e0, 0x6db, 0x6d6, 0x6d2, 0x6cd, 0x6c8, 0x6c4, 0x6bf, 0x6ba,
        0x6b5, 0x6b1, 0x6ac, 0x6a8, 0x6a3, 0x69e, 0x69a, 0x695, 0x691, 0x68c, 0x688, 0x683, 0x67f, 0x67a, 0x676, 0x671,
        0x66d, 0x668, 0x664, 0x65f, 0x65b, 0x657, 0x652, 0x64e, 0x649, 0x645, 0x641, 0x63c, 0x638, 0x634, 0x630, 0x62b,
        0x627, 0x623, 0x61e, 0x61a, 0x616, 0x612, 0x60e, 0x609, 0x605, 0x601, 0x5fd, 0x5f9, 0x5f5, 0x5f0, 0x5ec, 0x5e8,
        0x5e4, 0x5e0, 0x5dc, 0x5d8, 0x5d4, 0x5d0, 0x5cc, 0x5c8, 0x5c4, 0x5c0, 0x5bc, 0x5b8, 0x5b4, 0x5b0, 0x5ac, 0x5a8,
        0x5a4, 0x5a0, 0x59c, 0x599, 0x595, 0x591, 0x58d, 0x589, 0x585, 0x581, 0x57e, 0x57a, 0x576, 0x572, 0x56f, 0x56b,
        0x567, 0x563, 0x560, 0x55c, 0x558, 0x554, 0x551, 0x54d, 0x549, 0x546, 0x542, 0x53e, 0x53b, 0x537, 0x534, 0x530,
        0x52c, 0x529, 0x525, 0x522, 0x51e, 0x51b, 0x517, 0x514, 0x510, 0x50c, 0x509, 0x506, 0x502, 0x4ff, 0x4fb, 0x4f8,
        0x4f4, 0x4f1, 0x4ed, 0x4ea, 0x4e7, 0x4e3, 0x4e0, 0x4dc, 0x4d9, 0x4d6, 0x4d2, 0x4cf, 0x4cc, 0x4c8, 0x4c5, 0x4c2,
        0x4be, 0x4bb, 0x4b8, 0x4b5, 0x4b1, 0x4ae, 0x4ab, 0x4a8, 0x4a4, 0x4a1, 0x49e, 0x49b, 0x498, 0x494, 0x491, 0x48e,
        0x48b, 0x488, 0x485, 0x482, 0x47e, 0x47b, 0x478, 0x475, 0x472, 0x46f, 0x46c, 0x469, 0x466, 0x463, 0x460, 0x45d,
        0x45a, 0x457, 0x454, 0x451, 0x44e, 0x44b, 0x448, 0x445, 0x442, 0x43f, 0x43c, 0x439, 0x436, 0x433, 0x430, 0x42d,
        0x42a, 0x428, 0x425, 0x422, 0x41f, 0x41c, 0x419, 0x416, 0x414, 0x411, 0x40e, 0x40b, 0x408, 0x406, 0x403, 0x400,
    ];

    /// <summary>The frequency multipliers, doubled: 1/2, 1, 2, ... 15.</summary>
    private static readonly byte[] Mt = [1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30];

    private static readonly byte[] KslRom = [0, 32, 40, 45, 48, 51, 53, 55, 56, 58, 59, 60, 61, 62, 63, 64];

    private static readonly byte[] KslShift = [8, 1, 2, 0];

    private static readonly byte[,] EgIncStep = { { 0, 0, 0, 0 }, { 1, 0, 0, 0 }, { 1, 0, 1, 0 }, { 1, 1, 1, 0 } };

    private static readonly sbyte[] AdSlot =
    [
        0, 1, 2, 3, 4, 5, -1, -1, 6, 7, 8, 9, 10, 11, -1, -1,
        12, 13, 14, 15, 16, 17, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
    ];

    private static readonly byte[] ChSlot = [0, 1, 2, 6, 7, 8, 12, 13, 14, 18, 19, 20, 24, 25, 26, 30, 31, 32];

    private sealed class Slot
    {
        public Channel Channel = null!;
        public int Out;     // index of out in Signals
        public int FbMod;   // index of fbmod in Signals
        public int Mod;     // index the modulation is read from
        public short Prout;
        public ushort EgRout;
        public ushort EgOut;
        public byte EgGen;
        public byte EgKsl;
        public bool Trem;   // tremolo, or the chip's zero
        public byte RegVib, RegType, RegKsr, RegMult, RegKsl, RegTl, RegAr, RegDr, RegSl, RegRr, RegWf;
        public byte Key;
        public uint PgReset;
        public uint PgPhase;
        public ushort PgPhaseOut;
        public byte SlotNum;
    }

    private sealed class Channel
    {
        public Slot[] Slots = new Slot[2];
        public Channel? Pair;
        public int[] Out = new int[4];
        public byte ChType;
        public ushort FNum, FNumReg;
        public byte Block, BlockReg;
        public byte Fb, Con, Alg, Ksv;
        public ushort Cha, Chb, Chc, Chd;
        public byte ChNum;
    }

    /// <summary>Every slot's out and fbmod, and the chip's zero at 0: what slots and channels point at.</summary>
    private readonly short[] _signals = new short[1 + (36 * 2)];
    private readonly Channel[] _channel = new Channel[18];
    private readonly Slot[] _slot = new Slot[36];
    private ushort _timer;
    private ulong _egTimer;
    private byte _egTimerRem, _egState, _egAdd, _egTimerLo;
    private byte _newm, _nts, _rhy, _vibPos, _vibShift, _tremolo, _tremoloPos, _tremoloShift;
    private uint _noise;
    private readonly int[] _mixBuff = new int[4];
    private byte _rmHhBit2, _rmHhBit3, _rmHhBit7, _rmHhBit8, _rmTcBit3, _rmTcBit5;
    private int _rateRatio, _sampleCnt;
    private readonly short[] _oldSamples = new short[4];
    private readonly short[] _samples = new short[4];

    private const int Zero = 0;

    public NukedOpl3(int sampleRate) => Reset(sampleRate);

    // ------------------------------------------------------------------ envelope generator

    private static short CalcExp(uint level)
    {
        if (level > 0x1fff)
            level = 0x1fff;
        return (short)((ExpRom[level & 0xff] << 1) >> (int)(level >> 8));
    }

    private static short CalcSin(int wf, ushort phase, ushort envelope)
    {
        ushort output;
        int neg = 0;
        phase &= 0x3ff;
        switch (wf)
        {
            case 0:
                if ((phase & 0x200) != 0)
                    neg = 0xffff;
                output = (phase & 0x100) != 0 ? LogSinRom[(phase & 0xff) ^ 0xff] : LogSinRom[phase & 0xff];
                return (short)(CalcExp((uint)(output + (envelope << 3))) ^ neg);
            case 1:
                output = (phase & 0x200) != 0 ? (ushort)0x1000
                    : (phase & 0x100) != 0 ? LogSinRom[(phase & 0xff) ^ 0xff] : LogSinRom[phase & 0xff];
                return CalcExp((uint)(output + (envelope << 3)));
            case 2:
                output = (phase & 0x100) != 0 ? LogSinRom[(phase & 0xff) ^ 0xff] : LogSinRom[phase & 0xff];
                return CalcExp((uint)(output + (envelope << 3)));
            case 3:
                output = (phase & 0x100) != 0 ? (ushort)0x1000 : LogSinRom[phase & 0xff];
                return CalcExp((uint)(output + (envelope << 3)));
            case 4:
                if ((phase & 0x300) == 0x100)
                    neg = 0xffff;
                output = (phase & 0x200) != 0 ? (ushort)0x1000
                    : (phase & 0x80) != 0 ? LogSinRom[((phase ^ 0xff) << 1) & 0xff] : LogSinRom[(phase << 1) & 0xff];
                return (short)(CalcExp((uint)(output + (envelope << 3))) ^ neg);
            case 5:
                output = (phase & 0x200) != 0 ? (ushort)0x1000
                    : (phase & 0x80) != 0 ? LogSinRom[((phase ^ 0xff) << 1) & 0xff] : LogSinRom[(phase << 1) & 0xff];
                return CalcExp((uint)(output + (envelope << 3)));
            case 6:
                if ((phase & 0x200) != 0)
                    neg = 0xffff;
                return (short)(CalcExp((uint)(envelope << 3)) ^ neg);
            default:
                if ((phase & 0x200) != 0)
                {
                    neg = 0xffff;
                    phase = (ushort)((phase & 0x1ff) ^ 0x1ff);
                }
                output = (ushort)(phase << 3);
                return (short)(CalcExp((uint)(output + (envelope << 3))) ^ neg);
        }
    }

    private static void EnvelopeUpdateKsl(Slot slot)
    {
        short ksl = (short)((KslRom[slot.Channel.FNum >> 6] << 2) - ((0x08 - slot.Channel.Block) << 5));
        if (ksl < 0)
            ksl = 0;
        slot.EgKsl = (byte)ksl;
    }

    private void EnvelopeCalc(Slot slot)
    {
        byte regRate = 0;
        byte reset = 0;
        slot.EgOut = (ushort)(slot.EgRout + (slot.RegTl << 2) + (slot.EgKsl >> KslShift[slot.RegKsl]) + (slot.Trem ? _tremolo : 0));
        if (slot.Key != 0 && slot.EgGen == EgRelease)
        {
            reset = 1;
            regRate = slot.RegAr;
        }
        else
        {
            switch (slot.EgGen)
            {
                case EgAttack: regRate = slot.RegAr; break;
                case EgDecay: regRate = slot.RegDr; break;
                case EgSustain:
                    if (slot.RegType == 0)
                        regRate = slot.RegRr;
                    break;
                case EgRelease: regRate = slot.RegRr; break;
            }
        }
        slot.PgReset = reset;
        byte ks = (byte)(slot.Channel.Ksv >> ((slot.RegKsr ^ 1) << 1));
        bool nonzero = regRate != 0;
        byte rate = (byte)(ks + (regRate << 2));
        byte rateHi = (byte)(rate >> 2);
        byte rateLo = (byte)(rate & 0x03);
        if ((rateHi & 0x10) != 0)
            rateHi = 0x0f;
        byte egShift = (byte)(rateHi + _egAdd);
        byte shift = 0;
        if (nonzero)
        {
            if (rateHi < 12)
            {
                if (_egState != 0)
                {
                    switch (egShift)
                    {
                        case 12: shift = 1; break;
                        case 13: shift = (byte)((rateLo >> 1) & 0x01); break;
                        case 14: shift = (byte)(rateLo & 0x01); break;
                    }
                }
            }
            else
            {
                shift = (byte)((rateHi & 0x03) + EgIncStep[rateLo, _egTimerLo]);
                if ((shift & 0x04) != 0)
                    shift = 0x03;
                if (shift == 0)
                    shift = _egState;
            }
        }
        ushort egRout = slot.EgRout;
        short egInc = 0;
        bool egOff = false;
        // Instant attack
        if (reset != 0 && rateHi == 0x0f)
            egRout = 0x00;
        // Envelope off
        if ((slot.EgRout & 0x1f8) == 0x1f8)
            egOff = true;
        if (slot.EgGen != EgAttack && reset == 0 && egOff)
            egRout = 0x1ff;
        switch (slot.EgGen)
        {
            case EgAttack:
                if (slot.EgRout == 0)
                    slot.EgGen = EgDecay;
                else if (slot.Key != 0 && shift > 0 && rateHi != 0x0f)
                    egInc = (short)(~slot.EgRout >> (4 - shift));
                break;
            case EgDecay:
                if ((slot.EgRout >> 4) == slot.RegSl)
                    slot.EgGen = EgSustain;
                else if (!egOff && reset == 0 && shift > 0)
                    egInc = (short)(1 << (shift - 1));
                break;
            case EgSustain:
            case EgRelease:
                if (!egOff && reset == 0 && shift > 0)
                    egInc = (short)(1 << (shift - 1));
                break;
        }
        slot.EgRout = (ushort)((egRout + egInc) & 0x1ff);
        // Key off
        if (reset != 0)
            slot.EgGen = EgAttack;
        if (slot.Key == 0)
            slot.EgGen = EgRelease;
    }

    private static void EnvelopeKeyOn(Slot slot, byte type) => slot.Key |= type;

    private static void EnvelopeKeyOff(Slot slot, byte type) => slot.Key &= (byte)~type;

    // ------------------------------------------------------------------ phase generator

    private void PhaseGenerate(Slot slot)
    {
        ushort fNum = slot.Channel.FNum;
        if (slot.RegVib != 0)
        {
            sbyte range = (sbyte)((fNum >> 7) & 7);
            byte vibPos = _vibPos;
            if ((vibPos & 3) == 0)
                range = 0;
            else if ((vibPos & 1) != 0)
                range >>= 1;
            range >>= _vibShift;
            if ((vibPos & 4) != 0)
                range = (sbyte)-range;
            fNum = (ushort)(fNum + range);
        }
        uint baseFreq = (uint)(fNum << slot.Channel.Block) >> 1;
        ushort phase = (ushort)(slot.PgPhase >> 9);
        if (slot.PgReset != 0)
            slot.PgPhase = 0;
        slot.PgPhase += (baseFreq * Mt[slot.RegMult]) >> 1;
        // Rhythm mode
        uint noise = _noise;
        slot.PgPhaseOut = phase;
        if (slot.SlotNum == 13) // hh
        {
            _rmHhBit2 = (byte)((phase >> 2) & 1);
            _rmHhBit3 = (byte)((phase >> 3) & 1);
            _rmHhBit7 = (byte)((phase >> 7) & 1);
            _rmHhBit8 = (byte)((phase >> 8) & 1);
        }
        if (slot.SlotNum == 17 && (_rhy & 0x20) != 0) // tc
        {
            _rmTcBit3 = (byte)((phase >> 3) & 1);
            _rmTcBit5 = (byte)((phase >> 5) & 1);
        }
        if ((_rhy & 0x20) != 0)
        {
            byte rmXor = (byte)((_rmHhBit2 ^ _rmHhBit7) | (_rmHhBit3 ^ _rmTcBit5) | (_rmTcBit3 ^ _rmTcBit5));
            switch (slot.SlotNum)
            {
                case 13: // hh
                    slot.PgPhaseOut = (ushort)(rmXor << 9);
                    slot.PgPhaseOut |= (ushort)(((rmXor ^ (noise & 1)) != 0) ? 0xd0 : 0x34);
                    break;
                case 16: // sd
                    slot.PgPhaseOut = (ushort)((_rmHhBit8 << 9) | ((_rmHhBit8 ^ (noise & 1)) << 8));
                    break;
                case 17: // tc
                    slot.PgPhaseOut = (ushort)((rmXor << 9) | 0x80);
                    break;
            }
        }
        uint nBit = ((noise >> 14) ^ noise) & 0x01;
        _noise = (noise >> 1) | (nBit << 22);
    }

    // ------------------------------------------------------------------ slot

    private static void SlotWrite20(Slot slot, byte data)
    {
        slot.Trem = ((data >> 7) & 0x01) != 0;
        slot.RegVib = (byte)((data >> 6) & 0x01);
        slot.RegType = (byte)((data >> 5) & 0x01);
        slot.RegKsr = (byte)((data >> 4) & 0x01);
        slot.RegMult = (byte)(data & 0x0f);
    }

    private static void SlotWrite40(Slot slot, byte data)
    {
        slot.RegKsl = (byte)((data >> 6) & 0x03);
        slot.RegTl = (byte)(data & 0x3f);
        EnvelopeUpdateKsl(slot);
    }

    private static void SlotWrite60(Slot slot, byte data)
    {
        slot.RegAr = (byte)((data >> 4) & 0x0f);
        slot.RegDr = (byte)(data & 0x0f);
    }

    private static void SlotWrite80(Slot slot, byte data)
    {
        slot.RegSl = (byte)((data >> 4) & 0x0f);
        if (slot.RegSl == 0x0f)
            slot.RegSl = 0x1f;
        slot.RegRr = (byte)(data & 0x0f);
    }

    private void SlotWriteE0(Slot slot, byte data)
    {
        slot.RegWf = (byte)(data & 0x07);
        if (_newm == 0x00)
            slot.RegWf &= 0x03;
    }

    private void SlotGenerate(Slot slot) =>
        _signals[slot.Out] = CalcSin(slot.RegWf, (ushort)(slot.PgPhaseOut + _signals[slot.Mod]), slot.EgOut);

    private void SlotCalcFb(Slot slot)
    {
        short output = _signals[slot.Out];
        _signals[slot.FbMod] = slot.Channel.Fb != 0x00 ? (short)((slot.Prout + output) >> (0x09 - slot.Channel.Fb)) : (short)0;
        slot.Prout = output;
    }

    // ------------------------------------------------------------------ channel

    private void ChannelUpdateRhythm(byte data)
    {
        _rhy = (byte)(data & 0x3f);
        if ((_rhy & 0x20) != 0)
        {
            var channel6 = _channel[6];
            var channel7 = _channel[7];
            var channel8 = _channel[8];
            channel6.Out[0] = channel6.Slots[1].Out;
            channel6.Out[1] = channel6.Slots[1].Out;
            channel6.Out[2] = Zero;
            channel6.Out[3] = Zero;
            channel7.Out[0] = channel7.Slots[0].Out;
            channel7.Out[1] = channel7.Slots[0].Out;
            channel7.Out[2] = channel7.Slots[1].Out;
            channel7.Out[3] = channel7.Slots[1].Out;
            channel8.Out[0] = channel8.Slots[0].Out;
            channel8.Out[1] = channel8.Slots[0].Out;
            channel8.Out[2] = channel8.Slots[1].Out;
            channel8.Out[3] = channel8.Slots[1].Out;
            for (int chnum = 6; chnum < 9; chnum++)
                _channel[chnum].ChType = ChDrum;
            ChannelSetupAlg(channel6);
            ChannelSetupAlg(channel7);
            ChannelSetupAlg(channel8);
            // hh
            if ((_rhy & 0x01) != 0)
                EnvelopeKeyOn(channel7.Slots[0], EgkDrum);
            else
                EnvelopeKeyOff(channel7.Slots[0], EgkDrum);
            // tc
            if ((_rhy & 0x02) != 0)
                EnvelopeKeyOn(channel8.Slots[1], EgkDrum);
            else
                EnvelopeKeyOff(channel8.Slots[1], EgkDrum);
            // tom
            if ((_rhy & 0x04) != 0)
                EnvelopeKeyOn(channel8.Slots[0], EgkDrum);
            else
                EnvelopeKeyOff(channel8.Slots[0], EgkDrum);
            // sd
            if ((_rhy & 0x08) != 0)
                EnvelopeKeyOn(channel7.Slots[1], EgkDrum);
            else
                EnvelopeKeyOff(channel7.Slots[1], EgkDrum);
            // bd
            if ((_rhy & 0x10) != 0)
            {
                EnvelopeKeyOn(channel6.Slots[0], EgkDrum);
                EnvelopeKeyOn(channel6.Slots[1], EgkDrum);
            }
            else
            {
                EnvelopeKeyOff(channel6.Slots[0], EgkDrum);
                EnvelopeKeyOff(channel6.Slots[1], EgkDrum);
            }
        }
        else
        {
            for (int chnum = 6; chnum < 9; chnum++)
            {
                _channel[chnum].ChType = ChTwoOp;
                ChannelSetupAlg(_channel[chnum]);
                EnvelopeKeyOff(_channel[chnum].Slots[0], EgkDrum);
                EnvelopeKeyOff(_channel[chnum].Slots[1], EgkDrum);
            }
        }
    }

    private void ChannelUpdateFrequency(Channel channel)
    {
        channel.Ksv = (byte)((channel.Block << 1) | ((channel.FNum >> (0x09 - _nts)) & 0x01));
        EnvelopeUpdateKsl(channel.Slots[0]);
        EnvelopeUpdateKsl(channel.Slots[1]);
    }

    private void ChannelRestoreFrequency(Channel channel)
    {
        channel.FNum = channel.FNumReg;
        channel.Block = channel.BlockReg;
        ChannelUpdateFrequency(channel);
    }

    private void ChannelSync4Op(Channel channel)
    {
        channel.Pair!.FNum = channel.FNum;
        channel.Pair.Block = channel.Block;
        ChannelUpdateFrequency(channel.Pair);
    }

    private void ChannelWriteA0(Channel channel, byte data)
    {
        channel.FNumReg = (ushort)((channel.FNumReg & 0x300) | data);
        if (channel.ChType == ChFourOp2)
            return;
        ChannelRestoreFrequency(channel);
        if (channel.ChType == ChFourOp)
            ChannelSync4Op(channel);
    }

    private void ChannelWriteB0(Channel channel, byte data)
    {
        channel.FNumReg = (ushort)((channel.FNumReg & 0xff) | ((data & 0x03) << 8));
        channel.BlockReg = (byte)((data >> 2) & 0x07);
        if (channel.ChType == ChFourOp2)
            return;
        ChannelRestoreFrequency(channel);
        if (channel.ChType == ChFourOp)
            ChannelSync4Op(channel);
    }

    private static void ChannelSetupAlg(Channel channel)
    {
        if (channel.ChType == ChDrum)
        {
            if (channel.ChNum == 7 || channel.ChNum == 8)
            {
                channel.Slots[0].Mod = Zero;
                channel.Slots[1].Mod = Zero;
                return;
            }
            switch (channel.Alg & 0x01)
            {
                case 0x00:
                    channel.Slots[0].Mod = channel.Slots[0].FbMod;
                    channel.Slots[1].Mod = channel.Slots[0].Out;
                    break;
                case 0x01:
                    channel.Slots[0].Mod = channel.Slots[0].FbMod;
                    channel.Slots[1].Mod = Zero;
                    break;
            }
            return;
        }
        if ((channel.Alg & 0x08) != 0)
            return;
        if ((channel.Alg & 0x04) != 0)
        {
            var pair = channel.Pair!;
            pair.Out[0] = Zero;
            pair.Out[1] = Zero;
            pair.Out[2] = Zero;
            pair.Out[3] = Zero;
            switch (channel.Alg & 0x03)
            {
                case 0x00:
                    pair.Slots[0].Mod = pair.Slots[0].FbMod;
                    pair.Slots[1].Mod = pair.Slots[0].Out;
                    channel.Slots[0].Mod = pair.Slots[1].Out;
                    channel.Slots[1].Mod = channel.Slots[0].Out;
                    channel.Out[0] = channel.Slots[1].Out;
                    channel.Out[1] = Zero;
                    channel.Out[2] = Zero;
                    channel.Out[3] = Zero;
                    break;
                case 0x01:
                    pair.Slots[0].Mod = pair.Slots[0].FbMod;
                    pair.Slots[1].Mod = pair.Slots[0].Out;
                    channel.Slots[0].Mod = Zero;
                    channel.Slots[1].Mod = channel.Slots[0].Out;
                    channel.Out[0] = pair.Slots[1].Out;
                    channel.Out[1] = channel.Slots[1].Out;
                    channel.Out[2] = Zero;
                    channel.Out[3] = Zero;
                    break;
                case 0x02:
                    pair.Slots[0].Mod = pair.Slots[0].FbMod;
                    pair.Slots[1].Mod = Zero;
                    channel.Slots[0].Mod = pair.Slots[1].Out;
                    channel.Slots[1].Mod = channel.Slots[0].Out;
                    channel.Out[0] = pair.Slots[0].Out;
                    channel.Out[1] = channel.Slots[1].Out;
                    channel.Out[2] = Zero;
                    channel.Out[3] = Zero;
                    break;
                case 0x03:
                    pair.Slots[0].Mod = pair.Slots[0].FbMod;
                    pair.Slots[1].Mod = Zero;
                    channel.Slots[0].Mod = pair.Slots[1].Out;
                    channel.Slots[1].Mod = Zero;
                    channel.Out[0] = pair.Slots[0].Out;
                    channel.Out[1] = channel.Slots[0].Out;
                    channel.Out[2] = channel.Slots[1].Out;
                    channel.Out[3] = Zero;
                    break;
            }
        }
        else
        {
            switch (channel.Alg & 0x01)
            {
                case 0x00:
                    channel.Slots[0].Mod = channel.Slots[0].FbMod;
                    channel.Slots[1].Mod = channel.Slots[0].Out;
                    channel.Out[0] = channel.Slots[1].Out;
                    channel.Out[1] = Zero;
                    channel.Out[2] = Zero;
                    channel.Out[3] = Zero;
                    break;
                case 0x01:
                    channel.Slots[0].Mod = channel.Slots[0].FbMod;
                    channel.Slots[1].Mod = Zero;
                    channel.Out[0] = channel.Slots[0].Out;
                    channel.Out[1] = channel.Slots[1].Out;
                    channel.Out[2] = Zero;
                    channel.Out[3] = Zero;
                    break;
            }
        }
    }

    private static void ChannelUpdateAlg(Channel channel)
    {
        channel.Alg = channel.Con;
        if (channel.ChType == ChFourOp)
        {
            channel.Pair!.Alg = (byte)(0x04 | (channel.Con << 1) | channel.Pair.Con);
            channel.Alg = 0x08;
            ChannelSetupAlg(channel.Pair);
        }
        else if (channel.ChType == ChFourOp2)
        {
            channel.Alg = (byte)(0x04 | (channel.Pair!.Con << 1) | channel.Con);
            channel.Pair.Alg = 0x08;
            ChannelSetupAlg(channel);
        }
        else
        {
            ChannelSetupAlg(channel);
        }
    }

    private void ChannelWriteC0(Channel channel, byte data)
    {
        channel.Fb = (byte)((data & 0x0e) >> 1);
        channel.Con = (byte)(data & 0x01);
        ChannelUpdateAlg(channel);
        if (_newm != 0)
        {
            channel.Cha = ((data >> 4) & 0x01) != 0 ? (ushort)0xffff : (ushort)0;
            channel.Chb = ((data >> 5) & 0x01) != 0 ? (ushort)0xffff : (ushort)0;
            channel.Chc = ((data >> 6) & 0x01) != 0 ? (ushort)0xffff : (ushort)0;
            channel.Chd = ((data >> 7) & 0x01) != 0 ? (ushort)0xffff : (ushort)0;
        }
        else
        {
            channel.Cha = channel.Chb = 0xffff;
            channel.Chc = channel.Chd = 0;
        }
    }

    private static void ChannelKeyOn(Channel channel)
    {
        if (channel.ChType == ChFourOp)
        {
            EnvelopeKeyOn(channel.Slots[0], EgkNorm);
            EnvelopeKeyOn(channel.Slots[1], EgkNorm);
            EnvelopeKeyOn(channel.Pair!.Slots[0], EgkNorm);
            EnvelopeKeyOn(channel.Pair.Slots[1], EgkNorm);
        }
        else if (channel.ChType == ChTwoOp || channel.ChType == ChDrum)
        {
            EnvelopeKeyOn(channel.Slots[0], EgkNorm);
            EnvelopeKeyOn(channel.Slots[1], EgkNorm);
        }
    }

    private static void ChannelKeyOff(Channel channel)
    {
        if (channel.ChType == ChFourOp)
        {
            EnvelopeKeyOff(channel.Slots[0], EgkNorm);
            EnvelopeKeyOff(channel.Slots[1], EgkNorm);
            EnvelopeKeyOff(channel.Pair!.Slots[0], EgkNorm);
            EnvelopeKeyOff(channel.Pair.Slots[1], EgkNorm);
        }
        else if (channel.ChType == ChTwoOp || channel.ChType == ChDrum)
        {
            EnvelopeKeyOff(channel.Slots[0], EgkNorm);
            EnvelopeKeyOff(channel.Slots[1], EgkNorm);
        }
    }

    private void ChannelSet4Op(byte data)
    {
        for (int bit = 0; bit < 6; bit++)
        {
            int chnum = bit;
            if (bit >= 3)
                chnum += 9 - 3;
            if (((data >> bit) & 0x01) != 0)
            {
                _channel[chnum].ChType = ChFourOp;
                _channel[chnum + 3].ChType = ChFourOp2;
                ChannelSync4Op(_channel[chnum]);
                ChannelUpdateAlg(_channel[chnum]);
            }
            else
            {
                _channel[chnum].ChType = ChTwoOp;
                _channel[chnum + 3].ChType = ChTwoOp;
                ChannelRestoreFrequency(_channel[chnum + 3]);
                ChannelUpdateAlg(_channel[chnum]);
                ChannelUpdateAlg(_channel[chnum + 3]);
            }
        }
    }

    private static short ClipSample(int sample) => (short)Math.Clamp(sample, short.MinValue, short.MaxValue);

    private void ProcessSlot(Slot slot)
    {
        SlotCalcFb(slot);
        EnvelopeCalc(slot);
        PhaseGenerate(slot);
        SlotGenerate(slot);
    }

    private short Accumulate(Channel channel) =>
        (short)(_signals[channel.Out[0]] + _signals[channel.Out[1]] + _signals[channel.Out[2]] + _signals[channel.Out[3]]);

    /// <summary>One sample at the chip's own rate: its four outputs - A, B, C, D - into <paramref name="buf4"/>.</summary>
    public void Generate4Ch(Span<short> buf4)
    {
        buf4[1] = ClipSample(_mixBuff[1]);
        buf4[3] = ClipSample(_mixBuff[3]);

        // Quirk: some FM channels are output one sample later on the left side than the right.
        for (int ii = 0; ii < 15; ii++)
            ProcessSlot(_slot[ii]);

        int mix0 = 0, mix1 = 0;
        for (int ii = 0; ii < 18; ii++)
        {
            var channel = _channel[ii];
            short accm = Accumulate(channel);
            mix0 += (short)(accm & channel.Cha);
            mix1 += (short)(accm & channel.Chc);
        }
        _mixBuff[0] = mix0;
        _mixBuff[2] = mix1;

        for (int ii = 15; ii < 18; ii++)
            ProcessSlot(_slot[ii]);

        buf4[0] = ClipSample(_mixBuff[0]);
        buf4[2] = ClipSample(_mixBuff[2]);

        for (int ii = 18; ii < 33; ii++)
            ProcessSlot(_slot[ii]);

        mix0 = mix1 = 0;
        for (int ii = 0; ii < 18; ii++)
        {
            var channel = _channel[ii];
            short accm = Accumulate(channel);
            mix0 += (short)(accm & channel.Chb);
            mix1 += (short)(accm & channel.Chd);
        }
        _mixBuff[1] = mix0;
        _mixBuff[3] = mix1;

        for (int ii = 33; ii < 36; ii++)
            ProcessSlot(_slot[ii]);

        if ((_timer & 0x3f) == 0x3f)
            _tremoloPos = (byte)((_tremoloPos + 1) % 210);
        _tremolo = _tremoloPos < 105 ? (byte)(_tremoloPos >> _tremoloShift) : (byte)((210 - _tremoloPos) >> _tremoloShift);

        if ((_timer & 0x3ff) == 0x3ff)
            _vibPos = (byte)((_vibPos + 1) & 7);

        _timer++;

        if (_egState != 0)
        {
            int shift = 0;
            while (shift < 13 && ((_egTimer >> shift) & 1) == 0)
                shift++;
            _egAdd = shift > 12 ? (byte)0 : (byte)(shift + 1);
            _egTimerLo = (byte)(_egTimer & 0x3u);
        }

        if (_egTimerRem != 0 || _egState != 0)
        {
            if (_egTimer == 0xfffffffffUL)
            {
                _egTimer = 0;
                _egTimerRem = 1;
            }
            else
            {
                _egTimer++;
                _egTimerRem = 0;
            }
        }

        _egState ^= 1;
    }

    /// <summary>One sample at the rate the chip was reset for, made from its own by straight lines
    /// between them, as Nuked OPL3 makes it.</summary>
    public void Generate4ChResampled(Span<short> buf4)
    {
        while (_sampleCnt >= _rateRatio)
        {
            _oldSamples[0] = _samples[0];
            _oldSamples[1] = _samples[1];
            _oldSamples[2] = _samples[2];
            _oldSamples[3] = _samples[3];
            Generate4Ch(_samples);
            _sampleCnt -= _rateRatio;
        }
        for (int i = 0; i < 4; i++)
            buf4[i] = (short)(((_oldSamples[i] * (_rateRatio - _sampleCnt)) + (_samples[i] * _sampleCnt)) / _rateRatio);
        _sampleCnt += 1 << RsmFrac;
    }

    /// <summary>Left and right, at the rate the chip was reset for.</summary>
    public void GenerateResampled(Span<short> buf)
    {
        Span<short> samples = stackalloc short[4];
        Generate4ChResampled(samples);
        buf[0] = samples[0];
        buf[1] = samples[1];
    }

    /// <summary>The chip as it powers up, to make samples at <paramref name="sampleRate"/>.</summary>
    public void Reset(int sampleRate)
    {
        Array.Clear(_signals);
        _timer = 0;
        _egTimer = 0;
        _egTimerRem = _egState = _egAdd = _egTimerLo = 0;
        _newm = _nts = _rhy = _vibPos = _tremolo = _tremoloPos = 0;
        Array.Clear(_mixBuff);
        _rmHhBit2 = _rmHhBit3 = _rmHhBit7 = _rmHhBit8 = _rmTcBit3 = _rmTcBit5 = 0;
        _sampleCnt = 0;
        Array.Clear(_oldSamples);
        Array.Clear(_samples);
        for (int slotnum = 0; slotnum < 36; slotnum++)
        {
            _slot[slotnum] = new Slot
            {
                Out = 1 + (2 * slotnum),
                FbMod = 2 + (2 * slotnum),
                Mod = Zero,
                EgRout = 0x1ff,
                EgOut = 0x1ff,
                EgGen = EgRelease,
                Trem = false,
                SlotNum = (byte)slotnum,
            };
        }
        for (int channum = 0; channum < 18; channum++)
        {
            var channel = _channel[channum] = new Channel();
            int localChSlot = ChSlot[channum];
            channel.Slots[0] = _slot[localChSlot];
            channel.Slots[1] = _slot[localChSlot + 3];
            _slot[localChSlot].Channel = channel;
            _slot[localChSlot + 3].Channel = channel;
        }
        for (int channum = 0; channum < 18; channum++)
        {
            var channel = _channel[channum];
            if (channum % 9 < 3)
                channel.Pair = _channel[channum + 3];
            else if (channum % 9 < 6)
                channel.Pair = _channel[channum - 3];
            channel.Out[0] = Zero;
            channel.Out[1] = Zero;
            channel.Out[2] = Zero;
            channel.Out[3] = Zero;
            channel.ChType = ChTwoOp;
            channel.Cha = 0xffff;
            channel.Chb = 0xffff;
            channel.ChNum = (byte)channum;
            ChannelSetupAlg(channel);
        }
        _noise = 1;
        _rateRatio = (sampleRate << RsmFrac) / NativeRate;
        _tremoloShift = 4;
        _vibShift = 1;
    }

    /// <summary>A register written: 0x000-0x0FF the first bank, 0x100-0x1FF the second, the OPL3's own.</summary>
    public void WriteReg(int reg, byte v)
    {
        int high = (reg >> 8) & 0x01;
        int regm = reg & 0xff;
        switch (regm & 0xf0)
        {
            case 0x00:
                if (high != 0)
                {
                    switch (regm & 0x0f)
                    {
                        case 0x04: ChannelSet4Op(v); break;
                        case 0x05: _newm = (byte)(v & 0x01); break;
                    }
                }
                else if ((regm & 0x0f) == 0x08)
                {
                    _nts = (byte)((v >> 6) & 0x01);
                }
                break;
            case 0x20:
            case 0x30:
                if (AdSlot[regm & 0x1f] >= 0)
                    SlotWrite20(_slot[(18 * high) + AdSlot[regm & 0x1f]], v);
                break;
            case 0x40:
            case 0x50:
                if (AdSlot[regm & 0x1f] >= 0)
                    SlotWrite40(_slot[(18 * high) + AdSlot[regm & 0x1f]], v);
                break;
            case 0x60:
            case 0x70:
                if (AdSlot[regm & 0x1f] >= 0)
                    SlotWrite60(_slot[(18 * high) + AdSlot[regm & 0x1f]], v);
                break;
            case 0x80:
            case 0x90:
                if (AdSlot[regm & 0x1f] >= 0)
                    SlotWrite80(_slot[(18 * high) + AdSlot[regm & 0x1f]], v);
                break;
            case 0xe0:
            case 0xf0:
                if (AdSlot[regm & 0x1f] >= 0)
                    SlotWriteE0(_slot[(18 * high) + AdSlot[regm & 0x1f]], v);
                break;
            case 0xa0:
                if ((regm & 0x0f) < 9)
                    ChannelWriteA0(_channel[(9 * high) + (regm & 0x0f)], v);
                break;
            case 0xb0:
                if (regm == 0xbd && high == 0)
                {
                    _tremoloShift = (byte)((((v >> 7) ^ 1) << 1) + 2);
                    _vibShift = (byte)(((v >> 6) & 0x01) ^ 1);
                    ChannelUpdateRhythm(v);
                }
                else if ((regm & 0x0f) < 9)
                {
                    var channel = _channel[(9 * high) + (regm & 0x0f)];
                    ChannelWriteB0(channel, v);
                    if ((v & 0x20) != 0)
                        ChannelKeyOn(channel);
                    else
                        ChannelKeyOff(channel);
                }
                break;
            case 0xc0:
                if ((regm & 0x0f) < 9)
                    ChannelWriteC0(_channel[(9 * high) + (regm & 0x0f)], v);
                break;
        }
    }
}
