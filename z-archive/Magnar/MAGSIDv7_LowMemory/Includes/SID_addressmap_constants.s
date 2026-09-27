
        ; SID-register
        var SID_BASE      = $D400

        ; Röst 2 ($D407–$D40D) och Röst 3 ($D40E–$D414) följer samma struktur.

        var SID_FREQ_LO   = SID_BASE + 0
        var SID_FREQ_HI   = SID_BASE + 1
        var SID_PW_LO     = SID_BASE + 2
        var SID_PW_HI     = SID_BASE + 3
        var SID_CTRL      = SID_BASE + 4
        var SID_AD        = SID_BASE + 5
        var SID_SR        = SID_BASE + 6

        var SID_FCUT_LO   = SID_BASE + 21 ; $D415 - Filter Cutoff
        var SID_FCUT_HI   = SID_BASE + 22 ; $D416 - Filter Cutoff
        var SID_RES_FILT  = SID_BASE + 23 ; $D417 - Resonance/Filter select
        var SID_MODE_VOL  = SID_BASE + 24 ; $D418 - Volume & filter mode