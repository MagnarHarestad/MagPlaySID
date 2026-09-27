		; ------------------------------------------------------------------------------------
		; MAGSID v7 Low Memory - Standalone SID export driver
		;
		; Assembles ONLY the self-contained player segment (JMP table + Voice_* subroutines +
		; music.init/music.play + the Lua-generated song/pattern tables from Startup\Tables.s)
		; with no BASIC stub, IRQ setup, or musicshower demo code. Used by the MagSID Editor's
		; ".sid" export feature: it overwrites SongData\Patterns.s with the song being
		; exported, assembles this file, then wraps the resulting memory image in a PSID header
		; whose init/play point at addr.music.loc / addr.music.loc+3.
		;
		; The load address is chosen by the caller with -d v_export_loc=$xxxx and defaults to
		; $1000. Nothing here is position-fixed - the player holds no alignment requirements and
		; no baked-in $1xxx addresses, so reassembling at another origin is all relocation takes.
		; ------------------------------------------------------------------------------------

			include "all.s"
			include "math.s"
			include "..\Globalmacros.s"
			include "Startup\Tables.s"

			var v_musicdebug = false
			var v_channels = 3

			; Overridden from the command line by the editor's export dialog.
			if !def v_export_loc
			const v_export_loc = $1000
			endif

			var addr.music.loc = v_export_loc
			var addr.music.init = addr.music.loc
			var addr.music.play = addr.music.loc+$3

			include "Code\Musicplayer.s"

			m_tables

end
