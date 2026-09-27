macro m_init_code {

// >>>>>>>>>>>>>> Basic-Startup <<<<<<<<<<<<<<<<<<<

        if !def LINKVERSION
                if musicshower 				
                        ; --- Basic Startup 
                        basic_startup addr.musicshower.loc
                else
                        basic_startup !+
                        !:
                        ldx #0
                !:	lda message,x
                        beq msgdone
                        sta $0400,x
                        inx
                        bne !-
                        msgdone:
                        rts
                endif
        endif

        message:
        byte "     magsid song export completed!      "
        byte "use sidedit to create a sid-file of the "
        byte " prg-file that now is in the directory. "
        byte $00


        message_screen_1:
        byte "rastertime max: $00. now: $00     sid:  "
        byte "                                        "
        byte "songpos......:             /            "
        byte "pattern nr...:             /            "
        byte "pattern pos..:             /            "
        byte "note duration:             /            "
        byte $00
        
        

        message_screen_2:
        byte "note.........:                          "
        byte "                                        "
        byte "instrument...:                          "
        byte "attack/decay.:                          "
        byte "sustain/rel..:                          "
        byte "waveform/gate:                          "
        byte "pulse........:                          "
        byte $00

        message_screen_3:
        byte "hardreset....:                          "
        byte "                                        "
        byte "filter/volume:                          "
        byte "filter res+v.:                          "
        byte "filter cutoff:                          "
        byte "filter add...:                          "
        byte $00


// >>>>>>>>>>>>>> Zero-page Setup <<<<<<<<<<<<<<<<<<<
        
        m_ZP.b "ZP.Init_Completed"
        m_ZP.b "ZP.First_D012"
        m_ZP.b "ZP.Second_D012"
        m_ZP.b "ZP.SpentRastertime"
        m_ZP.b "ZP.MaxRastertime"

        m_CreateZPs "up", $10, "Zero Page MusicShower"



// >>>>>>>>>>>>>> INIT Main Code <<<<<<<<<<<<<<<<<<<

        org addr.musicshower.loc, "M_Init_MusicShower"
        start:	
        m_Initiation_IRQ v_d012 false                    // Set a IRQ to take over and run while the init is doing its thing. True = play song, False = Do not play song.

        m_Screen_Colors 0, 0, 0, 0, 3                    // $d020, $d021, $d022, $d023, ColorRam
        
        m_set_dd00_or_dd02 v_vicbank                     // Set VicBank in correct $dd00 or $dd02 based on what Loader-framework is being used.
        m_Set_VicBank

        set_d018 $1000, $0400                           // Change font and Screen
        set_d016 0, 1, 1                                // Change to hires mode

        m_Sprites_Init                                  // Reset all sprites
        m_Sprites_Colors $00,$00,$00                    // Set colors for sprites

        move8 #0, ZP.MaxRastertime


        // Skulle behöva en macro för detta...
        ldx #$00
        lda #$20
!:	sta $0400,x
        sta $0500,x
        sta $0600,x
        sta $06e8,x
        inx
        bne !-
        
        ldx #0
!:	lda message_screen_1,x
        beq !+
        sta $0400 + $28*2,x
        inx
        bne !-
        !:
        

        ldx #0
!:	lda message_screen_2,x
        beq !+
        sta $0400 + $28*9,x
        inx
        bne !-
        !:


        ldx #0
!:	lda message_screen_3,x
        beq !+
        sta $0400 + $28*16,x
        inx
        bne !-
        !:

        m_Sprites_Init                                  // Reset all sprites
        m_Sprites_Colors $00,$00,$00                    // Set colors for sprites


        if initmusic 
                lda #0                                  // Song nr.
                ldx #1                                  // Check SID-type 6581 / 8580. This will also prepare filter-add modifications for old SID chips.
                jsr addr.music.init
        endif


        // Stop the Init-IRQ used to avoid a music-frame skip during init of ScreenRAM & ColorRAM etc. Move on with setting up demoparts IRQs


        move8 #1, ZP.Init_Completed
        sei
        set_d011 3,1,1,0,0,0                            // vscroll:8, rows25:8, screen_on:8, bitmap:8, extended:8 ,raster_hi:8
        move8 #v_d012, $d012
        move16 #IRQ_Main, $fffe
        cli
                
        // Outside IRQ continues here

   

}

