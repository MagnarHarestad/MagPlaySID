if def _UTILS_INCLUDED == 0
const _UTILS_INCLUDED = 1

include "word.s"

/*
	A fairly smart memset macro. It will analyze the size and generate the code that makes sense.
	Small size might be unlooped. It will wreck all registers when using a size above $800. Otherwise X will be untouched.
	Future improvements could be to check the alignment and avoid over block edge indexing.

	This macro got trouble with forward declarations.
*/
macro memset addr:16, data:8, size:16
{
	const .blocks 	= size / $100									//How many whole $100 byte blocks are we talking about
	const .rest 	= size \ $100									//Modulo, how many remaining bytes are we talking about

	if size															//Check if we got any size at all, if not we will not generate any code
	
		lda #data													//Load A with fillbyte
		
		if .blocks >= 1												//If we have more than $100 bytes to fill, this block will handle that part
		
			if .blocks <= 8											//If it's 8 or fewer blocks we do it partly unlooped
				ldy #$00
				.loop1:												//The lua script will generate sta address+block,y up to 8 times
				lua {								
					local r
					for r=0,##.blocks-1,1 do
						asm("sta ",##addr+0x100*r,",y")
					end
				}
				iny
				bne .loop1
			else													//If longer than 8 blocks, do a self modifying code, a bit slower
				ldx #.blocks
				ldy #$00
			.s:	sta addr,y
				iny
				bne .s
				inc .s+2
				dex
				bne .s
				//restore for a future run
				ldx #addr>>8
				stx .s+2
			endif
			
		endif
		
		if .rest													//Handle any reminaing size, less than a block
		
			if (.rest <= 8 && .blocks == 0) || .rest <= 2			//A pure unlooped mode. Will only be used if the total size <= 8 or there are <= 2 bytes reminaing
				lua {
					local r
					for r=0,##.rest-1,1 do
						asm("sta ",##addr+0x100*##.blocks+r)
					end
				}
			elseif .rest <= $80										//If there is $80 or fewer bytes, we can use the negitive flag for looping
				ldy #.rest-1
				.loop2:
				sta addr+$100*.blocks,y
				dey
				bpl .loop2
			else													//If there is more than $80 bytes left we use zero flag and an extra sta
				ldy #.rest-1
				.loop3:
				sta addr+$100*.blocks,y
				dey
				bne .loop3
				sta addr+$100*.blocks
			endif
			
		endif
		
	endif
}

macro memmove src:16, dst:16, size:16, restore:0
{
	const .end = src+size
	
	const .blocks 	= size / $100									//How many whole $100 byte blocks are we talking about
	const .rest 	= size \ $100									//Modulo, how many remaining bytes are we talking about
	
	var .yisff = 0
	
	//Check for stupid arguments
	
	if src == dst || size == 0
		// Do nothing
	elseif dst >= src  && dst < .end		//overlapping and dst > src .. backwards copy
	
		if .rest													//Handle any reminaing size, less than a block
	
			if (.rest <= 4 && .blocks == 0) || .rest <= 2			//A pure unlooped mode. Will only be used if the total size <= 8 or there are <= 2 bytes reminaing
				lua {
					local r
					for r=##.rest-1,0,-1 do
						asm("lda ",##src,"+$100*(.blocks-1)+",r)
						asm("sta ",##dst,"+$100*(.blocks-1)+",r)
					end
				}
			else
				ldy #.rest-1
			.loop3:
				lda src+$100*.blocks,y
				sta dst+$100*.blocks,y
				dey
				if (.rest-1) <= 128
					bpl .loop3
				else
					cpy #$ff
					bne .loop3
				endif
				var .yisff = 1
			endif
			
		endif
	
	
		if .blocks >= 1												//If we have more than $100 bytes to copy, this block will handle that part
		
			if .blocks <= 4											//If it's 4 or fewer blocks we do it partly unlooped
			
				if .yisff == 0
					ldy #$ff
				endif
				.loop1:												//The lua script will generate sta address+block,y up to 8 times
				lua {								
					local r
					for r=##.blocks-1,0,-1 do
						asm("lda ",##src+0x100*r,",y")
						asm("sta ",##dst+0x100*r,",y")
					end
				}
				dey
				cpy #$ff
				bne .loop1
			else													//If longer than 8 blocks, do a self modifying code, a bit slower
				ldx #.blocks
				if .yisff == 0
					ldy #$ff
				endif
		.s1:	lda src+$100*(.blocks-1),y
		.s2:	sta dst+$100*(.blocks-1),y
				dey
				cpy #$ff
				bne .s1
				dec .s1+2
				dec .s2+2
				dex
				bne .s1
				
				if restore
					//restore for a future run
					ldx #(src+$100*(.blocks-1))>>8
					stx .s1+2
					ldx #(dst+$100*(.blocks-1))>>8
					stx .s2+2
				endif
			endif
			
		endif
		
	else	//forward copy
	
		if .blocks >= 1												//If we have more than $100 bytes to copy, this block will handle that part
		
			if .blocks <= 4											//If it's 4 or fewer blocks we do it partly unlooped
				ldy #$00
				.loop1:												//The lua script will generate sta address+block,y up to 8 times
				lua {								
					local r
					for r=0,##.blocks-1,1 do
						asm("lda ",##src+0x100*r,",y")
						asm("sta ",##dst+0x100*r,",y")
					end
				}
				iny
				bne .loop1
			else													//If longer than 8 blocks, do a self modifying code, a bit slower
				ldx #.blocks
				ldy #$00
		.s1:	lda src,y
		.s2:	sta dst,y
				iny
				bne .s1
				inc .s1+2
				inc .s2+2
				dex
				bne .s1
				if restore
					//restore for a future run
					ldx #src>>8
					stx .s1+2
					ldx #dst>>8
					stx .s2+2
				endif
			endif
			
			var .yisff = 1
			
		endif
	
		if .rest													//Handle any reminaing size, less than a block
		
			if (.rest <= 4 && .blocks == 0) || .rest <= 2			//A pure unlooped mode. Will only be used if the total size <= 8 or there are <= 2 bytes reminaing
				lua {
					local r
					for r=0,##.rest-1,1 do
						asm("lda ",##src+0x100*##.blocks+r)
						asm("sta ",##dst+0x100*##.blocks+r)
					end
				}
			else
				ldx #.rest
				if .yisff == 0
					ldy #0
				endif
				.loop3:
				lda src+$100*.blocks,y
				sta dst+$100*.blocks,y
				iny
				dex
				bne .loop3
			endif
			
		endif
	
	endif
	
}

macro memmove src:16, dst:16, size:16
{
	memmove src, dst, size, 1
}

//Stupid and slow but practical memcpy
macro memcopy src:16, dst:16, size:16
{
	const .end = src+size
	
.s:	lda src
.d:	sta dst
	inc16 .s+1
	inc16 .d+1
	cmp16bne #.end, .s+1, .s
}

//Write the filesize of the specified file as a word
macro prgfilesize file:0
{
	lua {
		local filesize, file, prg
		file=io.open(##file,"rb")
		prg = file:read(2)
		filesize=file:seek("end")
		io.close(file)
		--Dont count PRG header
		filesize = filesize - 2
		asm("word (",string.byte(prg,2)," << 8) + ",string.byte(prg,1)," + ",filesize)
	}
}

//Primitive PETSCII macro
macro text str:0
{
	lua
	{
		local txt = string.upper(##str)
		local r
		for r=1,string.len(txt),1 do
			asm("byte ",string.byte(txt,r))
		end
	}
}

//Include PRG at PRG header position
macro incprgorg file:0
{
	lua {
		local file, prg
		file=io.open(##file,"rb")
		prg = file:read(2)
		io.close(file)
		asm("@ = (",string.byte(prg,2)," << 8) + ",string.byte(prg,1))
		asm("incprg " .. ##file)
	}
}

//Include PRG at PRG header position and create a label at PRG ORG
macro incprgorg file:0, label:0
{
	lua {
		local file, prg
		file=io.open(##file,"rb")
		prg = file:read(2)
		io.close(file)
		asm("@ = (",string.byte(prg,2)," << 8) + ",string.byte(prg,1))
		asm(##label)
		asm("incprg " .. ##file)
	}
}

macro break
{
	vice break @
}

macro warphere
{
	vice resset "WarpMode" "1"
	vice command 1 "resset \"WarpMode\" \"0\""
	vice trace exec @
}

macro org name:0 {
	org @, name
}

macro org name:0 virtual {
	org @, name virtual
}

macro org p:16, name:0 {
    org p, p, name
}

macro org p:16, name:0 virtual {
    org p, p, name virtual
}

macro org p:16, pw:16, name:0 {
    lua {
        if not __org_table then
            __org_table = {}
        end
        e = {}
        e.start_addr = ##p
        e.name = ##name
		e.virtual = false
        if __org_table_last then
			if __org_table_last.align_end and __org_table_last.align_end >= ##@ then
            	__org_table_last.end_addr = __org_table_last.align_start
			else
	            __org_table_last.end_addr = ##@
			end
        end
        table.insert(__org_table, e)
        __org_table_last = e
    }
    @ = p, pw
}

macro org p:16, pw:16, name:0 virtual {
    lua {
        if not __org_table then
            __org_table = {}
        end
        e = {}
        e.start_addr = ##p
        e.name = ##name
		e.virtual = true
        if __org_table_last then
			if __org_table_last.align_end and __org_table_last.align_end >= ##@ then
            	__org_table_last.end_addr = __org_table_last.align_start
			else
	            __org_table_last.end_addr = ##@
			end
        end
        table.insert(__org_table, e)
        __org_table_last = e
    }
    @ = p, pw virtual
}

macro end {
    lua {
        if __org_table then
            __org_table_last.end_addr = ##@
            table.sort(__org_table, function(l,r)
                return l.start_addr < r.start_addr
            end)
            print("")
            print(string.format("Memory Map"))
            print(string.format("=========="))

			-- Starting with an empty range
			local start_addr = 0
			local end_addr
			if __org_table[1] then
				end_addr = __org_table[1].start_addr
			else
				end_addr = 0x10000
			end
			local size = end_addr - start_addr
			if size > 0 then
				print(string.format(" $%04x - $%04x ($%04x)", start_addr, end_addr-1, size))
			end

			-- Every occupied range and following empty range
            for i,e in ipairs(__org_table) do
                size = e.end_addr - e.start_addr
                if (size > 0) then
                    last_addr = e.end_addr - 1
                else
                    last_addr = e.start_addr;
                end
				local vch = " "
				if e.virtual then vch = "*" end
                print(string.format("%s$%04x - $%04x ($%04x) %s", vch, e.start_addr, last_addr, size, e.name))

				-- Empty range following
				local start_addr = __org_table[i].end_addr
				local end_addr
				if __org_table[i+1] then
					end_addr = __org_table[i+1].start_addr
				else
					end_addr = 0x10000
				end
				local size = end_addr - start_addr
				if size > 0 then
					print(string.format(" $%04x - $%04x ($%04x)", start_addr, end_addr-1, size))
				end
            end
            print("")
        end
    }
}

macro print (a0:0)
{
    lua print(string.format(##a0))
}

macro print (a0:0, a1:0)
{
    lua print(string.format(##a0, ##a1))
}

macro print (a0:0, a1:0, a2:0)
{
    lua print(string.format(##a0, ##a1, ##a2))
}

macro print (a0:0, a1:0, a2:0, a3:0)
{
    lua print(string.format(##a0, ##a1, ##a2, ##a3))
}

macro print (a0:0, a1:0, a2:0, a3:0, a4:0)
{
    lua print(string.format(##a0, ##a1, ##a2, ##a3, ##a4))
}

macro print (a0:0, a1:0, a2:0, a3:0, a4:0, a5:0)
{
    lua print(string.format(##a0, ##a1, ##a2, ##a3, ##a4, ##a5))
}

macro print (a0:0, a1:0, a2:0, a3:0, a4:0, a5:0, a6:0)
{
    lua print(string.format(##a0, ##a1, ##a2, ##a3, ##a4, ##a5, ##a6))
}

macro print (a0:0, a1:0, a2:0, a3:0, a4:0, a5:0, a6:0, a7:0)
{
    lua print(string.format(##a0, ##a1, ##a2, ##a3, ##a4, ##a5, ##a6, ##a7))
}

macro print (a0:0, a1:0, a2:0, a3:0, a4:0, a5:0, a6:0, a7:0, a8:0)
{
    lua print(string.format(##a0, ##a1, ##a2, ##a3, ##a4, ##a5, ##a6, ##a7, ##a8))
}

macro assert cond:0, msg:0
{
    if !cond
        error msg
    endif
}

endif //_UTILS_INCLUDED
