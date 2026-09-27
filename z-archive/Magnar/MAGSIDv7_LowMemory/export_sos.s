		; ------------------------------------------------------------------------------------
		; MAGSID v7 Low Memory - Standalone SID export driver for SongData\Lightforce.s
		;
		; Assembles ONLY the self-contained $1000 segment (JMP table + Voice_* subroutines +
		; music.init/music.play + the Lua-generated song/pattern tables from Startup\Tables.s)
		; with no BASIC stub, IRQ setup, or musicshower demo code. Used by the MagSID Editor's
		; ".sid" export feature: it overwrites SongData\Patterns.s with the song being
		; exported, assembles this file, then wraps the resulting $1000-based memory image in
		; a PSID header (init=$1000, play=$1003 - matching addr.music.init/addr.music.play
		; below exactly).
		; ------------------------------------------------------------------------------------

			include "all.s"
			include "math.s"
			include "..\Globalmacros.s"
			; Set before Tables.s is included: it decides at include time whether the vibrato code is
			; assembled into the player at all (v_vibrato), by scanning this song file.
			lua { v_songfile = "SOS.s" }
			include "Startup\Tables.s"

			var v_musicdebug = false
			var v_channels = 3

			var addr.music.loc = $1000
			var addr.music.init = addr.music.loc
			var addr.music.play = addr.music.loc+$3

			include "Code\Musicplayer.s"

			m_tables

end
