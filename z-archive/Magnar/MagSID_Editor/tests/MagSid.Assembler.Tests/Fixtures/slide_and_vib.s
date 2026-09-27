; Test fixture, not a tune: the smallest song that turns on SLIDE and VIB together.
; No song in SongData uses SLIDE, so without this the slide half of the player
; is never compared against c6510. See PlayerImageTests.
var v_Song_Speed = 6

INSTRUMENTS:
01, $41, $00, $f7, WAVE $09 $41 $40, ARP ROOT, PW 6 +0, SLIDE +8, VIB $34 $10
02, $21, $00, $aa, WAVE $09 $21 $20, ARP ROOT, PW 2 +6 CONT, SLIDE -4, VIB $25 $08
END:

PAT01:
"C-4", 01
"...", 00
"E-4", 02
"...", 00
END:

ORDER_CH1:
1
END:

ORDER_CH2:
1
END:

ORDER_CH3:
1
END:
