		; ---  MAGSID v7 - Low Memoryfootprint - Coded by Magnar Harestad with great help from Robert Lundquist (Krikkit) and Fredrik Ternell (Swallow) of Censor Design!

		; ---  Goattracker player rastertime consumation is around $17 and can be a bit lower with less effects on channels etc. This is with SID-buffering enabled.

		; ---  Date: 2026-05-31

	

		; --- Includes and macros ------------------------------------------------------------------------------------------


			include "all.s"
			include "math.s"
			include "..\Globalmacros.s"

			include "Startup\Init_code.s"
			include "Startup\Outside_irq.s"
			include "Startup\Tables.s"
			include "Startup\Load_Stuff.s"
			

			; --- debug and linking --------------------------------------------------------------------------------------------

			var showrastertime = true

			var musicshower = true

			var v_musicdebug = false


			; --- Variables (global) or Constants (local) ----------------------------------------------------------------------

			var initmusic = true
			var playmusic = true

			var v_channels = 3

			var addr.music.loc = $1000
			
			var addr.music.init = addr.music.loc
			var addr.music.play = addr.music.loc+$3

			var addr.musicshower.loc = $2000		
			var ZP.StartShower = $10		

		    var v_d012 = $10
    	    var v_vicbank = $0000


// >>>>>>>>>>>>>> Initialization <<<<<<<<<<<<<<<<<<<<<<

			m_load_stuff
			m_init_code
			m_outside_irq

// >>>>>>>>>>>>>> Execute parts <<<<<<<<<<<<<<<<<<<<<<<

			include "Code\Musicshower.s"
			include "Code\Musicplayer.s"
		
			m_tables

// >>>>>>>>>>>>>> Show Memory-map <<<<<<<<<<<<<<<<<<<<<
end  

