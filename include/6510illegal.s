if def _6510ILLEGAL_INCLUDED == 0
const _6510ILLEGAL_INCLUDED = 1

//http://members.chello.nl/taf.offenga/illopc31.txt

//AAC (ANC) [ANC]
//~~~~~~~~~~~~~~~
//AND byte with accumulator. If result is negative then carry is
//set. Status flags: N,Z,C
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Immediate   |AAC #arg   |$0B| 2 | 2
//Immediate   |AAC #arg   |$2B| 2 | 2

macro aac #n:8 {
	byte $0b, n
}

macro anc #n:8 {
	byte $0b, n
}

//AAX (SAX) [AXS]
//~~~~~~~~~~~~~~~
//AND X register with accumulator and store result in memory.
//Status flags: N,Z
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Zero Page   |AAX arg    |$87| 2 | 3
//Zero Page,Y |AAX arg,Y  |$97| 2 | 4
//(Indirect,X)|AAX (arg,X)|$83| 2 | 6
//Absolute    |AAX arg    |$8F| 3 | 4

macro aax n:8 {
	byte $87, n
}

macro aax n:8,y {
	byte $97, n
}

macro aax (n:8,x) {
	byte $83, n
}

macro aax n:16 {
	byte $8f
	word n
}

//ARR (ARR) [ARR]
//~~~~~~~~~~~~~~~
//AND byte with accumulator, then rotate one bit right in accu-
//mulator and check bit 5 and 6:
//If both bits are 1: set C, clear V.
//If both bits are 0: clear C and V.
//If only bit 5 is 1: set V, clear C.
//If only bit 6 is 1: set C and V.
//Status flags: N,V,Z,C
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Immediate   |ARR #arg   |$6B| 2 | 2

macro arr #n:8 {
	byte $6b, n
}

//ASR (ASR) [ALR]
//~~~~~~~~~~~~~~~
//AND byte with accumulator, then shift right one bit in accumu-
//lator. Status flags: N,Z,C
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Immediate   |ASR #arg   |$4B| 2 | 2

macro asr #n:8 {
	byte $4b, n
}

//ATX (LXA) [OAL]
//~~~~~~~~~~~~~~~
//AND byte with accumulator, then transfer accumulator to X
//register. Status flags: N,Z
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Immediate   |ATX #arg   |$AB| 2 | 2

macro atx #n:8 {
	byte $ab, n
}

//AXA (SHA) [AXA]
//~~~~~~~~~~~~~~~
//AND X register with accumulator then AND result with 7 and
//store in memory. Status flags: -
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Absolute,Y  |AXA arg,Y  |$9F| 3 | 5
//(Indirect),Y|AXA arg    |$93| 2 | 6

macro axa n:16,y {
	byte $9f
	word n
}

macro axa (n:8,y) {
	byte $93, n
}

//AXS (SBX) [SAX]
//~~~~~~~~~~~~~~~
//AND X register with accumulator and store result in X regis-
//ter, then subtract byte from X register (without borrow).
//Status flags: N,Z,C
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Immediate   |AXS #arg   |$CB| 2 | 2

macro axs #n:8 {
	byte $cb, n
}

macro sbx #n:8 {
	byte $cb, n
}

//DCP (DCP) [DCM]
//~~~~~~~~~~~~~~~
//Subtract 1 from memory (without borrow).
//Status flags: C
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Zero Page   |DCP arg    |$C7| 2 | 5
//Zero Page,X |DCP arg,X  |$D7| 2 | 6
//Absolute    |DCP arg    |$CF| 3 | 6
//Absolute,X  |DCP arg,X  |$DF| 3 | 7
//Absolute,Y  |DCP arg,Y  |$DB| 3 | 7
//(Indirect,X)|DCP (arg,X)|$C3| 2 | 8
//(Indirect),Y|DCP (arg),Y|$D3| 2 | 8

macro dcp n:8 {
	byte $c7, n
}

macro dcp n:8,x {
	byte $d7, n
}

macro dcp n:16 {
	byte $cf
	word n
}

macro dcp n:16,x {
	byte $df
	word n
}

macro dcp n:16,y {
	byte $db
	word n
}

macro dcp (n:8,x) {
	byte $c3, n
}

macro dcp (n:8),y {
	byte $d3, n
}

//ISC (ISB) [INS]
//~~~~~~~~~~~~~~~
//Increase memory by one, then subtract memory from accumulator
//(with borrow). Status flags: N,V,Z,C
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Zero Page   |ISC arg    |$E7| 2 | 5
//Zero Page,X |ISC arg,X  |$F7| 2 | 6
//Absolute    |ISC arg    |$EF| 3 | 6
//Absolute,X  |ISC arg,X  |$FF| 3 | 7
//Absolute,Y  |ISC arg,Y  |$FB| 3 | 7
//(Indirect,X)|ISC (arg,X)|$E3| 2 | 8
//(Indirect),Y|ISC (arg),Y|$F3| 2 | 8

macro isc n:8 {
	byte $e7, n
}

macro isc n:8,x {
	byte $f7, n
}

macro isc n:16 {
	byte $ef
	word n
}

macro isc n:16,x {
	byte $ff
	word n
}

macro isc n:16,y {
	byte $fb
	word n
}

macro isc (n:8,x) {
	byte $e3, n
}

macro isc (n:8),y {
	byte $f3, n
}

//LAR (LAE) [LAS]
//~~~~~~~~~~~~~~~
//AND memory with stack pointer, transfer result to accumulator,
//X register and stack pointer.
//Status flags: N,Z
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Absolute,Y  |LAR arg,Y  |$BB| 3 | 4 *

macro lar n:16,y {
	byte $bb
	word n
}

//LAX (LAX) [LAX]
//~~~~~~~~~~~~~~~
//Load accumulator and X register with memory.
//Status flags: N,Z
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Imm         |LAX #arg	  |$AB| 2 | 2   	// note use with care LAX #$00 is safe good for multi init 
//Zero Page   |LAX arg    |$A7| 2 | 3
//Zero Page,Y |LAX arg,Y  |$B7| 2 | 4
//Absolute    |LAX arg    |$AF| 3 | 4
//Absolute,Y  |LAX arg,Y  |$BF| 3 | 4 *
//(Indirect,X)|LAX (arg,X)|$A3| 2 | 6
//(Indirect),Y|LAX (arg),Y|$B3| 2 | 5 *

macro lax #n:8 {
	byte $ab, n
}

macro lax n:8 {
	byte $a7, n
}

macro lax n:8,y {
	byte $b7, n
}

macro lax n:16 {
	byte $af
	word n
}

macro lax n:16,y {
	byte $bf
	word n
}

macro lax (n:8,x) {
	byte $a3, n
}

macro lax (n:8),y {
	byte $b3, n
}

//RLA (RLA) [RLA]
//~~~~~~~~~~~~~~~
//Rotate one bit left in memory, then AND accumulator with
//memory. Status flags: N,Z,C
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Zero Page   |RLA arg    |$27| 2 | 5
//Zero Page,X |RLA arg,X  |$37| 2 | 6
//Absolute    |RLA arg    |$2F| 3 | 6
//Absolute,X  |RLA arg,X  |$3F| 3 | 7
//Absolute,Y  |RLA arg,Y  |$3B| 3 | 7
//(Indirect,X)|RLA (arg,X)|$23| 2 | 8
//(Indirect),Y|RLA (arg),Y|$33| 2 | 8

macro rla n:8 {
	byte $27, n
}

macro rla n:8,x {
	byte $37, n
}

macro rla n:16 {
	byte $2f
	word n
}

macro rla n:16,x {
	byte $3f
	word n
}

macro rla n:16,y {
	byte $2b
	word n
}

macro rla (n:8,x) {
	byte $23, n
}

macro rla (n:8),y {
	byte $33, n
}

//RRA (RRA) [RRA]
//~~~~~~~~~~~~~~~
//Rotate one bit right in memory, then add memory to accumulator
//(with carry).
//
//Status flags: N,V,Z,C
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Zero Page   |RRA arg    |$67| 2 | 5
//Zero Page,X |RRA arg,X  |$77| 2 | 6
//Absolute    |RRA arg    |$6F| 3 | 6
//Absolute,X  |RRA arg,X  |$7F| 3 | 7
//Absolute,Y  |RRA arg,Y  |$7B| 3 | 7
//(Indirect,X)|RRA (arg,X)|$63| 2 | 8
//(Indirect),Y|RRA (arg),Y|$73| 2 | 8

macro rra n:8 {
	byte $67, n
}

macro rra n:8,x {
	byte $77, n
}

macro rra n:16 {
	byte $6f
	word n
}

macro rra n:16,x {
	byte $7f
	word n
}

macro rra n:16,y {
	byte $7b
	word n
}

macro rra (n:8,x) {
	byte $63, n
}

macro rra (n:8),y {
	byte $73, n
}

//SLO (SLO) [ASO]
//~~~~~~~~~~~~~~~
//Shift left one bit in memory, then OR accumulator with memory.
//
//Status flags: N,Z,C
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Zero Page   |SLO arg    |$07| 2 | 5
//Zero Page,X |SLO arg,X  |$17| 2 | 6
//Absolute    |SLO arg    |$0F| 3 | 6
//Absolute,X  |SLO arg,X  |$1F| 3 | 7
//Absolute,Y  |SLO arg,Y  |$1B| 3 | 7
//(Indirect,X)|SLO (arg,X)|$03| 2 | 8
//(Indirect),Y|SLO (arg),Y|$13| 2 | 8

macro slo n:8 {
	byte $07, n
}

macro slo n:8,x {
	byte $17, n
}

macro slo n:16 {
	byte $0f
	word n
}

macro slo n:16,x {
	byte $1f
	word n
}

macro slo n:16,y {
	byte $1b
	word n
}

macro slo (n:8,x) {
	byte $03, n
}

macro slo (n:8),y {
	byte $13, n
}

//SRE (SRE) [LSE]
//~~~~~~~~~~~~~~~
//Shift right one bit in memory, then EOR accumulator with
//memory. Status flags: N,Z,C
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Zero Page   |SRE arg    |$47| 2 | 5
//Zero Page,X |SRE arg,X  |$57| 2 | 6
//Absolute    |SRE arg    |$4F| 3 | 6
//Absolute,X  |SRE arg,X  |$5F| 3 | 7
//Absolute,Y  |SRE arg,Y  |$5B| 3 | 7
//(Indirect,X)|SRE (arg,X)|$43| 2 | 8
//(Indirect),Y|SRE (arg),Y|$53| 2 | 8

macro sre n:8 {
	byte $47, n
}

macro sre n:8,x {
	byte $57, n
}

macro sre n:16 {
	byte $4f
	word n
}

macro sre n:16,x {
	byte $5f
	word n
}

macro sre n:16,y {
	byte $5b
	word n
}

macro sre (n:8,x) {
	byte $43, n
}

macro sre (n:8),y {
	byte $53, n
}

//SXA (SHX) [XAS]
//~~~~~~~~~~~~~~~
//AND X register with the high byte of the target address of the
//argument + 1. Store the result in memory.
//
//M = X AND HIGH(arg) + 1
//
//Status flags: -
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Absolute,Y  |SXA arg,Y  |$9E| 3 | 5

macro sxa n:16,y {
	byte $9e
	word n
}

macro shx n:16,y {
	byte $9e
	word n
}

//SYA (SHY) [SAY]
//~~~~~~~~~~~~~~~
//AND Y register with the high byte of the target address of the
//argument + 1. Store the result in memory.
//
//M = Y AND HIGH(arg) + 1
//
//Status flags: -
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Absolute,X  |SYA arg,X  |$9C| 3 | 5

macro sya n:16,x {
	byte $9c
	word n
}

macro shy n:16,x {
	byte $9c
	word n
}

//XAS (SHS) [TAS]
//~~~~~~~~~~~~~~~
//AND X register with accumulator and store result in stack
//pointer, then AND stack pointer with the high byte of the
//target address of the argument + 1. Store result in memory.
//
//S = X AND A, M = S AND HIGH(arg) + 1
//
//Status flags: -
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Absolute,Y  |XAS arg,Y  |$9B| 3 | 5

macro xas n:16,y {
	byte $9b
	word n
}

//XAA (highly unstable) external ref A = (A | magic) & X & imm
//~~~~~~~~~~~~~~~
//transfer x to accumulator, AND byte with accumulator
//register. Status flags: N,Z
//
//Addressing  |Mnemonics  |Opc|Sz | n
//------------|-----------|---|---|---
//Immediate   |XAA #arg   |$AB| 2 | 2

macro xaa #n:8 {
	byte $8b, n
}

endif //_6510ILLEGAL_INCLUDED