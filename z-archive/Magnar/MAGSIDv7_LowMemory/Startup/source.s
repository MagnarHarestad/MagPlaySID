       include "all.s"
       include "math.s"

        org $10, "Zero Page" virtual

        ZP.Ch1.Dur:      byte 0
        ZP.Ch2.Dur:      byte 0
        ZP.Ch3.Dur:      byte 0

       basic_startup $1000

       org $1000, "code" 

//       lua asm("dec ZP.Ch", ##i, ".Dur")

       
       for(i=1 : i<3+1 : i=i+1)
        {
            lua print(string.format("i = %#d", ##i))
            
            dec ZP.Ch%i.Dur
            bpl ch%i.durationdonothing
            lda tbl_ch%i_pattern,x
            ch%i.durationdonothing:
            
        }
        rts

        tbl_ch1_pattern: byte 0
        tbl_ch2_pattern: byte 0
        tbl_ch3_pattern: byte 0
    
end