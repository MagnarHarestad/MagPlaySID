if !def _PROTOTHREAD_INCLUDED
const _PROTOTHREAD_INCLUDED = 1

; - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

macro LC_INIT
{
    sq {
        asm("lda #<(", __lc_current + 2, ")")
        asm("sta ", __lc_current + 0)
        asm("lda #>(", __lc_current + 2, ")")
        asm("sta ", __lc_current + 1)
    }
}

macro LC_INIT c:0
{
    lda #<(c+3)
    sta c + 1
    lda #>(c+3)
    sta c + 2
}

macro LC_RESUME
{
    sq __lc_current <- org()+1
    jmp !+
!:
}

macro LC_SET
{
    lda #<!+
    sq asm("sta ", __lc_current + 0)
    lda #>!+
    sq asm("sta ", __lc_current + 1)
!:
}

; Extended version used in my version of PT_YIELD
macro LC_SET addr:0
{
    lda #<addr
    sq asm("sta ", __lc_current + 0)
    lda #>addr
    sq asm("sta ", __lc_current + 1)
}

macro LC_END
{
}


; - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

const PT_WAITING = 0
const PT_EXITED = 1
const PT_ENDED = 2


macro PT_INIT
{
    LC_INIT
}

macro PT_INIT pt:0
{
    LC_INIT pt
}

macro PT_BEGIN
{
    LC_RESUME
}

macro PT_END
{
    LC_END
    PT_INIT
    lda #PT_ENDED
    rts
}

macro PT_WAIT_UNTIL c:0
{
    LC_SET
    jsr c
    bne !+
    lda #PT_WAITING
    rts
!:
}

macro PT_WAIT_WHILE c:0
{
    LC_SET
    jsr c
    beq !+
    lda #PT_WAITING
    rts
!:
}

macro PT_WAIT_THREAD thread:0
{
    ; Note that we here rely on PT_WAITING being 0 (false) and all others !=0 (true). This holds
    ; as long as there is no PT_YIELD (1) as in the original ProtoThread C implemention.
    PT_WAIT_UNTIL thread
}

;
; Initializes child_pt as the PT to be used inside thread and waits until thread
; ends or exits, i.e. returns either PT_EXITED (1) or PT_ENDED (2).
;
macro PT_SPAWN child_pt:0, thread:0
{
    PT_INIT child_pt
    PT_WAIT_THREAD thread
}

macro PT_RESTART
{
    PT_INIT
    lda #PT_WAITING
    rts
}

macro PT_EXIT
{
    PT_INIT
    lda #PT_EXITED
    rts
}

; N flag set if still running afterwards, cleared otherwise. Instead of making the comparison and
; based on that setting the Z state flag we use the N state flag for the same thing, saving a few
; cycles.
macro PT_SCHEDULE f:0
{
    jsr f
    cmp #PT_EXITED
}

macro PT_YIELD
{
    ; Note that this LC_SET points to the end of the macro, meaning that once we have returned and
    ; next time come back here execution will contine after the macro as expected. No need for
    ; setting any flag like in the original C version.
    LC_SET !+
    lda #PT_WAITING
    rts
!:
}


; - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -

;
; Yields execution for the specified number of ticks (0 to 255 frames) and then continues
; execution after the macro. The macro can be used in a loop, it is reset when called
; again.
;
macro PT_YIELD_TICKS #ticks:8
{
    lda #ticks
    sta !+
    LC_SET
    lda #$ff
    dcp !+
    beq !++
    lda #PT_WAITING
    rts
!:  byte 0
!:
}


;
; Calls the specified routine for the number of ticks (0 to 255 frames) and then continues
; execution after the macro. The macro can be used in a loop, it is reset when called.
; The routine will receive the current tick number in the a register.
;
macro PT_CALL_TICKS #ticks:0, routine:0
{
    lda #0
    sta !+ + 1
    LC_SET
!:  lda #0
    cmp #ticks
    beq !+
    inc !- + 1
    jsr routine
    lda #PT_WAITING
    rts
!:
}


macro PT_WAIT_UNTIL_EQ #val:8, variable:0
{
    LC_SET
    lda #val
    cmp variable
    beq !+
    lda #PT_WAITING
    rts
!:
}


macro PT_WAIT_WHILE_EQ #val:8, variable:0
{
    LC_SET
    lda #val
    cmp variable
    bne !+
    lda #PT_WAITING
    rts
!:
}


macro PT_EVENT
{
    byte 0
}

macro PT_INIT_EVENT event:0
{
    PT_RESET_EVENT event
}

macro PT_SET_EVENT event:0
{
    lda #1
    sta event
}

macro PT_RESET_EVENT event:0
{
    lda #0
    sta event
}

macro PT_WAIT_EVENT event:0
{
    PT_WAIT_WHILE_EQ #0, event
    PT_RESET_EVENT event
}


endif