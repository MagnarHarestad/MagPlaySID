# The C64's changes to Microsoft's 6502 BASIC, as patches to its source.
from port import patch
import os
HERE = os.path.dirname(os.path.abspath(__file__))
def here(name):
    return os.path.join(HERE, name)
def between(start, end):
    # The original source from start through end - for patches that drop or replace a whole block.
    src = open(here('m6502.asm'), encoding='latin1').read().upper().replace(chr(13), '')
    i = src.index(start)
    return src[i:src.index(end, i) + len(end)]

# ---- page zero as the C64 lays it out
patch("\tROMSW==1\n\tRORSW==1\n\tTRMPOS=^O306>", "\tROMSW==1\n\tRORSW==1>")
patch("\tCQTIMR=^O215\n", "\tCQTIMR=^O240\t\t;C64: THE JIFFY CLOCK AT $A0\n")
patch("\tCQSTAT=^O226\n", "\tCQSTAT=^O220\t\t;C64: STATUS AT $90\n")
patch("IFN\tROMSW,<\nUSRPOK: JMP\tFCERR>", "USRPOK=^O1420\t\t\t;C64: THE USR JUMP AT $0310\n\tORG\t7")
patch("ENDCHR: BLOCK\t1\t\t;THE OTHER DELIMITING CHARACTER.\n",
      "ENDCHR: BLOCK\t1\t\t;THE OTHER DELIMITING CHARACTER.\nTRMPOS: BLOCK\t1\t\t;C64: COLUMN FOR TAB AND SPC.\nVERCK:\tBLOCK\t1\t\t;C64: LOAD OR VERIFY.\n")
patch("IFN\tREALIO,<\nCNTWFL: BLOCK\t1>", "CNTWFL=2")
patch("LINWID: LINLEN\t\t\t;LENGTH OF LINE (WIDTH).\nNCMWID: NCMPOS", "LINWID=2\nNCMWID=2")

# ---- the KERNAL calls and vectors the C64's BASIC goes through
patch("\tISCNTC=^O177741\n", "\tSTOPK=^O177741\t\t;C64: THE KERNAL'S STOP, CALLED FROM ISCNTC\n")
patch("\tCQSYS= ^O177736\n", "\tRDTIM=^O177736\t\t;C64: $FFDE READS THE JIFFY CLOCK\n\tSETTIM=^O177733\t\t;C64: $FFDB SETS IT\n\tREADST=^O177667\t\t;C64: $FFB7 READS THE STATUS\n\tPLOT=^O177760\t\t;C64: $FFF0 THE CURSOR\n\tSETMSG=^O177620\t\t;C64: $FF90\n\tIGONE=^O1410\t\t;C64: VECTORS INTO BASIC\n\tIEVAL=^O1412\n")

# NEWSTT: ISCNTC is a subroutine that calls the KERNAL; a NOP where the C64 has one; the next statement through $0308.
patch("\tCPYI\tBUFPAG>\t\t;SEE IF IT WAS DIRECT BY CHECK FOR BUF'S PAGE NUMBER\n\tBEQ\tDIRCON\n",
      "\tCPYI\tBUFPAG>\t\t;SEE IF IT WAS DIRECT BY CHECK FOR BUF'S PAGE NUMBER\n\tNOP\t\t\t;C64\n\tBEQ\tDIRCON\n")
patch("\tBCC\tGONE\n\tINC\tTXTPTR+1\nGONE:\tJSR\tCHRGET\t\t;GET THE STATEMENT TYPE.\n",
      "\tBCC\tNEWGO\n\tINC\tTXTPTR+1\nNEWGO:\tJMPD\tIGONE\t\t;C64: THROUGH THE VECTOR\nGONE:\tJSR\tCHRGET\t\t;GET THE STATEMENT TYPE.\n")
patch("STOP:\tBCS\tSTOPC\t\t;MAKE [C] NONZERO AS A FLAG.\n",
      "ISCNTC: JSR\tSTOPK\t\t;C64: ASK THE KERNAL\nSTOP:\tBCS\tSTOPC\t\t;MAKE [C] NONZERO AS A FLAG.\n")

# RUN: the C64 turns the KERNAL's messages off first.
patch("RUN:\tJEQ\tRUNC\t\t;IF NO LINE # ARGUMENT.\n",
      "RUN:\tPHP\n\tLDAI\t0\n\tJSR\tSETMSG\t\t;C64: NO KERNAL MESSAGES\n\tPLP\n\tJEQ\tRUNC\t\t;IF NO LINE # ARGUMENT.\n")

# GOTO: the C64 compares the whole line number to decide where to search from.
patch("\tJSR\tREMN\t\t;SKIP TO END OF LINE.\n\tLDA\tCURLIN+1\n\tCMP\tLINNUM+1\n\tBCS\tLUK4IT\n",
      "\tJSR\tREMN\t\t;SKIP TO END OF LINE.\n\tSEC\t\t\t;C64: ALL OF THE LINE NUMBER\n\tLDA\tCURLIN\n\tSBC\tLINNUM\n\tLDA\tCURLIN+1\n\tSBC\tLINNUM+1\n\tBCS\tLUK4IT\n")

# READ: the C64 skips spaces before a DATA it looks for.
patch("NOWLIN: LDADY\tTXTPTR\t\t;HOW IS IT?\n\tTAX\n\tJSR\tADDON\t\t;ADD [Y] TO [TXTPTR].\n",
      "NOWLIN: JSR\tADDON\t\t;C64: ADD [Y] TO [TXTPTR] FIRST,\n\tJSR\tCHRGOT\t\t;AND SKIP SPACES.\n\tTAX\n")

# EVAL through $030A; ISVAR out of a branch's reach.
patch("EVAL:\tCLR\tVALTYP\t\t;ASSUME VALUE WILL BE NUMERIC.\n",
      "EVAL:\tJMPD\tIEVAL\t\t;C64: THROUGH THE VECTOR\nEVALC:\tCLR\tVALTYP\t\t;ASSUME VALUE WILL BE NUMERIC.\n")
patch("EVAL2:\tJSR\tISLETC\t\t;VARIABLE NAME?\n\tBCS\tISVAR\t\t;YES.\n",
      "EVAL2:\tJSR\tISLETC\t\t;VARIABLE NAME?\n\tBCC\tEVALV\n\tJMP\tISVAR\t\t;YES.\nEVALV:\n")

# PTRGET: the C64 clears SUBFLG with Y.
patch("\tCLR\tSUBFLG\t\t;ALLOW SUBSCRIPTS AGAIN.\n\tLDA\tVARTAB\t\t;PLACE TO START SEARCH.\n\tLDX\tVARTAB+1\n\tLDYI\t0\nSTXFND:",
      "\tLDYI\t0\n\tSTY\tSUBFLG\t\t;ALLOW SUBSCRIPTS AGAIN.\n\tLDA\tVARTAB\t\t;PLACE TO START SEARCH.\n\tLDX\tVARTAB+1\nSTXFND:")

# PRINT: no output suppression, the line feed only to files over 127, and the column from the KERNAL.
patch("OUTDO:\tIFN\tREALIO,<\n\tBIT\tCNTWFL\t\t;SHOULDN'T AFFECT CHANNEL I/O!\n\tBMI\tOUTRTS>\n", "OUTDO:\n")
patch("OUTLOC: JSR\tOUTCH>\t\t;OUTPUT THE CHARACTER.\n", "OUTLOC: JSR\tCOUT>\t\t;C64: THROUGH A CHECK FOR ERRORS\n")
patch("GOCR:\tLDAI\t13>\t\t;X AND Y MUST BE PRESERVED.\n\tJSR\tOUTDO\n\tLDAI\t10\n\tJSR\tOUTDO\n",
      "GOCR:\tLDAI\t13>\t\t;X AND Y MUST BE PRESERVED.\n\tJSR\tOUTDO\n\tBIT\tCHANNL\t\t;C64: A LINE FEED ONLY FOR A FILE OVER 127\n\tBPL\tCRFIN\n\tLDAI\t10\n\tJSR\tOUTDO\n")
patch("COMPRT: LDA\tTRMPOS\n", "COMPRT: SEC\t\t\t;C64: THE CURSOR'S COLUMN\n\tJSR\tPLOT\n\tTYA\n")
patch("TABER:\tPHP\t\t\t;REMEMBER IF SPC OR TAB FUNCTION.\n",
      "TABER:\tPHP\t\t\t;REMEMBER IF SPC OR TAB FUNCTION.\n\tSEC\t\t\t;C64: THE CURSOR'S COLUMN\n\tJSR\tPLOT\n\tSTY\tTRMPOS\n")

# ISVAR: the C64 knows TI, TI$ and ST by a pointer into the ROM, and reads the clock through the KERNAL.
patch("""ISVRET: STWD	FACMO
IFN	TIME!EXTIO,<
	LDWD	VARNAM>		;CHECK TIME,TIME$,STATUS.
	LDX	VALTYP
	BEQ	GOOO		;THE STRING IS SET UP.
	LDXI	0
	STX	FACOV
IFN	TIME,<
	BIT	FACLO		;AN ARRAY?
	BPL	STRRTS		;YES.
	CMPI	"T"		;TI$?
	BNE	STRRTS
	CPYI	"I"+128
""", """ISVRET: STWD	FACMO
	LDXY	VARNAM		;C64: CHECK TIME,TIME$,STATUS.
	LDA	VALTYP
	BEQ	GOOO		;THE STRING IS SET UP.
	LDAI	0
	STA	FACOV
IFN	TIME,<
	JSR	ISROM		;C64: TI$ POINTS INTO THE ROM.
	BCC	STRRTS
	CPXI	"T"		;TI$?
	BNE	STRRTS
	CPYI	"I"+128
""")
patch("""IFN	INTPRC,<
	LDX	INTFLG
	BPL	GOOOOO
""", """IFN	INTPRC,<
	BIT	INTFLG		;C64
	BPL	GOOOOO
""")
patch("""GOOOOO:
IFN	TIME,<
	BIT	FACLO		;AN ARRAY?
	BPL	GOMOVF		;YES.
	CMPI	"T"
	BNE	QSTATV
	CPYI	"I"
""", """GOOOOO:
IFN	TIME,<
	JSR	ISROM		;C64: TI AND ST POINT INTO THE ROM.
	BCC	GOMOVF
	CPXI	"T"
	BNE	QSTATV
	CPYI	"I"
""")
patch("""GETTIM: LDWDI	<CQTIMR-2>
	SEI			;TURN OF INT SYS.
	JSR	MOVFM
	CLI			;BACK ON.
	STY	FACHO		;ZERO HIGHEST.
	RTS>
QSTATV:
IFN	EXTIO,<
	CMPI	"S"
	BNE	GOMOVF
	CPYI	"T"
	BNE	GOMOVF
	LDA	CQSTAT
	JMP	FLOAT
""", """GETTIM: JSR	RDTIM		;C64: THE KERNAL READS THE CLOCK.
	STX	FACMO
	STY	FACMOH
	STA	FACLO
	LDYI	0
	STY	FACHO		;ZERO HIGHEST.
	RTS>
QSTATV:
IFN	EXTIO,<
	CPXI	"S"
	BNE	GOMOVF
	CPYI	"T"
	BNE	GOMOVF
	JSR	READST		;C64: SO DOES THE STATUS.
	JMP	FLOAT
""")

patch("""	JSR	REASON
	LDAI	0
	TAY
	STA	CURTOL+1
""", """	JSR	REASON
	LDYI	0		;C64
	STY	CURTOL+1
""")

# PEEK reads the ROM too, and WAIT has no egg.
patch("""IFE	REALIO-3,<
	CMPI	ROMLOC/256	;IF WITHIN BASIC,
	BCC	GETCON
	CMPI	LASTWR/256
	BCC	DOSGFL>		;GIVE HIM ZERO FOR AN ANSWER.
""", "")
patch("\tBEQ\tZSTORDO\n\tJSR\tCOMBYT\t\t;GET MASK2.\n", "\tBEQ\tSTORDO\n\tJSR\tCOMBYT\t\t;GET MASK2.\n")
# the KERNAL's input through checks for errors, and no ^O.
patch("\tJSR\tCZGETL\t\t;DON'T WANT INCHR. JUST ONE.\n", "\tJSR\tCGETIN\t\t;C64: DON'T WANT INCHR. JUST ONE.\n")
patch("INPUT:\tIFN\tREALIO,<\n\tLSR\tCNTWFL>\t\t;BE TALKATIVE.\n", "INPUT:\n")
# RND: five-byte constants, and RND(0) from CIA 1's timer and clock.
patch("""RMULZC: 230
	065
	104
	172
RADDZC: 150
	050
	261
	106
""", """RMULZC: 230
	065
	104
	172
	000			;C64: FIVE BYTES
RADDZC: 150
	050
	261
	106
	000
""")

# The statements Commodore's KERNAL did for it.
patch("\tADR(CQLOAD-1)\n\tADR(CQSAVE-1)\n\tADR(CQVERF-1)>", "\tADR(LOAD-1)\n\tADR(SAVE-1)\n\tADR(VERIFY-1)>")
patch("\tADR(CQSYS-1)\n\tADR(CQOPEN-1)\n\tADR(CQCLOS-1)>", "\tADR(SYS-1)\n\tADR(OPEN-1)\n\tADR(CLOSE-1)>")
patch("\tJSR\tCQOIN\t\t;GET CHANNEL OPEN FOR INPUT.\n", "\tJSR\tCCHKIN\t\t;C64: GET CHANNEL OPEN FOR INPUT.\n")
patch("\tJSR\tCQOIN\t\t;GO WHERE COMMODORE CHECKS IN OPEN.\n", "\tJSR\tCCHKIN\t\t;C64: GO WHERE COMMODORE CHECKS IN OPEN.\n")
patch("\tJSR\tCQOOUT\t\t;CHECK AND OPEN OUTPUT CHANNL.\n", "\tJSR\tCCKOUT\t\t;C64: CHECK AND OPEN OUTPUT CHANNL.\n")

# The ROM starts at $A000 with its two entry points, and runs on at $E000.
patch("ROMLOC=^O140000\n", "ROMLOC=^O120000\t\t;C64: $A000\n")
patch("\tORG\tROMLOC\n\nSTMDSP:", "\tORG\tROMLOC\n\tADR(INIT)\t\t;C64: COLD START,\n\tADR(WARM)\t\t;AND WARM.\n\tDT\"MSBASIC2\"\n\nSTMDSP:")

# The C64's start, in place of the PET's.
src_tail_anchor = "PAGE\nSUBTTL\tSYSTEM INITIALIZATION CODE.\n"

_src = open(here('m6502.asm'), encoding='latin1').read().upper().replace(chr(13), '')
_i = _src.index(src_tail_anchor)
patch(_src[_i:], open(here('tail.mac')).read() + chr(10) + "END" + chr(10))
patch("GMOVMF: JMP\tMOVMF\t\t;PUT NEW ONE INTO MEMORY.\n",
      "GMOVMF: JMP\tMOVMF\t\t;PUT NEW ONE INTO MEMORY.\n" + open(here('glue.mac')).read())
# EXP runs on at $E000, as the C64's does.
patch("STOLD:\tSTA\tOLDOV\n", "STOLD:\tJMP\tSTOLD2\t\t;C64: ON AT $E000.\n\tORG\t^O160000\nSTOLD2: STA\tOLDOV\n")
# ISROM where the C64 has it, before ISVAR; room to move ISVAR so that no other call of PTRGET
# returns to the same low byte (see port.collisions).
patch("ISVAR:\tJSR\tPTRGET\t\t;GET A PNTR TO VARIABLE.\n", """;
; C64: IS [FACMO] A POINTER INTO THE ROM - TI, TI$ OR ST? CARRY SET IF IT IS.
;
ISROM:	SEC
	LDA	FACMO
	SBCI	<ROMLOC&^O377>
	LDA	FACMO+1
	SBCI	<ROMLOC/^O400>
	BCC	ISRRTS
	LDAI	<ROMEND&^O377>
	SBC	FACMO
	LDAI	<ROMEND/^O400>
	SBC	FACMO+1
ISRRTS: RTS
	BLOCK	ISVPAD
ISVAR:	JSR	PTRGET		;GET A PNTR TO VARIABLE.
""")
# ":" goes on through the vector too.
patch("MORSTS: CMPI\t\":\"\n\tBEQ\tGONE\t\t;IF A \":\" CONTINUE STATEMENT\n", "MORSTS: CMPI\t\":\"\n\tBEQ\tNEWGO\t\t;C64: IF A \":\" CONTINUE STATEMENT, THROUGH THE VECTOR\n")
# No WAIT 6502 egg on the C64.
patch(between("IFE\tREALIO-3,<\nZSTORD:!", "IF2,<PURGE ZSTORD>>\n"), "")
# The C64's line input: the KERNAL's CHRIN, a limit of 88 characters, no ^O; CRUNCH through $0304.
patch("INLINC: JSR\tINCHR\t\t;GET A CHARACTER.\n", "INLINC: JSR\tCIN\t\t;C64: GET A CHARACTER.\n")
patch(between("GOODCH:\nIFN\tREALIO-3,<\n\tCPXI", "INCRTS: RTS\t\t\t;END OF INCHR.\n"), """GOODCH: STA	BUF,X
	INX
	CPXI	89		;C64: TOO LONG?
	BCC	INLINC
	LDXI	ERRLS
	JMP	ERROR
FININ1: JMP	FININL>		;GO TO FININL FAR, FAR AWAY.
INCHR=	CIN
""")
patch("CRUNCH: LDX\tTXTPTR\t\t;SET SOURCE POINTER.\n", "CRUNCH: JMPD\t^O1404\t\t;C64: THROUGH THE VECTOR\nCRUNC2: LDX\tTXTPTR\t\t;SET SOURCE POINTER.\n")
patch("ENDCON: LDWDI\tBRKTXT\nIFN\tREALIO,<\n\tLDXI\t0\n\tSTX\tCNTWFL>\n", "ENDCON: LDWDI\tBRKTXT\n")
# TI$= sets the clock through the KERNAL.
patch("""	LDXI	2
	SEI			;DISALLOW INTERRUPTS.
TIMEST: LDA	FACMOH,X
	STA	CQTIMR,X
	DEX
	BPL	TIMEST		;LOOP 3 TIMES.
	CLI			;TURN ON INTS AGAIN.
	RTS
""", """	LDX	FACMO		;C64: THE KERNAL SETS THE CLOCK.
	LDY	FACMOH
	LDA	FACLO
	JMP	SETTIM
""")
# CLR closes the files before CLEARC, which RUN with a line number also comes in by.
patch("""CLEARC: LDWD	MEMSIZ		;FREE UP STRING SPACE.
	STWD	FRETOP
IFN	EXTIO,<
	JSR	CQCALL>		;CLOSE ALL OPEN FILES.
""", """CLEARA: JSR	CQCALL		;C64: CLOSE ALL OPEN FILES FIRST.
CLEARC: LDWD	MEMSIZ		;FREE UP STRING SPACE.
	STWD	FRETOP
""")
patch("\tJSR\tCLEARC\t\t;CLEAN UP -- RESET THE STACK.\n\tJMP\tRUNC2\t\t;MUST REPLACE RTS ADDR.\n", "\tJSR\tCLEARA\t\t;CLEAN UP -- RESET THE STACK.\n\tJMP\tRUNC2\t\t;MUST REPLACE RTS ADDR.\n")
patch('EXIGNT: DT"?EXTRA IGNORED"\n\tACRLF\n\t0\nTRYAGN: DT"?REDO FROM START"\n\tACRLF\n\t0\n',
      'EXIGNT: DT"?EXTRA IGNORED"\n\t13\t\t\t;C64: NO LINE FEEDS.\n\t0\nTRYAGN: DT"?REDO FROM START"\n\t13\n\t0\n')
patch(between("	241	; 7.2362932E7", "	315>"), ">")		# the egg's message, after SINCON
patch("\tPHA\n\tLDAI\t0\t\t;SET A TERMINATOR.\n\tSTADY\tINDEX2\n", "\tPHA\n\tTYA\t\t\t;C64: SET A TERMINATOR.\n\tSTADY\tINDEX2\n")
# The error message's two letters, then on to TYPERR where the C64 has it.
patch("""	LDA	ERRTAB+1,X,	;GET SECOND CHR.
	JSR	OUTDO>		;OUTPUT IT.
""", """	LDA	ERRTAB+1,X,	;GET SECOND CHR.
	JSR	OUTDO		;OUTPUT IT.
	JMP	TYPERR>		;C64: ITS LONGER MESSAGES TAKE MORE ROOM.
""")
patch("ERROR:\nIFN\tREALIO,<\n\tLSR\tCNTWFL>\t\t;FORCE OUTPUT.\n", "ERROR:\n")
# READY turns the KERNAL's messages on, and goes to MAIN through $0302.
patch("""READY:
IFN	REALIO,<
	LSR	CNTWFL>		;TURN OUTPUT BACK ON IF SUPRESSED
	LDWDI	REDDY		;SAY "OK".
IFN	REALIO-3,<
	JSR	RDYJSR>		;OR GO TO INIT IF INIT ERROR.
IFE	REALIO-3,<
	JSR	STROUT>		;NO INIT ERRORS POSSIBLE.
MAIN:	JSR	INLIN		;GET A LINE FROM TERMINAL.
""", """READY:	LDWDI	REDDY		;SAY "OK".
	JSR	STROUT
	LDAI	128		;C64: KERNAL MESSAGES ON,
	JSR	SETMSG
MAINV:	JMPD	^O1402		;AND THROUGH THE VECTOR.
MAIN:	JSR	INLIN		;GET A LINE FROM TERMINAL.
""")
patch("ERRGO5: BEQ\tERRGO4\n", "ERRGO5: JMP\tERROR\t\t;C64\n")
patch("POS:\tLDY\tTRMPOS\t\t;GET POSITION.\n", "POS:\tSEC\t\t\t;C64: THE CURSOR'S COLUMN.\n\tJSR\tPLOT\n")
# RND(0): CIA 1's timer A and its clock's tenths and seconds, as the C64 takes them.
patch("""	LDA	CQHTIM
	STA	FACHO
	LDA	CQHTIM+4
	STA	FACMOH
	LDA	CQHTIM+1
	STA	FACMO
	LDA	CQHTIM+5
	STA	FACLO
	JMP	STRNEX>
""", """IOBASE=^O177763
	JSR	IOBASE		;C64: [X,Y] POINT AT CIA 1.
	STXY	INDEX
	LDYI	4
	LDADY	INDEX		;TIMER A LOW,
	STA	FACHO
	INY
	LDADY	INDEX		;HIGH,
	STA	FACMO
	LDYI	8
	LDADY	INDEX		;TENTHS OF THE CLOCK,
	STA	FACMOH
	INY
	LDADY	INDEX		;AND SECONDS.
	STA	FACLO
	JMP	STRNEX>
""")
