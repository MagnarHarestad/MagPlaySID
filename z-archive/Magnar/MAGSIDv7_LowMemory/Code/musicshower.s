		
			; ---  MAGSID MusicShower - Coded by Magnar Harestad

		; Tells Tables.s to emit note_display - only this file needs it, and only m_tables (which
		; runs after this include) can emit it.
		lua { v_musicshower = true }

		; m_show_a prints whatever is in A as two hex digits, so there is one copy of the nibble
		; split rather than one per value. Uses this file's own "numbers" table, not the library
		; m_hexnumber, which carries a private 16-byte copy of it into every call site.

		; Literal columns, ahead of the macros that read them - a macro body wants its symbols
		; already known. Never executed: the IRQ is entered by vector, at IRQ_Main below.
text_noteoff:	byte "-", "-", "-"
text_notewait:	byte ".", ".", "."
text_yesno:	byte "y", "n"

macro m_show_a screen:16 {
		pha
		lsr
		lsr
		lsr
		lsr
		tax
		lda numbers,x
		sta screen
		pla
		and #$0f
		tax
		lda numbers,x
		sta screen + 1
}

macro m_show_nib_a screen:16 {
		and #$0f
		tax
		lda numbers,x
		sta screen
}

		; The note column. voice_note holds a dictionary id, which says nothing about pitch alone -
		; note_display (Tables.s) maps it to the 0..95 index note_text1/2/3 use. "---" is a note off,
		; "..." a wait row that overwrote the note.
macro m_show_note ch:8, screen:16 {
		lda voice_note + ch
		beq .wait                              ; nothing has played on this channel yet
		bpl .real
		cmp #CMD_OFF
		bne .wait
		ldx #2
.offloop:
		lda text_noteoff,x
		sta screen,x
		dex
		bpl .offloop
		jmp .done
.wait:
		ldx #2
.waitloop:
		lda text_notewait,x
		sta screen,x
		dex
		bpl .waitloop
		jmp .done
.real:
		tay
		lda note_display,y
		tay
		lda note_text1,y
		sta screen + 0
		lda note_text2,y
		sta screen + 1
		lda note_text3,y
		sta screen + 2
.done:
}

		; Whether a retrigger on this channel would hard-restart: NOHR rides in bit 5 of the
		; sounding instrument's own pw_init byte, which voice_prev_pw keeps.
macro m_show_hardreset ch:8, screen:16 {
		ldx #0
		lda voice_prev_pw + ch
		and #$20
		beq .put
		inx
.put:
		lda text_yesno,x
		sta screen
}


			; m_wait_supportcode
IRQ_Main:
			m_stableraster_set 

			m_delay 63-9

			move8 #$0b, $d020

			move8 $d012, ZP.First_D012
			jsr addr.music.play
			move8 $d012, ZP.Second_D012

			move8 #$00, $d020

			lda ZP.Second_D012
			sec
			sbc ZP.First_D012
			sta ZP.SpentRastertime
			lsr
			lsr
			lsr
			lsr
			tax
			move8 numbers,x, $0400+27 + $28*2
			lda ZP.SpentRastertime
			and #$0f
			tax
			move8 numbers,x, $0401+27 + $28*2

			lda ZP.SpentRastertime
			cmp ZP.MaxRastertime
			bcc !+
			sta ZP.MaxRastertime
!:			lda ZP.MaxRastertime
			lsr
			lsr
			lsr
			lsr
			tax
			move8 numbers,x, $0400+17 + $28*2
			lda ZP.MaxRastertime
			and #$0f
			tax
			move8 numbers,x, $0401+17 + $28*2



/*			

			for(i=1 : i<v_channels+1 : i=i+1) { 
				var .y = 4
				m_hexnumber Song_Pos, i,        $0400 + ($28*(.y)) + 15 + ((i-1)*3)+i
			}			
*/


			; Player state, three columns at screen cols 16, 20 and 24. Unrolled per channel with
			; "array + .ch": each is a three-byte array, so the channel is an assembly-time offset.
			; After the rastertime measurement, which brackets music.play alone.
			for (.ch = 0 : .ch < v_channels : .ch = .ch + 1) {
				lda voice_orderpos + .ch
				m_show_a $0400 + $28*4 + 16 + .ch*4
				lda voice_patternidx + .ch
				m_show_a $0400 + $28*5 + 16 + .ch*4
				lda voice_event + .ch
				m_show_a $0400 + $28*6 + 16 + .ch*4

				; Ticks left on the current row over the row's own length, which is what the
				; "/" already sitting in this line is for.
				lda voice_duration + .ch
				m_show_a $0400 + $28*7 + 16 + .ch*4
				lda voice_rowdur + .ch
				m_show_a $0400 + $28*7 + 28 + .ch*4

				m_show_note .ch, $0400 + $28*9 + 16 + .ch*4

				; Instruments are authored 01..16 but voice_inst holds 0..15, so show it the
				; way the song file spells it.
				lda voice_inst + .ch
				clc
				adc #1
				m_show_a $0400 + $28*11 + 16 + .ch*4

				ldy voice_inst + .ch
				lda ad_table,y
				m_show_a $0400 + $28*12 + 16 + .ch*4
				ldy voice_inst + .ch
				lda sr_table,y
				m_show_a $0400 + $28*13 + 16 + .ch*4

				lda voice_last_waveform + .ch
				m_show_a $0400 + $28*14 + 16 + .ch*4

				; 12 bits: the high nibble is one digit, the shadow low byte two more.
				lda voice_pw_hi + .ch
				m_show_nib_a $0400 + $28*15 + 16 + .ch*4
				lda voice_pw_lo + .ch
				m_show_a $0400 + $28*15 + 17 + .ch*4

				m_show_hardreset .ch, $0400 + $28*16 + 16 + .ch*4
			}

			; The filter is a single global, not per channel - one column each.
			lda show_d418
			m_show_a $0400 + $28*18 + 16
			lda show_d417
			m_show_a $0400 + $28*19 + 16
			lda show_d416
			m_show_a $0400 + $28*20 + 16
			lda filter_delta
			m_show_a $0400 + $28*21 + 16

			move8 #v_d012, $d012
			move16 #IRQ_Main, $fffe
			m_irq_restore_regs

			.align $100
numbers:
			byte $30,$31,$32,$33,$34,$35,$36,$37,$38,$39,1,2,3,4,5,6

note_text1:	
			byte "c", "c", "d", "d", "e", "f", "f", "g", "g", "a", "a", "h"
			byte "c", "c", "d", "d", "e", "f", "f", "g", "g", "a", "a", "h"
			byte "c", "c", "d", "d", "e", "f", "f", "g", "g", "a", "a", "h"
			byte "c", "c", "d", "d", "e", "f", "f", "g", "g", "a", "a", "h"
			byte "c", "c", "d", "d", "e", "f", "f", "g", "g", "a", "a", "h"
			byte "c", "c", "d", "d", "e", "f", "f", "g", "g", "a", "a", "h"
			byte "c", "c", "d", "d", "e", "f", "f", "g", "g", "a", "a", "h"
			byte "c", "c", "d", "d", "e", "f", "f", "g", "g", "a", "a", "h"
note_text2:	
			byte "-", "#", "-", "#", "-", "-", "#", "-", "#", "-", "#", "-"
			byte "-", "#", "-", "#", "-", "-", "#", "-", "#", "-", "#", "-"
			byte "-", "#", "-", "#", "-", "-", "#", "-", "#", "-", "#", "-"
			byte "-", "#", "-", "#", "-", "-", "#", "-", "#", "-", "#", "-"
			byte "-", "#", "-", "#", "-", "-", "#", "-", "#", "-", "#", "-"
			byte "-", "#", "-", "#", "-", "-", "#", "-", "#", "-", "#", "-"
			byte "-", "#", "-", "#", "-", "-", "#", "-", "#", "-", "#", "-"
			byte "-", "#", "-", "#", "-", "-", "#", "-", "#", "-", "#", "-"
note_text3:	
			byte "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0"
			byte "1", "1", "1", "1", "1", "1", "1", "1", "1", "1", "1", "1"
			byte "2", "2", "2", "2", "2", "2", "2", "2", "2", "2", "2", "2"
			byte "3", "3", "3", "3", "3", "3", "3", "3", "3", "3", "3", "3"
			byte "4", "4", "4", "4", "4", "4", "4", "4", "4", "4", "4", "4"
			byte "5", "5", "5", "5", "5", "5", "5", "5", "5", "5", "5", "5"
			byte "6", "6", "6", "6", "6", "6", "6", "6", "6", "6", "6", "6"
			byte "7", "7", "7", "7", "7", "7", "7", "7", "7", "7", "7", "7"