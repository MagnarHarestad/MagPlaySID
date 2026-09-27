if def _STARTUP_INCLUDED == 0
const _STARTUP_INCLUDED = 1

macro basic_startup{
	@ = $0801
	byte $0b, $08, $0a, $00, $9e, $32, $30, $36, $31, $00, $00, $00
}

macro basic_startup n:16 {
	basic_startup
	jmp n
}

endif //_STARTUP_INCLUDED
