; Test fixture, not a tune: slide_only.s with funk speed - the shuffle byte after the slide state.
; Slides are pattern rows - 1xx up, 2xx down, 100 stops - and no song in SongData has one,
; so without this the slide half of the player is never compared against c6510.
var v_Song_Speed = 6
var v_Song_Funk = 9

INSTRUMENTS:
01, $41, $00, $f7, WAVE $09 $41 $40, ARP ROOT, PW 6 +0
02, $21, $00, $aa, WAVE $09 $21 $20, ARP ROOT, PW 2 +6 CONT
END:

PAT01:
"C-4", 01
"108", 00
"...", 00
"E-4", 02
"204", 00
"100", 00
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
