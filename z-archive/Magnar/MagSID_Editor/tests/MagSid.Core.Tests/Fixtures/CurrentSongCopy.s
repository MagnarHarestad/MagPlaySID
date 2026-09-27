var v_Song_Speed = 6

INSTRUMENTS:
; nr, waveform, AD, SR
01, $21, $09, $aa
02, $41, $00, $f8
03, $11, $20, $77
END:

PAT01:
"C-5", 01
"C-5", 01
"...", 00
"D-5", 01
"...", 00
"---", 00
END:

PAT02:
"E-5", 01
"...", 00
"F-5", 01
"...", 00
"G-5", 01
"...", 00
"...", 00
"---", 00
END:

PAT03:
"G-3", 03
"...", 00
LS
"A-3", 03
"...", 00
"B-3", 03
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
3
END:
