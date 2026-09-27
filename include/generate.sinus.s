	
		; ----- generate.sinus --------------------------------------------------------------------------
		;
		;   generates a sine, 256 bytes, at tbl_sine
		;   from 0 to sinus.max
		;   sinus.max can be $10, $20, $40, $80 and $100
		;
		;   code taken from a 256byte demo by Cruzer
		;
		
				
		const sinus.max = $100

generate.sinus:	ldx #0
				ldy #$7f
		
.sineval1:		lda #0
				clc
.sineadd1:		adc #0
				sta .sineval1+1

.sineval2:		lda #0
.sineadd2:		adc #0
				sta .sineval2+1
	
				sta tbl_sine+$80,x
				sta tbl_sine+$00,y
				eor #sinus.max-1
				sta tbl_sine+$00,x
				sta tbl_sine+$80,y
			
				lda .sineadd1+1
				adc #sinus.max >> 4
				sta .sineadd1+1
				bcc .nocarry
				inc .sineadd2+1

.nocarry:		inx
				dey
				cpx #$40
				bne .sineval1
				rts
		
.align 256
tbl_sine:	.fillbyte 0, 256	
