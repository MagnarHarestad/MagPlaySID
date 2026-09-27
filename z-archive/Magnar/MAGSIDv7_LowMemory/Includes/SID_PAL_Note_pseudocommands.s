
sq {
    local notes = [
        "cw", "cb", "dw", "db", "ew",
        "fw", "fb", "gw", "gb", "aw", "ab", "bw"
    ];

    // (cw0–bw7)
    for (local octave = 0; octave <= 7; octave++) {
        foreach (index, name in notes) {
            local macroName = name + octave;
            local value = format("$%02x", index + octave * 12);
            local line = format("macro %s { byte %s }", macroName, value);
            // print(line + "\n");
            asm(line);
        }
        // print("\n");
    }

    // (zcw0–zbw7)
    for (local octave = 0; octave <= 7; octave++) {
        foreach (index, name in notes) {
            local macroName = "z" + name + octave;
            local value = format("$%02x", 0x80 + index + octave * 12);
            local line = format("macro %s { byte %s }", macroName, value);
            // print(line + "\n");
            asm(line);
        }
        // print("\n");
    }
}

macro dur { byte $fe }
macro gat { byte $fd }


/*
macro cw0 { byte $00 }
macro cb0 { byte $01 }
macro dw0 { byte $02 }
macro db0 { byte $03 }
macro ew0 { byte $04 }
macro fw0 { byte $05 }
macro fb0 { byte $06 }
macro gw0 { byte $07 }
macro gb0 { byte $08 }
macro aw0 { byte $09 }
macro ab0 { byte $0a }
macro bw0 { byte $0b }

macro cw1 { byte $0c }
macro cb1 { byte $0d }
macro dw1 { byte $0e }
macro db1 { byte $0f }
macro ew1 { byte $10 }
macro fw1 { byte $11 }
macro fb1 { byte $12 }
macro gw1 { byte $13 }
macro gb1 { byte $14 }
macro aw1 { byte $15 }
macro ab1 { byte $16 }
macro bw1 { byte $17 }

macro cw2 { byte $18 }
macro cb2 { byte $19 }
macro dw2 { byte $1a }
macro db2 { byte $1b }
macro ew2 { byte $1c }
macro fw2 { byte $1d }
macro fb2 { byte $1e }
macro gw2 { byte $1f }
macro gb2 { byte $20 }
macro aw2 { byte $21 }
macro ab2 { byte $22 }
macro bw2 { byte $23 }

macro cw3 { byte $24 }
macro cb3 { byte $25 }
macro dw3 { byte $26 }
macro db3 { byte $27 }
macro ew3 { byte $28 }
macro fw3 { byte $29 }
macro fb3 { byte $2a }
macro gw3 { byte $2b }
macro gb3 { byte $2c }
macro aw3 { byte $2d }
macro ab3 { byte $2e }
macro bw3 { byte $2f }

macro cw4 { byte $30 }
macro cb4 { byte $31 }
macro dw4 { byte $32 }
macro db4 { byte $33 }
macro ew4 { byte $34 }
macro fw4 { byte $35 }
macro fb4 { byte $36 }
macro gw4 { byte $37 }
macro gb4 { byte $38 }
macro aw4 { byte $39 }
macro ab4 { byte $3a }
macro bw4 { byte $3b }

macro cw5 { byte $3c }
macro cb5 { byte $3d }
macro dw5 { byte $3e }
macro db5 { byte $3f }
macro ew5 { byte $40 }
macro fw5 { byte $41 }
macro fb5 { byte $42 }
macro gw5 { byte $43 }
macro gb5 { byte $44 }
macro aw5 { byte $45 }
macro ab5 { byte $46 }
macro bw5 { byte $47 }

macro cw6 { byte $48 }
macro cb6 { byte $49 }
macro dw6 { byte $4a }
macro db6 { byte $4b }
macro ew6 { byte $4c }
macro fw6 { byte $4d }
macro fb6 { byte $4e }
macro gw6 { byte $4f }
macro gb6 { byte $50 }
macro aw6 { byte $51 }
macro ab6 { byte $52 }
macro bw6 { byte $53 }

macro cw7 { byte $54 }
macro cb7 { byte $55 }
macro dw7 { byte $56 }
macro db7 { byte $57 }
macro ew7 { byte $58 }
macro fw7 { byte $59 }
macro fb7 { byte $5a }
macro gw7 { byte $5b }
macro gb7 { byte $5c }
macro aw7 { byte $5d }
macro ab7 { byte $5e }
macro bw7 { byte $5f }

macro zcw0 { byte $80 }
macro zcb0 { byte $81 }
macro zdw0 { byte $82 }
macro zdb0 { byte $83 }
macro zew0 { byte $84 }
macro zfw0 { byte $85 }
macro zfb0 { byte $86 }
macro zgw0 { byte $87 }
macro zgb0 { byte $88 }
macro zaw0 { byte $89 }
macro zab0 { byte $8a }
macro zbw0 { byte $8b }

macro zcw1 { byte $8c }
macro zcb1 { byte $8d }
macro zdw1 { byte $8e }
macro zdb1 { byte $8f }
macro zew1 { byte $90 }
macro zfw1 { byte $91 }
macro zfb1 { byte $92 }
macro zgw1 { byte $93 }
macro zgb1 { byte $94 }
macro zaw1 { byte $95 }
macro zab1 { byte $96 }
macro zbw1 { byte $97 }

macro zcw2 { byte $98 }
macro zcb2 { byte $99 }
macro zdw2 { byte $9a }
macro zdb2 { byte $9b }
macro zew2 { byte $9c }
macro zfw2 { byte $9d }
macro zfb2 { byte $9e }
macro zgw2 { byte $9f }
macro zgb2 { byte $a0 }
macro zaw2 { byte $a1 }
macro zab2 { byte $a2 }
macro zbw2 { byte $a3 }

macro zcw3 { byte $a4 }
macro zcb3 { byte $a5 }
macro zdw3 { byte $a6 }
macro zdb3 { byte $a7 }
macro zew3 { byte $a8 }
macro zfw3 { byte $a9 }
macro zfb3 { byte $aa }
macro zgw3 { byte $ab }
macro zgb3 { byte $ac }
macro zaw3 { byte $ad }
macro zab3 { byte $ae }
macro zbw3 { byte $af }

macro zcw4 { byte $b0 }
macro zcb4 { byte $b1 }
macro zdw4 { byte $b2 }
macro zdb4 { byte $b3 }
macro zew4 { byte $b4 }
macro zfw4 { byte $b5 }
macro zfb4 { byte $b6 }
macro zgw4 { byte $b7 }
macro zgb4 { byte $b8 }
macro zaw4 { byte $b9 }
macro zab4 { byte $ba }
macro zbw4 { byte $bb }

macro zcw5 { byte $bc }
macro zcb5 { byte $bd }
macro zdw5 { byte $be }
macro zdb5 { byte $bf }
macro zew5 { byte $c0 }
macro zfw5 { byte $c1 }
macro zfb5 { byte $c2 }
macro zgw5 { byte $c3 }
macro zgb5 { byte $c4 }
macro zaw5 { byte $c5 }
macro zab5 { byte $c6 }
macro zbw5 { byte $c7 }

macro zcw6 { byte $c8 }
macro zcb6 { byte $c9 }
macro zdw6 { byte $ca }
macro zdb6 { byte $cb }
macro zew6 { byte $cc }
macro zfw6 { byte $cd }
macro zfb6 { byte $ce }
macro zgw6 { byte $cf }
macro zgb6 { byte $d0 }
macro zaw6 { byte $d1 }
macro zab6 { byte $d2 }
macro zbw6 { byte $d3 }

macro zcw7 { byte $d4 }
macro zcb7 { byte $d5 }
macro zdw7 { byte $d6 }
macro zdb7 { byte $d7 }
macro zew7 { byte $d8 }
macro zfw7 { byte $d9 }
macro zfb7 { byte $da }
macro zgw7 { byte $db }
macro zgb7 { byte $dc }
macro zaw7 { byte $dd }
macro zab7 { byte $de }
macro zbw7 { byte $df }
*/