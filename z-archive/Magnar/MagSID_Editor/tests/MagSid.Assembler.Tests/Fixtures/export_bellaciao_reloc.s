; Like export_bellaciao.s but with a caller-chosen origin, for the relocation test. The checked-in
; song drivers hardcode $1000 and only export_sid.s takes -d, and that one reads Patterns.s.
		include "all.s"
		include "math.s"
		include "..\Globalmacros.s"
		lua { v_songfile = "BellaCiao.s" }
		include "Startup\Tables.s"

		var v_musicdebug = false
		var v_channels = 3

		if !def v_export_loc
		const v_export_loc = $1000
		endif

		var addr.music.loc = v_export_loc
		var addr.music.init = addr.music.loc
		var addr.music.play = addr.music.loc+$3

		include "Code\Musicplayer.s"

		m_tables
end
