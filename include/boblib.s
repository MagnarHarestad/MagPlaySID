/*
	THIS IS AN EXPERIMENTAL LIB/FRAME WORK in order to simplify certain functionality that are frequently used in Bobs parts
	------------------------------------------------------------------------------------------------------------------------
	Please don't change anything here. since it must be backward compatible for now.
*/


macro set_raster_irqAcc start:16, jump:16
{
			sta $d012
	
			lda #$4c		// initiate a jmp from zp
			sta start
			lda #jump & 255
			sta start+1
			lda #jump >> 8
			sta start+2
			
			lda #start & 255	// irq sets in zp due to ffff issue
			sta $fffe
			lda #start >> 8
			sta $ffff
}

macro set_raster_irqD #line:8, start:16
{
			lda #line			;2
			sta $d012			;4
			lda #start & 255	;2 
			sta irq_jmp+1		;3
			lda #start >> 8		;2
			sta irq_jmp+2		;3	= 18 
}

// NOTE THIS ONE MUST BE ALLIGNED!
macro double_irq_zp_timed #line:8
{
			pha	; push acc to stack
			txa
			pha ; push x
			tya
			pha ; push y
			lda $01	
			pha ; push 01 reg
			lda #$35
			sta $01		

			lda #line+1 
			sta $d012			;4
			lda #.irq4 & 255		;2 
			sta irq_jmp+1		;3 =  11 cc
;			lda #irq4 >> 8		;2			// Note this was causing orq overflow and thus fucked up the perfect timeing. needed to keep it short!!
;			sta irq_jmp+2		;3	= 18 

			lsr $d019
			tsx
			cli
			; --- The next IRQ will happen between one of the nops below, add or remove nops if needed
			nop
			nop
			nop
			nop
			nop
			nop
			nop
			nop
			nop
			nop
			nop
			nop
			nop
			nop
			nop
			
			brk
//--------------------------- STABLE SECOND IRQ STARTS HERE!!!
.irq4:
			txs				; Retain stack position from previous irq we dont need to restore anything due to the stack trick.. phas done in previous IRQ we will restore that end of this irq (4)
			ldx #$08
.e:			  dex
			bne .e
;			nop
			; --- Final cycle wobble check.
			ldy $d012 //#irq_line+1
			cpy $d012
			beq .start		
.start:		
}

// multi screen vsp scroller using crunch_fld_vsp variant. this can only work at specific raster values every 8th line.. 
// NOTE LOOPS ARE REQ TO BE INSIDE ONE PAGE!
// Dependency of variables
// data: table (for d011 crunch and fld)
// fldy: for setting the Ypos of the crunchlines
// vsp_sm1: for screen X position
macro crunch_fld_vsp_looped
{
if _DEBUG == 1
			inc $d020
			cmp ($c1,x)
			//dec $d020
else	
			cmp ($c1,x)
			cmp ($c1,x)		
endif	
			cmp ($c1,x)
			cmp ($c1,x)		
			cmp ($c1,x)		
			cmp ($c1,x)		
			cmp ($c1,x)
			cmp ($c1,x)
			cmp ($c1,x)		
			ldx #$00		; reset loop used for look up d011  (,x)
			ldy yPosOffset	; initial Y pos, used for cruncline. amount of lines
			;--- line_crunch:
			cpy #$00		; skip crunch if line == 0
			beq .s3
.crunchLoop:	lda table+7,x	; <----+	LINE CRUNCH loop handleing 6 picture (6 rows)	
				sta $d011		;      |
if _DEBUG == 1					;      |
				sta $d021		;      |
				nop				;      |
else							;      |
				cmp ($c1,x)		;      |
endif							;      |
				cmp ($c1,x)		;      |
				cmp ($c1,x)		;      |
				cmp ($c1,x)		;      |
				cmp ($c1,x)		;      |
				cmp ($c1,x)		;      |
				cmp ($c1,x)		;      |
				cmp ($c1,x)		;      |
				inx				;      |
				dey				;      |	
			bne .crunchLoop		; =----+
			nop
	;----- FLD LOOP ----
.s3:
fld1Loop:		lda table+0,x	; <----+	FLD loop handleing 6 picture (6 rows)
				sta $d011       ;      |
				inx             ;      |
fldy:			cpx #05			;      |
				beq fld2Loop    ;  ----+---> FLD EARLY EXIT TO HANDLE TIMING ISSUES.
if _DEBUG == 1                  ;      |
					sta $d021   ;      |
else		                    ;      |
					nop         ;      |
					nop         ;      |
endif		                    ;      |
					cmp ($c1,x)	;      |	
					cmp ($c1,x)	;      |	
					cmp ($c1,x)	;      |	
					cmp ($c1,x)	;      |	
					cmp ($c1,x)	;      |	
					cmp ($c1,x)	;      |	
					cmp ($c1,x)	;      |		
				jmp fld1Loop    ; =----+
fld2Loop:	lda table+0,x
			sta $d011			// 26 + d011 smooth scrolling added to the fld.
			inx
		;----- VSP PRE INIT & VSP
;			nop
if _DEBUG == 1
			inc $d021
else
			cmp ($c1,x)		
endif
			cmp ($c1,x)
			cmp ($c1,x)
			cmp ($c1,x)
			cmp ($c1,x)
			nop
			lda table+5,x		// vsp table combo to work with every d011 values. 
			aac #$7f			// and + clc
			ldy #d011_bkg		// OBSOLETE 
vsp_sm1:	bne .vsp			;-----------+		; stores cycle offset in bne
.vsp:			word $8080		;1  <-------+		; nop #$80 no reg changed here.
				word $8080		;2  <-------+
				word $8080		;3  <-------+
				word $8080		;4  <-------+
				word $8080		;5  <-------+
				word $8080		;6  <-------+
				word $8080		;7  <-------+	
				word $8080		;8  <-------+
				word $8080		;9  <-------+
				word $8080		;10 <-------+	[VSP BRANCHING]
				word $8080		;11 <-------+
				word $8080		;12 <-------+
				word $8080		;13 <-------+
				word $8080		;14 <-------+
				word $8080		;15 <-------+
				word $8080		;16 <-------+
				word $8080		;17 <-------+
				word $8080		;18 <-------+
				word $8080		;19 <-------+
				bit $ea			;20	<-------+	
			sta $d011	// Direct store avoid idle time 39
	    	adc #$01
		    and #$3f
		    sta $d011
}

macro end_of_irq_01
{
		pla
		sta $01
		pla
		tay
		pla
		tax
		pla
		rti
}
	
	
macro m_fld_loop
{
fld_cnt:	ldy #$00		; <- set this byte to N for N FLD lines (0 = skip)
			beq fld_done
fld_loop:	inc $d011		; 6cc
			lda $d011		; 4cc
			and #$07		; 2cc
			ora #$30		; 2cc
			sta $d011		; 4cc  = 18cc d011 block
		;	inc $d021
			bit $ea			; 3cc
			bit $ea			; 3cc
			bit $ea			; 3cc
			bit $ea			; 3cc
			bit $ea			; 3cc
			bit $ea			; 3cc
			bit $ea			; 3cc
			bit $ea			; 3cc
			bit $ea			; 3cc
			bit $ea			; 3cc
			bit $ea			; 3cc
			bit $ea			; 3cc  = 36cc (12 x bit $ea)
			nop				; 2cc
			nop				; 2cc  = 40cc filler total
			dey				; 2cc
			bne fld_loop		; 3cc  -> 63cc/line (branch taken)
fld_done:
}

macro m_fld_loop_short
{
fld_cnt:	ldy #$00		; <- set this byte to N for N FLD lines (0 = skip)
			beq fld_done
fld_loop:	inc $d011		; 6cc
			lda $d011		; 4cc
			and #$07		; 2cc
			ora #$30		; 2cc
			sta $d011		; 4cc  = 18cc d011 block
;			sta $d021
			nop
			nop
			ldx #$06
!:			dex
			bpl !-
			dey				; 2cc
			bne fld_loop		; 3cc  -> 63cc/line (branch taken)
fld_done:
}
