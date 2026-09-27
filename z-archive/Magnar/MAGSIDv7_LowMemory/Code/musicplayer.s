		; ---  MAGSID v6 - Coded by Magnar Harestad with great help from Robert Lundquist (Krikkit) and Fredrik Ternell (Swallow) of Censor Design!

		; ------------------------------------------------------------
		; MAGSID Low Memory Player
		; Designed for Lua-generated tables
		; Krikkit C6510 Assembler
		; ------------------------------------------------------------

		// Some helpful Debug stuff:
		// if (v_voice1 == true && i == 1) || (v_voice1 == false && v_voice2 == true && i == 2) || (v_voice1 == false && v_voice2 == false && v_voice3 == true && i == 3)
		// m_spacebar_keyonce

		; Pattern stream: variable-length rows. A note is one byte using the current instrument and
		; duration; only a change emits a command. $00 end, $01-$7F note id, $80-$BF duration 1..64,
		; $C0-$CF instrument 1..16, $D0 off, $D1 wait, $D2 loop end + count + target, $D3-$FF free.
		var CMD_DUR   = $80
		var CMD_INST  = $c0
		var CMD_OFF   = $d0
		var CMD_WAIT  = $d1
		var LOOP_END  = $d2

		; $00 terminates both the note stream and the song-order stream, and costs nothing
		; to test for - the lda that fetches the byte already sets Z, so a bare beq does it.

		; voice_note holds a note id or one of these two - deliberately the same bytes the stream
		; uses, so Voice_Tick stores the command straight from A and falls through to .playrow.
		; ROW_WAIT spends the row's duration without retriggering anything.
		var NOTE_OFF  = CMD_OFF                ; "---"
		var ROW_WAIT  = CMD_WAIT


		; ZP_lb/ZP_hb point at the current channel's row table while an event is fetched. ZP_ch
		; holds the channel index across the two places in Voice_Tick that hand X to a pattern or
		; instrument index and take it back.

		; Every per-channel array is in zero page: "array,x" costs 2 bytes there against 3, worth a
		; measured 83 across the player. Only the zero-starting ones moved - music.init clears them,
		; since ZP is not loaded. addr.music.zp relocates the block ($20 default, 49 bytes).
		if !def addr.music.zp
		var addr.music.zp = $20
		endif

		m_ZP.b "ZP_lb"
		m_ZP.b "ZP_hb"
		m_ZP.b "ZP_ch"

		m_ZP.b "Song_Speed"
		lua {
			-- One name per byte is what m_ZP.b takes, so the three-channel arrays are declared
			-- as a name plus two anonymous followers - consecutive, which is all "array,x" needs.
			for _, name in ipairs({ "voice_patternidx", "voice_event", "voice_duration",
			                        "voice_rowdur", "voice_note", "voice_wt_pos", "voice_arp_pos",
			                        "voice_root", "voice_pw_lo", "voice_pw_hi", "voice_inst",
			                        "voice_prev_pw", "voice_loop_counter", "voice_last_waveform" }) do
				table.insert(__ZPs, name)
				table.insert(__ZPs, name .. "_2")
				table.insert(__ZPs, name .. "_3")
			end
		}
		; Slide/vibrato state, same treatment and only when the song uses them. All zero at a
		; trigger, so music.init's loop covers them and Voice_PlayCurrent re-arms them per note.
		if def v_freqmod
		lua {
			for _, name in ipairs({ "voice_freq_lo", "voice_freq_hi", "voice_off_lo", "voice_off_hi" }) do
				table.insert(__ZPs, name)
				table.insert(__ZPs, name .. "_2")
				table.insert(__ZPs, name .. "_3")
			end
		}
		endif
		if def v_vibrato
		lua {
			for _, name in ipairs({ "voice_vib_dir", "voice_vib_cnt", "voice_vib_delay" }) do
				table.insert(__ZPs, name)
				table.insert(__ZPs, name .. "_2")
				table.insert(__ZPs, name .. "_3")
			end
		}
		endif

		m_ZP.b "filter_pos"
		m_ZP.b "filter_phase"
		m_ZP.b "filter_delta"

		m_CreateZPs "up", addr.music.zp, "Zero Page MusicPlayer"
		lua asm('const ZP_Player_Start = ', ZPstart)
		lua asm('const ZP_Player_Amount = ', ZPamount)


		; Set ZP_lb/ZP_hb from a lo/hi pointer pair indexed by the pattern number in X. Only the row
		; tables need it - their streams run past 256 bytes. The song order fits one byte and uses a
		; plain "abs,y" instead, see Voice_AdvanceOrder.

		macro m_setptr_x lo:16 hi:16 {
			move8 lo,x, ZP_lb
			move8 hi,x, ZP_hb
		}


		; --- MUSIC PLAYER LOCATION --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
		org addr.music.loc, "M_Music Player - JMPs for Init and Play"
		jmp music.init
		jmp music.play


		; Shared per-voice subroutines. X = channel (0,1,2) on entry to all three and preserved.
		; Song order: $00 ends a list, the next byte is the absolute restart position. All three
		; channels' lists share one array, so voice_orderpos indexes it absolutely (255 bytes max).

		org @, "M_Music Player - Shared voice subroutines"

Voice_NoteOff:
		ldy SID_channel_offset,x
		lda voice_last_waveform,x
		and #$fe                               ; clear gate bit only - keep the waveform-select
		sta $d404,y                            ; bits set, or the oscillator goes instantly
		move8 #0, voice_wt_pos,x                ; silent and masks the envelope's release entirely.
		rts                                    ; Stopping the wavetable matters just as much: it
		                                       ; writes the control register every frame and would
		                                       ; put the gate straight back on.

Voice_AdvanceOrder:
.read:
		ldy voice_orderpos,x
		lda tbl_songorder,y
		beq .endoflist                         ; the common path falls through
		sta voice_patternidx,x
		inc voice_orderpos,x                   ; not iny/tya/sta - Y is dead from here, so
		                                       ; incrementing the array in place is 2 bytes
		                                       ; and 2 cycles cheaper than routing it via Y
		lda #0
		sta voice_event,x
		sta voice_loop_counter,x
		rts
.endoflist:
		iny
		lda tbl_songorder,y                    ; absolute restart position
		sta voice_orderpos,x
		jmp .read

; A = note dictionary id, X = channel. Writes the note's frequency and returns with Y
; holding that channel's SID offset, which both callers go on to use.

Voice_SetFreq:
		tay                                    ; Y = note id, index into note_freq_low/high
		lda note_freq_low,y
		pha                                    ; the stack carries the lo byte across the Y
		lda note_freq_high,y                    ; switch from note id to SID channel offset,
		ldy SID_channel_offset,x                ; which is cheaper than bouncing it via ZP
		sta $d401,y
		if def v_freqmod
		; The SID's frequency registers cannot be read back, and vibrato/slide write base + offset
		; over whatever a note or arpeggio step just wrote - so the base is kept here, the one
		; place every frequency write passes through.
		sta voice_freq_hi,x
		endif
		pla
		sta $d400,y
		if def v_freqmod
		sta voice_freq_lo,x
		endif
		rts

Voice_PlayCurrent:
		lda voice_note,x
		cmp #NOTE_OFF
		beq Voice_NoteOff
		bcs .leavesounding                     ; wait row: let whatever is playing keep going.
		                                       ; bcs, not a second "cmp #ROW_WAIT" - the cmp above
		                                       ; already left C=1 for everything above $D0, so one
		                                       ; branch separates all three cases.

		sta voice_root,x                       ; the row's own note, kept for a ROOT step in the
		                                       ; note program. voice_note cannot serve: a later
		                                       ; wait row overwrites it with ROW_WAIT, which is
		                                       ; not an index into the frequency table.

		; NOHR check before Voice_SetFreq clobbers A/Y: bit 5 of the pw_init byte belonging to the
		; instrument ALREADY sounding here, which voice_prev_pw keeps - its envelope rate is the one
		; the reset pair risks colliding with. The result rides the stack across the jsr.
		tay                                    ; park the note in Y rather than reloading it
		lda voice_prev_pw,x                    ; from voice_root below - Y is free until the
		and #$20                               ; jsr, and tya is 2 bytes cheaper than the load
		pha
		tya

		jsr Voice_SetFreq                      ; SetFreq leaves Y = this channel's SID offset.

		; Hard restart unless NOHR: zero AD, release 7, gate off + TEST. Release 7 and not 0 because
		; the SID's envelope rate counter is never reset by a write - a period below where it already
		; sits wraps it through 15 bits and the next attack stalls. NOHR skips all three writes.
		pla
		bne .skiphardreset
		sta $d405,y                            ; A is already $00 here - it is the "and #$20"
		                                       ; result the bne just proved to be zero, so the
		                                       ; usual "lda #$00" in front of this is dead
		lda #$07
		sta $d406,y
		lda #$08
		sta $d404,y
.skiphardreset:

		lda voice_inst,x
		tax                                    ; X = instrument nr, channel parked in ZP_ch
		lda pw_init,x                          ; parked on the stack: A and Y are both busy
		pha                                    ; carrying wt_start and arp_start down to the
		lda wt_start,x                         ; stores below
		ldy flt_start,x
		beq .nofilter                          ; instrument leaves the filter alone
		sty filter_pos
.nofilter:
		ldy arp_start,x                        ; 0 for an instrument with no arpeggio
		ldx ZP_ch
		sta voice_wt_pos,x
		tya
		sta voice_arp_pos,x
		if def v_vibrato
		; Arm this note's vibrato: delay from vib_ds's top nibble (x4 frames), first run half a
		; speed long (low nibble >> 1), which centres the wave on the note.
		ldy voice_inst,x
		lda vib_ds,y
		and #$f0
		lsr
		lsr
		sta voice_vib_delay,x
		lda vib_ds,y
		and #$0f
		lsr
		sta voice_vib_cnt,x
		lda #0
		sta voice_vib_dir,x
		else
		if def v_slide
		lda #0
		endif
		endif
		if def v_freqmod
		sta voice_off_lo,x                     ; vibrato and slide both start from the bare note
		sta voice_off_hi,x
		endif
		pla
		tay                                    ; park pw_init while testing it - Y is free from
		sta voice_prev_pw,x                    ; here to the end of this routine. Stashed for the
		                                       ; *next* trigger's NOHR test above: this is the
		                                       ; instrument that will be sounding when it runs.
		                                       ; sta, not sty - STY has no absolute,X mode.
		and #$40
		bne .leavesounding                     ; bit 6 set: continuous - the sweep is per
		                                       ; channel, so leave it running across the trigger
		tya
		sta voice_pw_hi,x                      ; the default: a note restarts the sweep from
		lda #0                                 ; the instrument's own starting width
		sta voice_pw_lo,x
.leavesounding:
		rts

; One wavetable step plus ADSR for channel X, every frame rather than every tick - an attack
; transient needs frame resolution. A step byte goes straight to the control register; $00
; holds. Pool offset 0 is a dummy $00, so voice_wt_pos = 0 doubles as "nothing running".

Voice_Automation:
		ldy voice_wt_pos,x
		bne .running                           ; an rts of its own is 2 bytes cheaper than a long
		rts                                    ; branch to the .idle the arpeggio paths still use
.running:
		lda wt_data,y
		beq .adsr                              ; $00 = hold on the current waveform
		ldy SID_channel_offset,x
		sta $d404,y
		sta voice_last_waveform,x              ; remembered for Voice_NoteOff (gate off, waveform kept)
		inc voice_wt_pos,x
.adsr:
		ldy SID_channel_offset,x
		stx ZP_ch
		lda voice_inst,x
		tax                                    ; X = instrument nr, channel parked in ZP_ch
		lda ad_table,x
		sta $d405,y
		lda sr_table,x
		sta $d406,y

		; Pulse width. $D402/$D403 are write-only, so the width lives in a per-channel shadow and is
		; rewritten every frame. The delta is sign-extended into all 12 bits, so the sweep travels the
		; whole register and wraps. X is still the instrument index here.
		lda pw_add,x
		ldx ZP_ch
		pha
		clc
		adc voice_pw_lo,x
		sta voice_pw_lo,x
		sta $d402,y
		pla                                    ; the delta again. None of pla/and/beq/lda
		and #$80                               ; touches the carry flag either
		beq .pwhigh
		lda #$ff                               ; sign extension - this is a downward sweep
.pwhigh:
		adc voice_pw_hi,x
		and #$0f
		sta voice_pw_hi,x
		sta $d403,y

.arpentry:
		; Note program: one note per frame alongside the wavetable. Absolute note ids, except ROOT
		; which plays the row's own note - that pair is what makes a drum or a bass transient
		; expressible.
		ldy voice_arp_pos,x
		if def v_freqmod
		beq .freqmod                           ; instrument has no note program
		else
		beq .idle                              ; instrument has no note program
		endif
.arpstep:
		lda arp_data,y
		beq .arploop                           ; $00 = end marker, next byte says where to resume
		bpl .arpnote                           ; $01..$7F = a note dictionary id
		                                       ; else bit 7 set = exactly $80 = ROOT
.arproot:
		lda voice_root,x
		if def v_freqmod
		jsr Voice_SetFreq
		jmp .freqmod
		else
		jmp Voice_SetFreq
		endif
.arpnote:
		inc voice_arp_pos,x
		if def v_freqmod
		jsr Voice_SetFreq
		jmp .freqmod
		else
		jmp Voice_SetFreq                      ; tail call - it ends in the rts this needs
		endif
.arploop:
		iny
		lda arp_data,y
		sta voice_arp_pos,x
		tay
		bne .arpstep                           ; a target of 0 would stop the arpeggio instead
.idle:
		rts

		if def v_freqmod
		; Frequency modulation: slide and vibrato, last in Voice_Automation because the arpeggio
		; rewrites the bare note every frame. Both move one signed 16-bit offset; slide adds
		; slide_rate a frame, vibrato traces a triangle from vib_ds (delay/speed) and vib_depth.
.freqmod:
		ldy voice_inst,x
		if def v_slide
		if def v_vibrato
		lda slide_rate,y
		ora vib_depth,y
		beq .idle                              ; neither effect on this instrument
		lda slide_rate,y
		beq .noslide
		else
		lda slide_rate,y
		beq .idle
		endif
		; offset += rate, sign-extended. None of pla/and/beq/lda touches the carry the adc
		; produced, the same idiom the pulse-width sweep uses.
		pha
		clc
		adc voice_off_lo,x
		sta voice_off_lo,x
		pla
		and #$80
		beq .slidehigh
		lda #$ff
.slidehigh:
		adc voice_off_hi,x
		sta voice_off_hi,x
		endif
		if def v_vibrato
.noslide:
		lda vib_depth,y
		if def v_slide
		beq .fmwrite                           ; slide moved the offset: still write it
		else
		beq .idle
		endif
		pha
		lda voice_vib_delay,x
		beq .vibrun
		dec voice_vib_delay,x
		pla
		jmp .fmwrite
.vibrun:
		; offset += depth (dir $00) or -= depth (dir $ff). depth EOR dir is depth or ~depth, and
		; the carry - set by the compare exactly when dir is $ff - supplies the +1 that turns
		; ~depth into -depth; dir itself is the sign extension for the high byte.
		lda voice_vib_dir,x
		cmp #1
		pla
		eor voice_vib_dir,x
		adc voice_off_lo,x
		sta voice_off_lo,x
		lda voice_vib_dir,x
		adc voice_off_hi,x
		sta voice_off_hi,x
		dec voice_vib_cnt,x
		bpl .fmwrite
		lda voice_vib_dir,x                    ; run finished: turn round and reload the speed
		eor #$ff
		sta voice_vib_dir,x
		ldy voice_inst,x
		lda vib_ds,y
		and #$0f
		sta voice_vib_cnt,x
		endif
.fmwrite:
		lda voice_freq_lo,x
		clc
		adc voice_off_lo,x
		pha
		lda voice_freq_hi,x
		adc voice_off_hi,x
		ldy SID_channel_offset,x
		sta $d401,y
		pla
		sta $d400,y
		rts
		endif

		; --- MUSIC INIT --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
		org @, "M_Music Player - Init code"
music.init:
		; ZP is not part of the loaded image, so state that used to be initialised by its own
		; ".byte 0,0,0" is cleared here - before the Voice_AdvanceOrder loop, which writes
		; voice_event and voice_loop_counter.
		lda #0
		ldx #ZP_Player_Amount-1
.zerostate:
		sta ZP_Player_Start,x
		dex
		bpl .zerostate

		move8 #$0f, $d418
		if def musicshower
		move8 #$0f, show_d418
		endif
		ldx #v_channels-1
.initchannel:
		jsr Voice_AdvanceOrder                 ; preserves X
		dex
		bpl .initchannel
		rts

		; Per-channel state. Only the two arrays carrying real initial values live here; the rest is
		; in zero page (see the m_ZP block at the top) and zeroed by music.init. Always "array,x".

		voice_orderpos:    .byte v_ord_start1, v_ord_start2, v_ord_start3
		SID_channel_offset: .byte 0,7,14

		if def musicshower
		show_d418:         .byte 0            ; shadows of the three write-only filter registers
		show_d417:         .byte 0            ; - display only, see Filter_Play
		show_d416:         .byte 0
		endif


		; --- MUSIC PLAYER --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
		org @, "M_Music Player - Play code"
music.play:

		; Wavetables first and on every frame - that is what gives them frame resolution. Counting
		; down saves the cpx; it reverses the write order within a frame, but the writes and the
		; per-frame register state are identical and the voices are independent, so it is inaudible.
		ldx #v_channels-1
.nextvoice:
		jsr Voice_Automation
		dex
		bpl .nextvoice

Filter_Play:
		; Filter, once per frame and before the row fetch, so a program claimed this frame takes its
		; first step next frame. Byte stream: $01..$FE steps the cutoff, $FF introduces a four-byte
		; setup (mode+volume, resonance+routing, sweep rate, cutoff), $00 ends. Falls through, no jsr.
		ldx filter_pos
		beq .filterdone                        ; nothing has claimed the filter yet
		lda flt_data,x
		beq .filterdone                        ; program finished - hold the last cutoff
		cmp #$ff
		bne .filtercut
		inx
		lda flt_data,x
		sta $d418                              ; filter mode + master volume
		if def musicshower
		sta show_d418                          ; $d418/$d417/$d416 cannot be read back, so the
		endif                                  ; shower needs a shadow of each. Gated: a .sid
		inx                                    ; export has no shower and pays nothing.
		lda flt_data,x
		sta $d417                              ; resonance + which voices are routed through
		if def musicshower
		sta show_d417
		endif
		inx
		lda flt_data,x
		sta filter_delta                       ; sweep rate, 0 to stop a running sweep
		inx
		lda flt_data,x
.filtercut:
		sta $d416
		if def musicshower
		sta show_d416
		endif
		inx
		stx filter_pos
.filterdone:

		; Filter sweep, every frame. A free-running triangle: filter_phase advances by a signed rate
		; and wraps, the cutoff folds the top half back down and doubles. That fold is what makes it
		; ping-pong without bounds checks - 20 bytes, at the cost of a fixed $00..$FE range.
		lda filter_delta
		beq .sweepdone                         ; 0 = no sweep running
		clc
		adc filter_phase
		sta filter_phase
		bpl .sweepup
		eor #$ff                               ; past the top: fold back down
.sweepup:
		asl
		sta $d416
		if def musicshower
		sta show_d416
		endif
.sweepdone:

		dec Song_Speed
		bmi ONbeat.PlaySong
		rts                                     ; between ticks: the filter already stepped above
ONbeat.PlaySong:
		move8 #v_Song_Speed, Song_Speed

; Tick all channels. Filter_Play above ran by fallthrough, not a jsr, so the trailing rts
; below is this routine's own.

Player_Play:
		ldx #v_channels-1                      ; descending, same reasoning as music.play's own
.nextchannel:                                  ; channel loop above
		jsr Voice_Tick                         ; preserves X
		dex
		bpl .nextchannel
		rts

; Per-channel tick. X = channel on entry and preserved on every exit. One subroutine indexed
; by X rather than three macro copies, which used to cost 592 of the player's 794 bytes.
; Rows are variable length: voice_event is a byte offset, .nextcmd consumes commands.

Voice_Tick:
		stx ZP_ch

		; Count down first and test the result rather than testing for zero then decrementing: a row
		; with N ticks left spends exactly N of them here. The $ff is never seen - .playrow reloads
		; voice_duration from voice_rowdur first.
		dec voice_duration,x
		bmi .fetch
		rts

.fetch:
		lda voice_patternidx,x
		tax                                    ; X = pattern index, channel parked in ZP_ch
		m_setptr_x tbl_rows_lo tbl_rows_hi
		ldx ZP_ch
.fetchrow:
		ldy voice_event,x
.nextcmd:
		lda (ZP_lb),y
		beq .endofpattern
		bpl .playrow                           ; $01..$7F is a note id - store it as it stands
		cmp #CMD_INST
		bcc .setduration                       ; $80..$BF
		cmp #CMD_OFF
		bcc .setinstrument                     ; $C0..$CF
		cmp #LOOP_END
		bne .playrow                           ; $D0/$D1 - note off and wait are stored as the
		                                       ; command byte itself, which is what NOTE_OFF/ROW_WAIT
		                                       ; are defined to be. Falls through on $D2 into .loopend,
		                                       ; which is why that block has to stay right here.

.loopend:
		; $D2, then the repeat count (0 = bare LE, loops forever), then the byte offset to
		; jump back to (the most recent LS, or pattern start if none). Unchanged from the
		; fixed-length format, where those three sat in one row - the layout is the same.
		iny
		lda (ZP_lb),y
		beq .loopback
		; ISC (illegal $F7 zp,x) is INC then SBC, so this one instruction is the inc + cmp pair it
		; replaces: with carry set, SBC gives A - counter, zero exactly when equal. CARRY MUST BE SET
		; - it is, only because the sole way in is the falling-through cmp #LOOP_END above.
		isc voice_loop_counter,x
		bne .loopback
		sta voice_loop_counter,x               ; A is already 0 - it is the A-counter the isc
		                                       ; just computed, and this path only runs when
		                                       ; that difference was zero
		iny                                    ; step past the target byte to the next row
		iny
		jmp .nextcmd                           ; Y is already the new offset, so there is no
		                                       ; need to store it and have .fetchrow load it straight
		                                       ; back - nothing between here and .playrow reads
		                                       ; voice_event, and both of those write it themselves.
.loopback:
		iny
		lda (ZP_lb),y
		tay                                    ; same reasoning - jump straight in with Y set
		jmp .nextcmd

		; Masking rather than subtracting: the dispatch above already proved which range the
		; byte is in, so the low bits are the value and a single AND isolates them - one byte
		; less than sec/sbc, and it leaves no carry to think about.
.setduration:
		and #$3f                               ; $80..$BF -> 0..63 further ticks, so a row lasts
		                                       ; 1..64 and one authored row line is one tick
		sta voice_rowdur,x
		bpl .advance                           ; always - "and #$3f" cannot set N, and the sta
		                                       ; between does not touch flags at all

.setinstrument:
		and #$0f                               ; $C0..$CF -> 0..15
		sta voice_inst,x
.advance:
		iny
		bne .nextcmd                           ; always - a pattern never reaches offset 0 here

.playrow:
		sta voice_note,x
		iny
		tya
		sta voice_event,x
		lda voice_rowdur,x                     ; the row takes however long is current
		sta voice_duration,x
		jmp Voice_PlayCurrent                  ; tail call - its rts returns to our caller

.endofpattern:
		jsr Voice_AdvanceOrder                 ; reuses ZP_lb/hb, so re-derive the row pointer
		jmp .fetch

; Required Lua-generated tables (Startup\Tables.s): note_freq_low/high, tbl_rows_pat*,
; tbl_rows_lo/hi, tbl_songorder, ad_table, sr_table, wt_start/wt_data, arp_start/arp_data,
; flt_start/flt_data, pw_init, pw_add, plus vib_ds/vib_depth and slide_rate when used.
