if def _LONGBRANCH_INCLUDED == 0
const _LONGBRANCH_INCLUDED = 1

// Long branch

macro lbpl n:16 {
	bmi .b
	jmp n
.b:
}

macro lbmi n:16 {
	bpl .b
	jmp n
.b:
}

macro lbvc n:16 {
	bvs .b
	jmp n
.b:
}

macro lbvs n:16 {
	bvc .b
	jmp n
.b:
}

macro lbcc n:16 {
	bcs .b
	jmp n
.b:
}

macro lbcs n:16 {
	bcc .b
	jmp n
.b:
}

macro lbne n:16 {
	beq .b
	jmp n
.b:
}

macro lbeq n:16 {
	bne .b
	jmp n
.b:
}

endif //_LONGBRANCH_INCLUDED
