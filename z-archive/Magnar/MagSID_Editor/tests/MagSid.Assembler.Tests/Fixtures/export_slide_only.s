; Driver for the slide_only.s test fixture beside this file - see PlayerImageTests.
; v_songfile is relative to SongData\, which is how a fixture stays out of the player tree.
		include "all.s"
		include "math.s"
		include "..\Globalmacros.s"
		lua { v_songfile = "../../MagSID_Editor/tests/MagSid.Assembler.Tests/Fixtures/slide_only.s" }
		include "Startup\Tables.s"

		var v_musicdebug = false
		var v_channels = 3

		var addr.music.loc = $1000
		var addr.music.init = addr.music.loc
		var addr.music.play = addr.music.loc+$3

		include "Code\Musicplayer.s"

		m_tables
end
