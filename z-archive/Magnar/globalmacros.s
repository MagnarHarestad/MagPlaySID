

// >>>>>>>>>>>>>> Initiation IRQ <<<<<<<<<<<<<<<<<<

macro m_Initiation_IRQ IRQline.main:8 PlayMusic:8 {
		jmp .Start.IRQ
.IRQ.main:
        m_irq_store_regs
			if PlayMusic 
					m_music_play
			endif
		m_irq_restore_regs
.Start.IRQ:
                sei
		move8 #$35, $01
		move8 #$7f, $dc0d       				// Stop CIA timers
		sta $dd0d
		lda $dc0d
		lda $dd0d
			if PlayMusic 
				m_music_init
			endif
		move8 #$0b, $d011
		move8 #$1, $d01a
		move8 #IRQline.main, $d012
		move16 #.IRQ.main, $fffe
		cli
		// Further Init code will then happen here
}

// >>>>>>>>>>>>>> Stable Raster <<<<<<<<<<<<<<<<<<<

// This code is supposed to be initiated from a previous running IRQ

macro m_stableraster_set {
        
	; --- IRQ.Main: Stabilizing raster ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
.IRQ.main:	
        m_irq_store_regs
.irqchange:		
        move16 #.IRQ.1, $fffe
        inc $d012
        lsr $d019			
        tsx
        cli
        nop
        nop
        nop
        nop
        nop
        nop
        nop
        nop
        nop
        nop
        nop
        rti
.IRQ.1:	txs
        ldx #$08        // this waste 44 cycles
 !:     dex
        bne !-
        bit $00       
        lda $d012
        cmp $d012
        beq .perfect
.perfect:	        ; ----- Perfect timing here ----------
}


macro m_irq_store_regs {
        pha
        txa
        pha
        tya
        pha
}

macro m_irq_restore_regs{
        pla
        tay
        pla
        tax
        pla
        lsr $d019
        rti
}

// >>>>>>>>>>>>>> Music Init <<<<<<<<<<<<<<<<<<<

macro m_music_init {

        if !def LINKVERSION
                if initmusic
                        lda #0
                        jsr addr.music.init
                endif
        endif
}

macro m_music_init_at v_musicinit:16 {

        if !def LINKVERSION
                if initmusic
                        lda #0
                        jsr v_musicinit
                endif
        endif
}


// >>>>>>>>>>>>>> Music Play <<<<<<<<<<<<<<<<<<<

macro m_music_play {

		if showrastertime
				move8 #3,$d020
		endif

		if playmusic
				jsr addr.music.play
		endif

		if showrastertime
				move8 #0,$d020
		endif
}

macro m_music_play_at v_musicplay:16 {

		if showrastertime
				move8 #3,$d020
		endif

		if playmusic
				jsr v_musicplay
		endif

		if showrastertime
				move8 #0,$d020
		endif
}

      
macro m_frame_countdown zp_FrameCounter_lb:8 zp_FrameCounter_hb:8 {
	dec16 zp_FrameCounter_lb
	lda zp_FrameCounter_lb
	bne !+
	lda zp_FrameCounter_hb
	bne !+
	move8 #1, outsidewait_state+1
	!:
}

// >>>>>>>>>>>>>> $DD00 or $DD02 <<<<<<<<<<<<<<<<<<<
// Setup use of $DD00 or $DD02 for different loader-framework support

macro m_set_dd00_or_dd02 v_vicbank:16 {
        var USEDD02 = false
        if !def LINKVERSION
                var v_dd00 = 3-(v_vicbank/$4000)
				move8 #v_dd00 , $dd00
        else
                if USEDD02
                        var v_dd02 = $3c+(v_vicbank/$4000)
						move8 #v_dd02 , $dd02          
                else
                        var v_dd00 = 3-(v_vicbank/$4000)
						move8 #v_dd00 , $dd00
                endif
        endif
}

macro m_Set_VicBank {
	if !def LINKVERSION
                move8 #v_dd00 , $dd00
        else
                if USEDD02
                        move8 #v_dd02 , $dd02          
                else
                        move8 #v_dd00 , $dd00
                endif
        endif
}


// >>>>>>>>>>>>>> Wait for Spacebar to be pressed and then RESET computer <<<<<<<<<<<<<<<<<<<

macro m_Press_Space_Reset {
!:	
		lda $dc01
		cmp #$ef
		bne !-
		lda #$37
        sta $01
		jmp $fce2
}

// >>>>>>>>>>>>>> Wait for Spacebar to be pressed <<<<<<<<<<<<<<<<<<<

macro m_spacebar_keycheck {
!:		lda $dc01
				cmp #$ef
				bne !-
}

// >>>>>>>>>>>>>> Wait for Spacebar to be pressed and register it only once (holding it down will not progress multiple times) <<<<<<<<<<<<<<<<<<<

macro m_spacebar_keyonce {
					jmp !+
.buffer:			byte 0
			!:		lda $dc01		// Press SPACEBAR to progress!
					cmp .buffer
					beq !-
					sta .buffer
					cmp #$ef
					bne !-


}

// 

macro m_joy1_check jmpright:16 jmpleft:16 jmpup:16 jmpdown:16 jmpfire:16 joyheld:8 {

				if joyheld == 1
					jmp !+
.buffer:			byte 0
!:					lda $dc01
					cmp .buffer
                	beq .done
					sta .buffer
				endif

                lda $dc01
                and #%00000100		// Check Joy-Left
                bne .joy_right
				jsr jmpleft
                jmp .joy_up
.joy_right:
                lda $dc01
                and #%00001000		// Check Joy-Right
                bne .joy_up
                jsr jmpright
                jmp .joy_up
.joy_up:
                lda $dc01
                and #%00000001		// Check Joy-UP
                bne .joy_down
                jsr jmpup
                jmp .joy_fire
.joy_down:
                lda $dc01
                and #%00000010		// Check Joy-Down
                bne .joy_fire
                jsr jmpdown                                
.joy_fire:
                lda $dc01
                and #%00010000
                bne .done
				jsr jmpfire
.done:
}


macro m_joy2_check jmpright:16 jmpleft:16 jmpup:16 jmpdown:16 jmpfire:16 joyheld:8 {

				if joyheld == 1
					jmp !+
.buffer:			byte 0
!:					lda $dc00
					cmp .buffer
					beq .done
					sta .buffer
				endif

                lda $dc00
                and #%00000100
                bne .joy_right		// Check Joy-Left
				jsr jmpleft
                jmp .joy_up
.joy_right:
                lda $dc00
                and #%00001000		// Check Joy-Right
                bne .joy_up
                jsr jmpright
                jmp .joy_up
.joy_up:
                lda $dc00
                and #%00000001		// Check Joy-Up
                bne .joy_down
                jsr jmpup
                jmp .joy_fire
.joy_down:
                lda $dc00
                and #%00000010		// Check Joy-Down
                bne .joy_fire
                jsr jmpdown                                
.joy_fire:
                lda $dc00
                and #%00010000
                bne .done
				jsr jmpfire
.done:
}


// >>>>>>>>>>>>>> Delay and wait cycles <<<<<<<<<<<<<<<<<<<

macro m_delay cycles:8 {
	
	sq {
		function delay_remain(n) {
			switch (n) {
				case 2:	asm(" nop ");
					break;
				
				case 3:	asm(" bit $ea ");
					break;
				
				case 4: asm(" nop ");
					asm(" nop ");
					break;
			
				case 5: asm(" nop ");
					asm(" bit $ea ");
					break;
			
				case 6:	asm(" cmp ($00,x)");
					break;
				
				case 7:	asm(" nop ");
					asm(" nop ");
					asm(" bit $ea ");
					break;
				
				case 8: asm(" cmp ($00,x)");
					asm(" nop ");
					break;
			
				case 9: asm(" cmp ($00,x)");
					asm(" bit $ea ");
					break;
				
				case 10: asm(" cmp ($00,x)");
					asm(" nop ");
					asm(" nop ");
					break;
					
				case 11: asm(" cmp ($00,x)");
					asm(" nop ");
					asm(" bit $ea ");
					break;
				
				case 12: asm(" cmp ($00,x)");
					asm(" cmp ($00,x)");
					break;
					
				default: break;
			}
		}
		
		function delay_wo_loop(c) {
		
			local cmps = ((c - 6)/ 6) 
			local remain = (c - cmps*6)
			
			if (cmps)
				for(local i=0;i<cmps;i++)
					asm (" cmp ($00,x) ")
			
			delay_remain(remain)
		}
		
		
		function delay_loop(c) {
		
			local loops = (c - 1) / 5
			local remain = c - (loops*5+1)
			
			if (loops) {
				asm (" ldx # ", loops);		// x * 5 +1
				asm (" .loop: dex ");
				asm (" bne .loop ");
				//asm (" ldx #d016.SCROLX & %11110111")
			}
			
			delay_remain(remain)
		}
		
		if (floor((##@+5)/256) == floor((##@+2)/256)) {
			if (##cycles >18) {
				delay_loop(##cycles)
			}
			
			else {
				delay_wo_loop(##cycles)
			}
		}
		
		 else {
		
		delay_wo_loop(##cycles)
		
		}
		
	}
}

// >>>>>>>>>>>>>> Delay and wait cycles <<<<<<<<<<<<<<<<<<<

macro m_wait n:16 {
    
    assert n<=256, "Too many cycles to wait..."
    var .c = n
    if (.c >= 3 && (.c&1))
        bit $ea
        var .c = .c-3
    endif

    if (.c >= 12)
        jsr waitEnd-(.c-12)/2
    else
        for(.i=0 : .i<.c/2 : .i=.i+1)
        {
            nop
        }
    endif
}

// --------------------- SUPPORT CODE --------------------------------------------------------------

macro m_wait_supportcode {
waitSled:
        for (i=0 : i<256/2 : i=i+1)
        {
            nop     ; (2)
        }
waitEnd:
        rts         ; (6)
}


macro m_Screen_Colors col_d020:8, col_d021:8, col_d022:8, col_d023:8, col_colorram:16 {
		move8 #col_d020, $d020
		move8 #col_d021, $d021
        move8 #col_d022, $d022
		move8 #col_d023, $d023
		
		lua 
		{	
			local colorram = ##col_colorram
			if colorram > 16 then
				print(string.format("ColorRAM filled with content from memory: $%X", colorram))
				asm("ldx #0")
				asm("!: lda " .. colorram .. ",x")
				asm("sta $d800,x")
				asm("lda " .. colorram+256 .. ",x")
				asm("sta $d900,x")
				asm("lda " .. colorram+256*2 .. ",x")
				asm("sta $da00,x")
				asm("lda " .. colorram+256*3 .. ",x")
				asm("sta $db00,x")
				asm("inx")
				asm("bne !-")
			else 
				print(string.format("ColorRAM filled with color byte: $%X", colorram)) 
					asm("lda #" .. colorram)
					asm("ldx #0")
					asm("!: sta $d800,x")
					asm("sta $d900,x")
					asm("sta $da00,x")
					asm("sta $dae8,x")
					asm("inx")
					asm("bne !-")
			end
		}


}

macro m_Sprites_Init {
        move8 #0, $d015 			// All sprites off
        sta $d01d					// Reset X-stretch
        sta $d017	    	        // Reset Y-stretch
        sta $d01c                   // Turn off Multicolor on all sprites
        sta $d01b					// Reset Sprite Prio
        ldx #$10                    // We do NOT turn of $d011 for whipe, so we start at $10.
!:      sta $d000,x					// Reset sprite X,Y pos + highbit $d010
        dex
        bpl !-
}

macro m_Sprites_Colors mc1:8, mc2:8, spritecol:8 {
		move8 #mc1, $d025           // Sprite MC #1
        move8 #mc2, $d026           // Sprite MC #2
        lda #spritecol				// Sprite Color
		ldx #7
!:      sta $d027,x
        dex
        bpl !-
}

macro m_rasterbar cycle:8 {
		lda tbl_rasterbars.buffer.d020,y	; 4
		sta $d020 							; 4
		lda tbl_rasterbars.buffer.d021,y	; 4
		sta $d021							; 4
		iny									; 2
		m_delay cycle
}

macro m_rasterbarscollection {
		m_rasterbar 45
		m_rasterbar 45
		m_rasterbar 45
		m_rasterbar 45
		m_rasterbar 45
		m_rasterbar 45
		m_rasterbar 45
		m_rasterbar 3
}

// >>>>>>>>>>>>>>  ZeroPage Byte and Word fill  <>>>>>>>>>>>>>>><<<<<<<<

macro data.b name:0, value:8
        {
                lua print(string.format("%s:	%#x", ##name, ##@))
                lua asm(##name,":")
                .byte value
        }

macro data.w name:0, value:16
        {
                lua print(string.format("%s:	%#x", ##name, ##@))
                lua asm(##name,":")
                .word value
        }

macro m_ZP.b name:0
{
        lua{
                if not __ZPs then
                        __ZPs = {}
                end
        table.insert(__ZPs, ##name)
        }
}

macro m_LoadKoala filename:0, bitmap:16, screenram:16, colorram:16
{
		// m_LoadKoala "GFX/Bitmap/Roquake.kla", addr.picture.bitmap, addr.picture.screenram, addr.picture.colorram

		if loadpicture
		
			lua { 
					local v_filename = ##filename
					print("Loading Koalapic: " .. v_filename) 
					asm("org " .. ##bitmap .. ", \"M_Picture.bitmap:" .. v_filename .. "\"")
					asm("incprg \"" .. v_filename .. "\",0,8000")
					asm("org " .. ##screenram .. ", \"M_Picture.screenram:" .. v_filename .. "\"")
					asm("incprg \"" .. v_filename .. "\",8000,1000")
					asm("org " .. ##colorram .. ", \"M_Picture.colorram:" .. v_filename .. "\"")
					asm("incprg \"" .. v_filename .. "\",9000,1000")
			}

		endif
}

macro m_CreateZPs direction:0, zp:8, name:8 
{
        // Usage: m_CreateZPs "down", $ff
        //        m_CreateZPs "up", $ea

                lua{
                        if ##direction == "down" then
                                ZPstart = 1 + ##zp - #__ZPs 
                        else
                                ZPstart = ##zp 
                        end

						ZPamount = #__ZPs

                        print(string.format("ZPstart = $%X", ZPstart))
						print(string.format("ZPamount = $%X", ZPamount))

                        asm("org " .. ZPstart .. ", \"" .. ##name .. "\" virtual")

                        for i = 1, #__ZPs do
                                local ZPs = __ZPs[i]
                                print(string.format("$%X", ZPstart+i-1) .. " = " .. ZPs)
                                asm(ZPs .. ": .byte 0")
                        end
                        
                        __ZPs = {}
						
                        return ZPStart, ZPamount
                }
}

macro m_Sinus steps:16, center:8, amplitude:8
{
	lua	{
		for i = 0, ##steps - 1 do
    		local angle = (i / ##steps) * (2 * math.pi)  
    		sin_wave = math.floor(##center + ##amplitude * math.sin(angle))
			-- print(string.format("Step %d: %d", i, sin_wave))
			asm("byte " .. sin_wave)
		end
	}
}


macro ConvertBitmapToFont file:0
{
lua {
	local function readC64Bitmap(filename)
		local file = io.open(filename, "rb")  -- Open in binary mode
		if not file then
			error("Failed to open file: " .. filename)
		end

		local buffer = {
			bitmap = {},  -- 8000 bytes (1-bit per pixel)
			colorMem = {},  -- 1000 bytes (color RAM)
			screenMem = {},  -- 1000 bytes (screen RAM)
			background = 0  -- Single byte
		}

		-- Read bitmap data (8000 bytes)
		for i = 1, 8000 do
			buffer.bitmap[i] = string.byte(file:read(1))
		end

		-- Read color memory (1000 bytes)
		for i = 1, 1000 do
			buffer.colorMem[i] = string.byte(file:read(1))
		end

		-- Read screen memory (1000 bytes)
		for i = 1, 1000 do
			buffer.screenMem[i] = string.byte(file:read(1))
		end

		-- Read background color (if present)
		local bgColor = file:read(1)
		if bgColor then
			buffer.background = string.byte(bgColor)
		end

		file:close()
		return buffer
	end

	-- Example usage
	local bitmapData = readC64Bitmap(##file)

	-- Print first few bytes for debugging
	print("Bitmap Data (first 8 bytes):")
	for i = 1, 8 do
		io.write(string.format("%02X ", bitmapData.bitmap[i]))
	end
	print("\nBackground Color: " .. string.format("%02X", bitmapData.background))

	}
}

macro .text text:0
{
	lua {
		local function textToC64Charset(text)
			local charset = "@ABCDEFGHIJKLMNOPQRSTUVWXYZ[@]@@ !\"#$%&'()*+,-./0123456789:;<=>?"  
			local special_map = { ["£"] = 0x24, ["↑"] = 0x5E, ["←"] = 0x5F }  -- Manually map special chars

			local byteValues = {}

			for i = 1, #text do
				local char = text:sub(i, i)

				if char:match("[a-z]") then
					char = char:upper()
				end

				local charbyte = special_map[char] or charset:find(char, 1, true)  -- Find character or use special map

				if charbyte then
					charbyte = charbyte - 1  -- Adjust to start from $00
					table.insert(byteValues, charbyte)

					-- print(string.format("Char: %s -> $%02X", char, charbyte))
					asm(".byte " .. charbyte)

				else
					print(string.format("Error: Character '%s' is not in the charset!", char))
					return nil  -- Stop execution if an invalid character is found
				end
			end

			return byteValues
		end

		local text = ##text

		local byteArray = textToC64Charset(text)
	}
}

macro m_demoflow
{
		; --- Demo flow control -----------------------------------------------------------------------------------------------------------------

demoflow:
			ldx #0
			dec tbl_demoflow+2,x
			bne demoflowreset
			dec tbl_demoflow+3,x
			bpl demoflowreset
			lda demoflowreset+1
			clc
			adc #4
			sta demoflowreset+1
			lda demoflow+1
			clc
			adc #4
			sta demoflow+1
			bcc !+
			inc demoflow+2
			!:
demoflowreset:			
			jmp (tbl_demoflow)
   
}


macro m_createkoalafadetable
{
lua {
		local lowbits = {
			0x00, 0x0D, 0x09, 0x0E, 0x0B, 0x0C, 0x00, 0x0F, 
			0x0B, 0x00, 0x02, 0x06, 0x0B, 0x03, 0x04, 0x0C
		}
		local highbits = {
			0x00, 0xD0, 0x90, 0xE0, 0xB0, 0xC0, 0x00, 0xF0,
			0xB0, 0x00, 0x20, 0x60, 0xB0, 0x30, 0x40, 0xC0
		}

		local result = {}

		local index = 1  
		for hi = 1, #highbits do
			for i = 1, #lowbits do
				result[index] = lowbits[i] | highbits[hi]  
				index = index + 1  
			end
		end

		for i, byte in ipairs(result) do
			-- print(string.format("Byte %d: 0x%02X", i, byte))
			asm("byte " .. byte)
		end
    }
}

		; --- LUA function-script ------------------------------------------------------------------------------------------

		lua {
		function asmi(s,i)
			asm(string.format(s,i))
		end
		}

macro m_loadsid v_filename:0
{
	lua {

		local function read_string(f, length)
			return f:read(length):gsub("%z", "")
		end

		local function read_word_be(f)
			local b1 = f:read(1):byte()
			local b2 = f:read(1):byte()
			return b1 * 256 + b2
		end

		local function read_byte(f)
			return f:read(1):byte()
		end

		local filename = ##v_filename

		local f = assert(io.open(filename, "rb"))

		-- Checking the file ID
		local magic = f:read(4)
		if magic ~= "PSID" and magic ~= "RSID" then
			error("Not a valid SID-file (Missing PSID/RSID header)")
		end

		local header = {
			version = read_word_be(f),
			data_offset = read_word_be(f),
			load_address = read_word_be(f),
			init_address = read_word_be(f),
			play_address = read_word_be(f),
			songs = read_word_be(f),
			start_song = read_word_be(f),
			speed = read_word_be(f), -- bitfield: 0 = vblank, 1 = CIA 1 timer
			name = read_string(f, 32),
			author = read_string(f, 32),
			released = read_string(f, 32)
		}

		-- SIDv2 specifika fält (om version >= 2)
		if header.version >= 2 then
			header.flags = read_word_be(f)
			header.start_page = read_byte(f)
			header.page_length = read_byte(f)
			header.second_sid_address = read_byte(f)
			header.third_sid_address = read_byte(f)
		end

		f:close()

		print ("File:" .. filename)

		print("SID Header Info:")
		print("-------------------------")
		print("Format: " .. magic)
		print("Version: " .. header.version)
		print("Data Offset: " .. header.data_offset)
		print("Load Address: $" .. string.format("%04X", header.load_address))
		print("Init Address: $" .. string.format("%04X", header.init_address))
		print("Play Address: $" .. string.format("%04X", header.play_address))
		print("Number of Songs: " .. header.songs)
		print("Start Song: " .. header.start_song)
		print("Clock Speed (Speed Bitfield): " .. string.format("0x%04X", header.speed))
		print("Name: " .. header.name)
		print("Author: " .. header.author)
		print("Released: " .. header.released)

		if header.version >= 2 then
			print("Flags: 0x" .. string.format("%04X", header.flags))
			print("Start Page: $" .. string.format("%02X", header.start_page))
			print("Page Length: " .. header.page_length)
			print("Second SID Address: $" .. string.format("%02X", header.second_sid_address))
			print("Third SID Address: $" .. string.format("%02X", header.third_sid_address))
		end

		if header.load_address ~= 0x0000 then
   			asm("var addr.music.loc = " .. header.load_address)
		else
    		asm("var addr.music.loc = " .. header.init_address)
		end
		
		asm("var addr.music.init = " .. header.init_address)
		asm("var addr.music.play = " .. header.play_address)
		asm("var addr.music.volume = " .. header.init_address + 6)

		asm("org addr.music.loc, \"M_SID-music\"")
        -- asm("incprg \"music/Bday Song.sid\", $7c")
		asm("incprg \"" .. filename .. "\", $7c")

	}
}

macro m_Clear_Screen screen:16, clearbyte:8
{
        lda #clearbyte
		ldx #$00
!:		sta screen,x
        sta screen+$100,x
        sta screen+$200,x
        sta screen+$2e8,x
        inx
        bne !-
}

macro m_detect_ultimate zp:8
{
			; detect_ultimate_extended
			; Retur i A:
			;   #$01 = Definitivt Ultimate / UCI identifiering match (#$C9)
			;   #$02 = Något är mappat i $DFxx (möjlig Ultimate / cartridge), men ID-match misslyckades
			;   #$00 = Ingen UCI upptäckt (troligen riktig C64)
			;
			; Obs: Kör i normal C64-kod; funktionen sparar/restorerar A/X/Y/STATUS. Blockerar IRQ under test.
			; Källor: Ultimate / Ultimate-II dokumentation ($DF1C-$DF1F, ID @ $DF1D = $C9). :contentReference[oaicite:1]{index=1}

detect_ultimate_extended:
			pha             ; spara A
			txa
			pha             ; spara X
			tya
			pha             ; spara Y
			php             ; spara status

			sei             ; blockera IRQ under test

			; --- 1) Primärt test: läs identifikationsregistret $DF1D ---
			lda $df1d
			cmp #$C9
			beq .ret_definite    ; om match -> definitiv Ultimate

			; --- 2) Sekundärt test: kolla om något alls svarar i $DFxx ---
			; Läs statusregister ($DF1C) som normalt är $00 på UCI, men på en vanlig C64 utan cartridge
			; är ofta bussvärdet $FF (floating / open bus). Detta är heuristik — inte 100% garanti.
			lda $df1c
			cmp #$FF
			bne .ret_possible    ; något svarar i $DFxx -> möjlig UCI / cartridge

			; --- 3) Tertiärt test (fallback): läs $DF00 som tidigare enkel heuristik ---
			; (många U64 / UCI firmware använder flera DFxx-områden; läs $DF00 som extra hint)
			lda $df00
			cmp #$FF
			bne .ret_possible    ; något svarar -> möjlig mappning

			; Ingen enhet svarade i DF-områden — troligen riktig C64
			lda #$00
			sta zp
			jmp .restore_and_rts

		.ret_definite:
			lda #$01
			sta zp
			jmp .restore_and_rts

		.ret_possible:
			lda #$02
			sta zp

		.restore_and_rts:
			cli             ; återställ IRQ-hantering (vi plockar tillbaka status från stakken strax)
			plp             ; återställ status (inkl. I-flaggan)
			pla             ; återställ Y
			tay
			pla             ; återställ X
			tax
			pla             ; återställ A
			
}

macro m_hexnumber addr:0, i:8, screen:16
{
			jmp !+
			.numbers:
			byte $30,$31,$32,$33,$34,$35,$36,$37,$38,$39,1,2,3,4,5,6
			!:
	  	    lda addr + i
			tay
			lsr
			lsr
			lsr
			lsr
			tax
			lda .numbers,x
			sta screen
			tya
			and #$0f
			tax
			lda .numbers,x
			sta screen + 1
}