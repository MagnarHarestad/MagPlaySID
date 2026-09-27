if def _WORD_INCLUDED == 0
const _WORD_INCLUDED = 1

//-------------------------------------------------------------------------------
// 8 bit macros
//-------------------------------------------------------------------------------

// (d) = (s)
macro move8 s:0, d:0 {
	lda s
	sta d
}

// (d+X) = (s)
macro move8 s:0, d:0,x {
	lda s
	sta d,x
}

// (d+Y) = (s)
macro move8 s:0, d:0,y {
	lda s
	sta d,y
}

// (d) = (s+X)
macro move8 s:0,x, d:0 {
	lda s,x
	sta d
}

// (d+X) = (s+X)
macro move8 s:0,x, d:0,x {
	lda s,x
	sta d,x
}

// (d+Y) = (s+X)
macro move8 s:0,x, d:0,y {
	lda s,x
	sta d,y
}

// (d) = (s+Y)
macro move8 s:0,y, d:0 {
	lda s,y
	sta d
}

// (d+X) = (s+Y)
macro move8 s:0,y, d:0,x {
	lda s,y
	sta d,x
}

// (d+Y) = (s+Y)
macro move8 s:0,y, d:0,y {
	lda s,y
	sta d,y
}

// (d) = (s) using X
macro move8x s:0, d:0 {
	ldx s
	stx d
}

// (d) = i
macro move8 #i:8, d:0 {
	lda #i
	sta d
}

// (d+X) = i
macro move8 #i:8, d:0,x {
	lda #i
	sta d,x
}

// (d+Y) = i
macro move8 #i:8, d:0,y {
	lda #i
	sta d,y
}

// (d) = i using X
macro move8x #i:8, d:0 {
	ldx #i
	stx d
}

// (d) = (d) + (s)
macro add8 s:0, d:0 {
	lda s
	adc d
	sta d
}

// (d) = (d) + i
macro add8 #i:8, d:0 {
	lda #i
	adc d
	sta d
}

// (d) = (d) + A
macro adda8 d:0 {
	adc d
	sta d
}

// (d) = (d) - (s)
macro sub8 s:0, d:0 {
	lda d
	sbc s
	sta d
}

// (d) = (d) - i
macro sub8 #i:8, d:0 {
	lda d
	sbc #i
	sta d
}

// (d) = (d) - A
macro suba8 d:0 {
	eor #$ff
	adc d
	sta d
}

// (d) exchange value with (s) via stack
macro swap8 s:0, d:0 {
	lda d
	pha
	lda s
	sta d
	pla
	sta s
}

// (d) exchange value with (s), garbage X
macro swap8x s:0, d:0 {
	ldx d
	lda s
	sta d
	stx s
}

// (d) = (d) * (s), uses one byte at (tmp)
macro mul8 s:0, d:0, tmp:0 {
	lda s
	sta tmp
	lda #$00
	beq .enter
.doadd:
	clc
	adc tmp
.loop:
	asl tmp
.enter:
	lsr d
	bcs .doadd
	bne .loop
	sta d
}

// (d) = (d) * i
macro mul8 #i:8, d:0 {
	if i == 0
		lda #$00
		sta d
	elseif i == 1
		//Do nothing
	else
	
		if i & %00000001
			lda d
		else
			lda #$00
		endif

		if i >= %00000010
			asl d
		endif 
		
		if i & %00000010
			clc
			adc d
		endif
		
		if i >= %00000100
			asl d
		endif 
		
		if i & %00000100
			clc
			adc d
		endif
		
		if i >= %00001000
			asl d
		endif 
		
		if i & %00001000
			clc
			adc d
		endif
		
		if i >= %00010000
			asl d
		endif 
		
		if i & %00010000
			clc
			adc d
		endif
		
		if i >= %00100000
			asl d
		endif 
		
		if i & %00100000
			clc
			adc d
		endif
		
		if i >= %01000000
			asl d
		endif 
		
		if i & %01000000
			clc
			adc d
		endif
		
		if i >= %10000000
			asl d
			clc
			adc d
		endif 
		
		sta d
		
	endif
}

// (Stack) = (s)
macro push8 s:0
{
	lda s
	pha
}

// (Stack) = i
macro push8 #i:8
{
	lda #i
	pha
}

// (d) = (Stack)
macro pop8 d:0
{
	pla
	sta d
}

//-------------------------------------------------------------------------------
// 16 bit macros
//-------------------------------------------------------------------------------

// (d) = (s)
macro move16 s:0, d:0 {
	lda s
	sta d
	lda s+1
	sta d+1
}

// (d+X) = (s)
macro move16 s:0, d:0,x {
	lda s
	sta d,x
	lda s+1
	sta d+1,x
}

// (d+Y) = (s)
macro move16 s:0, d:0,y {
	lda s
	sta d,y
	lda s+1
	sta d+1,y
}

// (d) = (s+X)
macro move16 s:0,x , d:0 {
	lda s,x
	sta d
	lda s+1,x
	sta d+1
}

// (d+X) = (s+X)
macro move16 s:0,x , d:0,x {
	lda s,x
	sta d,x
	lda s+1,x
	sta d+1,x
}

// (d+Y) = (s+X)
macro move16 s:0,x , d:0,y {
	lda s,x
	sta d,y
	lda s+1,x
	sta d+1,y
}

// (d) = (s+Y)
macro move16 s:0,y , d:0 {
	lda s,y
	sta d
	lda s+1,y
	sta d+1
}

// (d+X) = (s+Y)
macro move16 s:0,y , d:0,x {
	lda s,y
	sta d,x
	lda s+1,y
	sta d+1,x
}

// (d+Y) = (s+Y)
macro move16 s:0,y , d:0,y {
	lda s,y
	sta d,y
	lda s+1,y
	sta d+1,y
}

// (d) = (s) using X
macro move16x s:0, d:0 {
	ldx s
	stx d
	ldx s+1
	stx d+1
}

// (d) = i
macro move16 #i:0, d:0 {
	lda #i&255
	sta d
if def i
	if (i >> 8) != (i & 255)
		lda #i>>8
	endif
else
	lda #i>>8
endif
	sta d+1
}

// (d+X) = i
macro move16 #i:0, d:0,x {
	lda #i&255
	sta d,x
if def i
	if (i >> 8) != (i & 255)
		lda #i>>8
	endif
else
	lda #i>>8
endif
	sta d+1,x
}

// (d+Y) = i
macro move16 #i:0, d:0,y {
	lda #i&255
	sta d,y
if def i
	if (i >> 8) != (i & 255)
		lda #i>>8
	endif
else
	lda #i>>8
endif
	sta d+1,y
}

// (d) = i using X
macro move16x #i:0, d:0 {
	ldx #i&255
	stx d
if def i
	if (i >> 8) != (i & 255)
		ldx #i>>8
	endif
else
	ldx #i>>8
endif
	stx d+1
}

// (d) = (d) + 1
macro inc16 d:0 {
	inc d
	bne .br
	inc d+1
.br:
}

// (d) = (d) + 1, branch to br if high byte is unaffected
macro inc16 d:0, br:16 {
	inc d
	bne br
	inc d+1
}

// (d+X) = (d+X) + 1
macro inc16 d:0,x {
	inc d,x
	bne .br
	inc d+1,x
.br:
}

// (d+X) = (d+X) + 1, branch to br if high byte is unaffected
macro inc16 d:0,x , br:16 {
	inc d,x
	bne br
	inc d+1,x
}

// (d) = (d) - 1
macro dec16 d:0 {
	lda d
	bne .br
	dec d+1
.br:
	dec d
}

// (d) = (d) - 1, branch to br if high byte is unaffected
macro dec16 d:0, br:16 {
	dec d
	bpl br
	dec d+1
}

// (d+X) = (d+X) - 1
macro dec16 d:0,x {
	dec d,x
	bpl .br
	dec d+1,x
.br:
}

// (d+X) = (d+X) - 1, branch to br if high byte is unaffected
macro dec16 d:0,x , br:16 {
	dec d,x
	bpl br
	dec d+1,x
}

// (d) = (d) + (s)
macro add16 s:0, d:0 {
	lda s
	adc d
	sta d
	lda s+1
	adc d+1
	sta d+1
}

// (d) = (d) + i
macro add16 #i:8, d:0 {
	lda #i
	adc d
	sta d
	bcc .br1
	inc d+1
.br1:
}

// (d) = (d) + i, branch to br if high byte is unaffected
macro add16 #i:8, d:0, br:16 {
	lda #i
	adc d
	sta d
	bcc br
	inc d+1
}

// Macro overload for 16 bit immidiate value
// (d) = (d) + i
macro add16 #i:16, d:0 {
	lda #i&255
	adc d
	sta d
	lda #i>>8
	adc d+1
	sta d+1
}

// (d+X) = (d+X) + i
macro add16 #i:8, d:0,x {
	lda #i
	adc d,x
	sta d,x
	bcc .br1
	inc d+1,x
.br1:
}

// (d+X) = (d+X) + i, branch to br if high byte is unaffected
macro add16 #i:8, d:0,x , br:16 {
	lda #i
	adc d,x
	sta d,x
	bcc br
	inc d+1,x
}

// (d+Y) = (d+Y) + i
macro add16 #i:8, d:0,y {
	lda #i
	adc d,y
	sta d,y
	lda #0
	adc d+1,y
	sta d+1,y
}

// Macro overload for 16 bit immidiate value
// (d+X) = (d+X) + i
macro add16 #i:16, d:0,x {
	lda #i&255
	adc d,x
	sta d,x
	lda #i>>8
	adc d+1,x
	sta d+1,x
}

// Macro overload for 16 bit immidiate value
// (d+Y) = (d+Y) + i
macro add16 #i:16, d:0,y {
	lda #i&255
	adc d,y
	sta d,y
	lda #i>>8
	adc d+1,y
	sta d+1,y
}

// (d) = (d) + A
macro adda16 d:0 {
	adc d
	sta d
	bcc .br1
	inc d+1
.br1:
}

// (d) = (d) + A, branch to br if high byte is unaffected
macro adda16 d:0, br:16 {
	adc d
	sta d
	bcc br
	inc d+1
}

// (d+X) = (d+X) + A
macro adda16 d:0,x {
	adc d,x
	sta d,x
	bcc .br1
	inc d+1,x
.br1:
}

// (d+X) = (d+X) + A, branch to br if high byte is unaffected
macro adda16 d:0,x , br:16 {
	adc d,x
	sta d,x
	bcc br
	inc d+1,x
}

// (d) = (d) - (s)
macro sub16 s:0, d:0 {
	lda d
	sbc s
	sta d
	lda d+1
	sbc s+1
	sta d+1
}

// (o) = (d) - (s)
macro sub16 s:0, d:0, o:0 {
	lda d
	sbc s
	sta o
	lda d+1
	sbc s+1
	sta o+1
}

// (d) = (d) - i
macro sub16 #i:8, d:0 {
	lda d
	sbc #i
	sta d
	bcs .br1
	dec d+1
.br1:
}

// (d) = (d) - i, branch to br if high byte is unaffected
macro sub16 #i:8, d:0, br:16 {
	lda d
	sbc #i
	sta d
	bcs br
	dec d+1
}

// Macro overload for 16 bit immidiate value
// (d) = (d) - i
macro sub16 #i:16, d:0 {
	lda d
	sbc #i&255
	sta d
	lda d+1
	sbc #i>>8
	sta d+1
}

// (d+X) = (d+X) - i
macro sub16 #i:8, d:0,x {
	lda d,x
	sbc #i
	sta d,x
	bcs .br1
	dec d+1,x
.br1:
}

// (d+X) = (d+X) - i, branch to br if high byte is unaffected
macro sub16 #i:8, d:0,x , br:16 {
	lda d,x
	sbc #i
	sta d,x
	bcs br
	dec d+1,x
}

// (d+Y) = (d+Y) - i
macro sub16 #i:8, d:0,y {
	lda d,y
	sbc #i
	sta d,y
	lda d+1,y
	sbc #0
	sta d+1,y
}

// Macro overload for 16 bit immidiate value
// (d+X) = (d+X) - i
macro sub16 #i:16, d:0,x {
	lda d,x
	sbc #i&255
	sta d,x
	lda d+1,x
	sbc #i>>8
	sta d+1,x
}

// Macro overload for 16 bit immidiate value
// (d+Y) = (d+Y) - i
macro sub16 #i:16, d:0,y {
	lda d,y
	sbc #i&255
	sta d,y
	lda d+1,y
	sbc #i>>8
	sta d+1,y
}

// (d) = (d) - A
macro suba16 d:0 {
	eor #$ff
	adc d
	sta d
	bcs .br1
	dec d+1
.br1:
}

// (d) = (d) - A, branch to br if high byte is unaffected
macro suba16 d:0, br:16 {
	eor #$ff
	adc d
	sta d
	bcs br
	dec d+1
}

// (d+X) = (d+X) - A
macro suba16 d:0,x {
	eor #$ff
	adc d,x
	sta d,x
	bcs .br1
	dec d+1,x
.br1:
}

// (d+X) = (d+X) - A, branch to br if high byte is unaffected
macro suba16 d:0,x , br:16 {
	eor #$ff
	adc d,x
	sta d,x
	bcs br
	dec d+1,x
}

// (d) exchange value with (s) via stack
macro swap16 s:0, d:0 {
	lda d
	pha
	lda s
	sta d
	pla
	sta s
	lda d+1
	pha
	lda s+1
	sta d+1
	pla
	sta s+1
}

// (d) exchange value with (s), garbage X
macro swap16x s:0, d:0 {
	ldx d
	lda s
	sta d
	stx s
	ldx d+1
	lda s+1
	sta d+1
	stx s+1
}

// (d) != (s), branch to br if not equal
macro cmp16bne s:0, d:0, br:16 {
	lda s
	cmp d
	bne br
	lda s+1
	cmp d+1
	bne br
}

// (d) != i, branch to br if not equal
macro cmp16bne #i:0, d:0, br:16 {
	lda #i&255
	cmp d
	bne br
	lda #i>>8
	cmp d+1
	bne br
}

// (d) == (s), branch to br if equal
macro cmp16beq s:0, d:0, br:16 {
	lda s
	cmp d
	bne .br
	lda s+1
	cmp d+1
	beq br
.br:
}

// (d) == (s), branch to br if equal
macro cmp16beq #i:0, d:0, br:16 {
	lda #i&255
	cmp d
	bne .br
	lda #i>>8
	cmp d+1
	beq br
.br:
}

// Uses 8 bits of (s) and 16 bits of (d)
// (d) = (d) * (s), uses one byte at (tmp), garbage X
macro mul16 s:0, d:0, tmp:0 {
	lda s
	sta tmp
	lda #$00
	tax
	beq .enter
.doadd:
	clc
	adc d
	pha
	txa
	adc d+1
	tax
	pla
.loop:
	asl d
	rol d+1
.enter:
	lsr tmp
	bcs .doadd
	bne .loop
	sta d
	stx d+1
}

// (Stack) = (s)
macro push16 s:0
{
	lda s
	pha
	lda s+1
	pha
}

// (Stack) = i
macro push16 #i:16
{
	lda #i & 255
	pha
	lda #i >> 8
	pha
}

// (d) = (Stack)
macro pop16 d:0
{
	pla
	sta d+1
	pla
	sta d
}

macro asl16 s:0
{
	asl s
	rol s+1
}

macro lsr16 s:0
{
	lsr s+1
	ror s
}

; normal binary division
; 8bit/8bit division
;
; Input: num, denom in zeropage
; Output: num = quotient, .A = remainder
macro div8_8 num:0 denom:0
{
		lda #$00
		ldx #$07
		clc
.loop: 	rol num
		rol
		cmp denom
		bcc .skip
			sbc denom
.skip: 	dex
		bpl .loop
		rol num
}

; denom or divisor is #imm (constant)
macro div8_8c num:0 #denom:8
{
		lda #$00
		ldx #$07
		clc
.loop: 	rol num
		rol
		cmp #denom
		bcc .skip
			sbc #denom
.skip: 	dex
		bpl .loop
		rol num
}

; 19 bytes
;
;  Best case  = 154 cycles
;  Worst case = 170 cycles
;
; With immediate denom:
;  Best case  = 146 cycles 
;  Worst case = 162 cycles
;
; Unrolled with variable denom:
;  Best case  = 106 cycles
;  Worst case = 127 cycles
;
; Unrolled with immediate denom:
;  Best case  =  98 cycles
;  Worst case = 111 cycles



; Best practice for ZP usage example given.
; divisor 		= word     
; dividend 		= word	
; remainder 	= word	
; result 	  =>  dividend ;save memory by reusing divident to store the result


; IN/OUT dividend will be truncated and replaced with the result
; IN divisor not touched
; OUT remainder will hold left over.

; Unsigned Division 16 bit with 16 bit
; example of use 
; div16 velocityX, divX, remainder      => result 
;--------------------
macro udiv16_16 dividend:0, divisor:0, remainder:0
{
			lda #$00        		; Reset remainder
			sta remainder
			sta remainder+1
			ldx #16	        		; Repeat for each bit: ...
.divloop:		asl dividend		; Dividend lb & hb*2, msb -> Carry
				rol dividend+1	
				rol remainder		; Remainder lb & hb * 2 + msb from carry
				rol remainder+1
				lda remainder
				sec
				sbc divisor			; Substract divisor to see if it fits in
				tay	        		; lb result -> Y, for we may need it later
				lda remainder+1
				sbc divisor+1
				bcc .skip			;If carry=0 then divisor didn't fit in yet
					sta remainder+1	;Else save substraction result as new remainder,
					sty remainder	
					inc dividend	;And INCrement result cause divisor fit in 1 times
.skip:		dex
			bne .divloop	
}

; --- unsigned division 16 bit using divisor as a immediate value (constant)
macro udiv16_16c dividend:0, #divisor:16, remainder:0
{
			lda #$00        		; Reset remainder
			sta remainder
			sta remainder+1
			ldx #16	        		; Repeat for each bit: ...
.divloop:		asl dividend		; Dividend lb & hb*2, msb -> Carry
				rol dividend+1	
				rol remainder		; Remainder lb & hb * 2 + msb from carry
				rol remainder+1
				lda remainder
				sec
				sbc #(divisor)&255			; Substract divisor to see if it fits in
				tay	        		; lb result -> Y, for we may need it later
				lda remainder+1
				sbc #(divisor)>>8
				bcc .skip			;If carry=0 then divisor didn't fit in yet
					sta remainder+1	;Else save substraction result as new remainder,
					sty remainder	
					inc dividend	;And INCrement result cause divisor fit in 1 times
.skip:		dex
			bne .divloop	
}

; --- signed division (+/-) 16 bit using divisor as a zp val

macro div16_16 dividend:0, divisor:0, remainder:0		
{
			lda dividend+1
			sta .sign+1
			bpl .cont
				// signed => negate
				lax #$00
				sec
				sbc dividend
				sta dividend
				txa
				sbc dividend+1
				sta dividend+1			
				
.cont:		udiv16 dividend, divisor, remainder

.sign:		lda #$AB
			bpl .uns
				// signed => negate
				lax #$00
				sec
				sbc dividend
				sta dividend
				txa
				sbc dividend+1
				sta dividend+1
.uns:			
}

; --- signed division (+/-) 16 bit using divisor as a immediate value (constant)

macro div16_16c dividend:0, #divisor:16, remainder:0		
{
			lda dividend+1
			sta .sign+1
			bpl .cont
				// signed => negate
				lax #$00
				sec
				sbc dividend
				sta dividend
				txa
				sbc dividend+1
				sta dividend+1			
				
.cont:		udiv16c dividend, divisor, remainder

.sign:		lda #$AB
			bpl .uns
				// signed => negate
				lax #$00
				sec
				sbc dividend
				sta dividend
				txa
				sbc dividend+1
				sta dividend+1
.uns:			
}

//	rts

endif //_WORD_INCLUDED
