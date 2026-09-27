var v_Song_Speed = 4

INSTRUMENTS:
; nr, waveform, AD, SR
01, $21, $09, $aa
02, $41, $00, $f8
END:

PAT01:
"C-5", 01
"...", 00
"D#4", 02
"---", 00
END:

PAT02:
"G-3", 02
LS
"A-3", 01
"...", 00
LE
END:

ORDER_CH1:
1, 2, "restart", 2
END:

ORDER_CH2:
1
END:

ORDER_CH3:
2, 1
END:
