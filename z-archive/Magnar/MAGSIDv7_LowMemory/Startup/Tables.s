macro m_tables {
lua {

------------------------------------------------------------
-- Compact first-occurrence Tables.s
-- Notes are case-insensitive:
--   C-1 = c-1 = C-1
-- Only notes actually used in the song are stored in
-- note_freq_low / note_freq_high, in first-occurrence order.
--
-- Patterns are a single global pool (PATnn), shared by all
-- channels. Each channel has its own ORDER_CHn song-order list
-- referencing pattern numbers from that shared pool - the same
-- pattern number played by two channels is the exact same data,
-- stored once.
--
-- A pattern is emitted as one variable-length byte stream, so the
-- player needs a single pointer per pattern and addresses into it
-- with a one-byte offset (255 bytes per pattern).
--
-- Instrument and duration are *state*: a note is one byte and
-- plays with whatever is current, so only a change costs a
-- command. A run of same-instrument, same-length notes therefore
-- costs a byte apiece rather than three, which is where roughly
-- half the old pattern data went.
--
--   $00      = end of pattern (channel advances its song-order)
--   $01..$7F = note dictionary ID (1-based)
--   $80..$BF = set duration: the row lasts 1..64 ticks, so
--              one authored row line is exactly one tick
--   $C0..$CF = set instrument 1..16
--   $D0      = NOTE_OFF ("---"), taking the current duration
--   $D1      = wait, taking the current duration: a "..." that no
--              preceding note could absorb, which happens when it
--              sits on the far side of an LS/LE marker from its
--              note - the editor draws it as a row of its own, so
--              it has to cost time rather than vanish.
--   $D2      = in-pattern loop-back ("LE"/"LE04"), followed by the
--              repeat count (0 = bare "LE", loops forever; N =
--              finite, the section plays N times total) and the
--              target offset (the most recent "LS", or 0).
--   $D3..$FF = unused, reserved for wavetable/arpeggio commands
--
-- Reserved bytes in the per-channel song-order stream:
--   $00 = end of song-order, immediately followed by a 0-based
--         restart-position byte saying which list position to
--         loop back to. The order-list always loops - there is
--         no "channel stops" case anymore.
--   $01..$FF = pattern number (1-based, shared pool). It stays
--         1-based because 0 is the end marker; tbl_rows_lo/hi
--         gets a dummy entry at index 0 so the player can use
--         the number as a table index without adjusting it.
------------------------------------------------------------

------------------------------------------------------------
-- Small helpers
------------------------------------------------------------

local function Trim(s)
    return (s:gsub("^%s+", ""):gsub("%s+$", ""))
end

local function NormalizeNote(note)
    note = note or ""
    note = note:gsub("%s+", "")
    return note:upper()
end

-- "LE" (bare, loops forever) or "LE04" etc (finite repeat count, N times total).
local function IsLE(tu)
    return tu == "LE" or tu:match("^LE%d+$") ~= nil
end

-- 0 = bare/infinite, N = the parsed finite repeat count.
local function LERepeatCount(tu)
    if tu == "LE" then
        return 0
    end
    return tonumber(tu:match("^LE(%d+)$"), 10)
end

local function EmitByteTable(label, data)
    asm(label)

    if #data == 0 then
        asm(".byte 0")
        asm("")
        return
    end

    local s = ".byte "
    for i = 1, #data do
        if i > 1 then
            s = s .. ","
        end
        s = s .. tostring(data[i])
    end

    asm(s)
    asm("")
end

local function ParseSingleNoteLine(line)
    line = line:gsub(" ", "")
    if line == "" then
        return nil
    end

    -- Expected format:
    -- "C-5",01
    local n, i = line:match('^"([^"]+)",([^,]+)$')

    if not n then
        return nil
    end

    return { note = n, inst = tonumber(i, 10) }
end

local function ParseInstrumentLine(line)
    -- Expected format:
    -- 01, $21, $09, $aa
    -- 01, $21, $09, $aa, ARP "C-1" "D#1" "G-1"
    local nr, wf, ad, sr, rest =
        line:match('^(%d+)%s*,%s*%$(%x+)%s*,%s*%$(%x+)%s*,%s*%$(%x+)%s*(.*)$')

    if not nr then
        return nil
    end

    -- Optional comma-separated sections after SR (no section body contains a comma):
    -- WAVE $81 $41 control bytes/frame | ARP "C-1" .. [LOOP|ROOT|HOLD] absolute notes/frame |
    -- FLT ($mv $rr $cc [+n]) $xx | PW w +d [CONT] | VIB $ds $depth | SLIDE +n | NOHR
    local arp, flt, pw, wave, nohr, vib, slide = nil, nil, nil, nil, false, nil, nil

    for seg in Trim(rest):gmatch("[^,]+") do
        seg = Trim(seg)

        if seg:upper() == "NOHR" then
            assert(not nohr, "INSTRUMENTS: duplicate NOHR: " .. line)
            nohr = true

        else
        local kw, body = seg:match("^(%a+)%s+(.+)$")
        kw = kw and kw:upper()

        if kw == "WAVE" then
            assert(not wave, "INSTRUMENTS: duplicate WAVE: " .. line)
            wave = {}
            for v in body:gmatch("%$(%x+)") do
                wave[#wave + 1] = tonumber(v, 16)
            end
            assert(#wave > 0, "INSTRUMENTS: WAVE needs at least one step: " .. line)
            for i, b in ipairs(wave) do
                assert(b >= 0x01 and b <= 0xff,
                    "INSTRUMENTS: WAVE step " .. i .. " must be $01..$ff ($00 ends a program): " .. line)
            end


        elseif kw == "ARP" then
            assert(not arp, "INSTRUMENTS: duplicate ARP: " .. line)
            arp = {}
            -- Tokenised in order, because ROOT and HOLD are positional: they say how the
            -- program ends, and a note after either of them would never be reached.
            for token in body:gmatch("%S+") do
                local quoted = token:match('^"([^"]+)"$')
                local upper = token:upper()
                if quoted then
                    arp[#arp + 1] = { note = quoted }
                elseif upper == "ROOT" then
                    arp[#arp + 1] = { root = true }
                elseif upper == "HOLD" then
                    arp[#arp + 1] = { hold = true }
                elseif upper == "LOOP" then
                    arp[#arp + 1] = { loop = true }
                else
                    assert(false, "INSTRUMENTS: cannot parse ARP token '" .. token .. "': " .. line)
                end
            end
            assert(#arp > 0, "INSTRUMENTS: ARP needs at least one step: " .. line)
            local loops = 0
            for i = 1, #arp do
                if arp[i].loop then
                    loops = loops + 1
                    assert(i < #arp, "INSTRUMENTS: ARP's LOOP marks where to go back to, so at " ..
                        "least one note must follow it: " .. line)
                elseif i < #arp then
                    assert(not arp[i].root and not arp[i].hold,
                        "INSTRUMENTS: ARP's ROOT/HOLD ends the program, so nothing may follow it: " .. line)
                end
            end
            assert(loops <= 1, "INSTRUMENTS: ARP can only have one LOOP marker: " .. line)

        elseif kw == "VIB" then
            assert(not vib, "INSTRUMENTS: duplicate VIB: " .. line)
            local ds, depth = body:match("^%$(%x+)%s+%$(%x+)$")
            assert(ds, "INSTRUMENTS: VIB takes a delay/speed byte and a depth byte, e.g. VIB $32 $10: " .. line)
            ds, depth = tonumber(ds, 16), tonumber(depth, 16)
            assert(ds <= 0xff and depth >= 0x01 and depth <= 0xff,
                "INSTRUMENTS: VIB delay/speed must be $00..$ff and depth $01..$ff: " .. line)
            vib = { ds = ds, depth = depth }

        elseif kw == "SLIDE" then
            assert(not slide, "INSTRUMENTS: duplicate SLIDE: " .. line)
            local rate = body:match("^([-+]?%d+)$")
            assert(rate, "INSTRUMENTS: SLIDE takes a signed rate, e.g. SLIDE +8: " .. line)
            slide = tonumber(rate, 10)
            assert(slide >= -128 and slide <= 127 and slide ~= 0,
                "INSTRUMENTS: SLIDE rate must be -128..127 and not 0: " .. line)

        elseif kw == "FLT" then
            assert(not flt, "INSTRUMENTS: duplicate FLT: " .. line)
            flt = {}
            local i = 1
            while i <= #body do
                if body:sub(i, i):match("%s") then
                    i = i + 1
                elseif body:sub(i, i) == "(" then
                    local inner, nxt = body:match("^%(([^)]*)%)()", i)
                    assert(inner, "INSTRUMENTS: unclosed FLT group: " .. line)
                    local vals, rate = {}, 0
                    for tok in inner:gmatch("%S+") do
                        local hex = tok:match("^%$(%x+)$")
                        local signed = tok:match("^([-+]%d+)$")
                        if hex then
                            vals[#vals + 1] = tonumber(hex, 16)
                        elseif signed then
                            rate = tonumber(signed, 10)
                            assert(rate >= -128 and rate <= 127,
                                "INSTRUMENTS: FLT sweep rate must fit a signed byte: " .. line)
                        else
                            assert(false, "INSTRUMENTS: cannot parse FLT group token '" .. tok .. "': " .. line)
                        end
                    end
                    assert(#vals == 3,
                        "INSTRUMENTS: a FLT group is (mode+volume, resonance+routing, cutoff) " ..
                        "with an optional signed sweep rate: " .. line)
                    flt[#flt + 1] = { setup = vals, rate = rate }
                    i = nxt
                else
                    local v, nxt = body:match("^%$(%x+)()", i)
                    assert(v, "INSTRUMENTS: cannot parse FLT at: " .. body:sub(i))
                    flt[#flt + 1] = { step = tonumber(v, 16) }
                    i = nxt
                end
            end
            assert(#flt > 0, "INSTRUMENTS: FLT needs at least one step: " .. line)

        elseif kw == "PW" then
            assert(not pw, "INSTRUMENTS: duplicate PW: " .. line)
            local width, add, tail = body:match("^(%d+)%s+([-+]?%d+)%s*(.*)$")
            assert(width, "INSTRUMENTS: PW takes a width 0..15 and a signed delta, e.g. PW 8 +2: " .. line)
            width, add = tonumber(width, 10), tonumber(add, 10)
            local continuous = false
            for opt in Trim(tail):gmatch("%S+") do
                opt = opt:upper()
                if opt == "CONT" then
                    continuous = true
                else
                    assert(false, "INSTRUMENTS: unknown PW option '" .. opt .. "': " .. line)
                end
            end
            assert(width >= 0 and width <= 15,
                "INSTRUMENTS: PW width is in 1/16ths, so 0..15: " .. line)
            assert(add >= -128 and add <= 127,
                "INSTRUMENTS: PW delta must fit a signed byte, -128..127: " .. line)
            pw = { width = width, add = add, continuous = continuous }

        else
            assert(false, "INSTRUMENTS: unknown section '" .. tostring(seg) .. "' in: " .. line)
        end
        end
    end

    return tonumber(nr, 10), tonumber(wf, 16), tonumber(ad, 16), tonumber(sr, 16),
           arp, flt, pw, wave, nohr, vib, slide
end

------------------------------------------------------------
-- Read Patterns.s
------------------------------------------------------------

-- Which song to build. Defaults to Patterns.s; a driver can pick another by setting the Lua
-- global v_songfile before m_tables, which is how SongData\Lightforce.s is built without
-- having to swap files around.
local songfile = "SongData/" .. (v_songfile or "Patterns.s")

local f = io.open(songfile, "r")
assert(f, "Cannot open " .. songfile)

local txt = f:read("*all")
f:close()

------------------------------------------------------------
-- Song speed
------------------------------------------------------------

local songspeed = txt:match("var%s+v_Song_Speed%s*=%s*(%d+)")

if songspeed then
    asm("var v_Song_Speed = " .. songspeed)
end

asm("")

-- Load SID PAL note table from external file
-- Format: note_freq_hb: byte $01,... / note_freq_lb: byte $12,...

local function LoadSIDPALTables(path)
    local sf = io.open(path, "r")
    assert(sf, "Cannot open " .. path)

    local sidtxt = sf:read("*all")
    sf:close()

    local hb = {}
    local lb = {}

    local current = nil

    for line in sidtxt:gmatch("[^\r\n]+") do
        local t = Trim(line)
        local tl = t:lower()

        if tl:find("note_freq_hb:", 1, true) then
            current = hb
        elseif tl:find("note_freq_lb:", 1, true) then
            current = lb
        elseif current then
            local i = 1
            while i <= #t - 2 do
                local c = t:sub(i, i)
                if c == "$" then
                    local hx = t:sub(i + 1, i + 2)
                    local v = tonumber(hx, 16)
                    if v ~= nil then
                        current[#current + 1] = v
                        i = i + 3
                    else
                        i = i + 1
                    end
                else
                    i = i + 1
                end
            end
        end
    end

    assert(#hb == 96, "note_freq_hb must contain 96 bytes, got " .. #hb)
    assert(#lb == 96, "note_freq_lb must contain 96 bytes, got " .. #lb)

    return lb, hb
end

local sid_freq_low, sid_freq_high = LoadSIDPALTables("Includes/SID_PAL_Note_table.s")

-- Fixed note index helper for the imported SID table
-- C-0..B-7 = 96 notes

local pitch_order =
{
    ["C-"] = 0,  ["C#"] = 1,  ["D-"] = 2,  ["D#"] = 3,
    ["E-"] = 4,  ["F-"] = 5,  ["F#"] = 6,  ["G-"] = 7,
    ["G#"] = 8,  ["A-"] = 9,  ["A#"] = 10, ["B-"] = 11
}

local function NoteIndex(note)
    note = NormalizeNote(note)

    if note == "..." or note == "---" then
        return nil
    end

    local pitch = note:sub(1, 2)
    local octave = tonumber(note:sub(3, 3), 10)

    assert(octave ~= nil, "Invalid note format: " .. tostring(note))

    local p = pitch_order[pitch]
    assert(p ~= nil, "Invalid pitch in note: " .. tostring(note))

    local idx = octave * 12 + p + 1
    assert(idx >= 1 and idx <= 96, "Note out of range C-0..B-7: " .. tostring(note))
    return idx
end

-- Dynamic note dictionary
-- First occurrence order across the whole song

local note_to_id = {}
local id_to_note = {}
local next_note_id = 1

local function GetNoteID(note)
    note = NormalizeNote(note)

    local id = note_to_id[note]
    if id then
        return id
    end

    id = next_note_id
    note_to_id[note] = id
    id_to_note[id] = note
    next_note_id = next_note_id + 1

    return id
end

-- Parse Patterns.s: INSTRUMENTS / PATnn / ORDER_CHn blocks
-- Patterns are global (not scoped to a channel).

local instruments = {}
local patterns = {}
local pattern_order = {}
local order_raw = { {}, {}, {} }
local order_seen = { false, false, false }

local mode = nil
local current_pattern_id = nil
local current_order_ch = nil

for line in txt:gmatch("[^\r\n]+") do
    -- A ';' starts a comment to end of line anywhere; no token in this grammar contains one,
    -- so cutting here is safe and keeps this reader and the editor's C# one accepting exactly
    -- the same files.
    local t = Trim((line:gsub(";.*$", "")))

    if t == "" then
        -- blank line (or one that was nothing but a comment), skip
    else
        local tu = t:upper()
        local patnum = tu:match("^PAT(%d+):$")
        local ordch = tu:match("^ORDER_CH([1-3]):$")

        if tu == "INSTRUMENTS:" then
            mode = "instruments"
        elseif patnum then
            local id = tonumber(patnum, 10)
            assert(patterns[id] == nil, "Duplicate PAT" .. patnum)
            patterns[id] = {}
            pattern_order[#pattern_order + 1] = id
            current_pattern_id = id
            mode = "pattern"
        elseif ordch then
            local ch = tonumber(ordch, 10)
            assert(not order_seen[ch], "Duplicate ORDER_CH" .. ordch)
            order_seen[ch] = true
            current_order_ch = ch
            mode = "order"
        elseif tu == "END:" then
            mode = nil
            current_pattern_id = nil
            current_order_ch = nil
        elseif mode == "instruments" then
            local nr, wf, ad, sr, arp, flt, pw, wavetable, nohr, vib, slide = ParseInstrumentLine(t)
            assert(nr, "INSTRUMENTS: cannot parse line: " .. t)
            instruments[nr] =
                { wave = wf, ad = ad, sr = sr, arp = arp, flt = flt, pw = pw, wavetable = wavetable,
                  nohr = nohr, vib = vib, slide = slide }
        elseif mode == "pattern" then
            patterns[current_pattern_id][#patterns[current_pattern_id] + 1] = t
        elseif mode == "order" then
            order_raw[current_order_ch][#order_raw[current_order_ch] + 1] = t
        end
    end
end

assert(#pattern_order > 0, "No patterns found (expected PAT01: ...)")
assert(order_seen[1] and order_seen[2] and order_seen[3], "Missing ORDER_CH1/ORDER_CH2/ORDER_CH3 block")

table.sort(pattern_order)
local pattern_count = pattern_order[#pattern_order]
for id = 1, pattern_count do
    assert(patterns[id] ~= nil,
        "Pattern numbers must be contiguous starting at 1 - missing PAT" .. string.format("%02d", id))
end

local instrument_count = 0
for nr, _ in pairs(instruments) do
    if nr > instrument_count then instrument_count = nr end
end
for id = 1, instrument_count do
    assert(instruments[id] ~= nil,
        "Instrument numbers must be contiguous starting at 1 - missing instrument " .. string.format("%02d", id))
end
-- The stream selects an instrument with $C0..$CF, so 16 is all the player can ever reach.
-- Extra ones assemble happily and never play, but still take a slot in every per-instrument
-- table and drag their programs into the pools - a silent cost paid on the C64.
assert(instrument_count <= 16,
    "INSTRUMENTS: " .. instrument_count .. " instruments defined, but a pattern can only " ..
    "select 1..16 ($C0..$CF) - the rest can never be played and would only cost bytes")

-- First pass: build the note dictionary in first-occurrence order.
-- Scan patterns in file order (global pool, not per-channel).

for _, id in ipairs(pattern_order) do
    for _, raw in ipairs(patterns[id]) do
        local tu = Trim(raw):upper()
        if tu ~= "LS" and not IsLE(tu) then
            local row = ParseSingleNoteLine(raw)
            if row then
                local note = NormalizeNote(row.note)
                if note ~= "..." and note ~= "---" then
                    GetNoteID(note)
                end
            end
        end
    end
end

-- Arpeggio steps come out of the same dictionary as the patterns, which is the whole reason
-- they are one byte each. Registered after the patterns so pattern encoding is unaffected by
-- whether an instrument happens to arpeggiate.
for id = 1, instrument_count do
    local arp = instruments[id].arp
    if arp then
        for _, step in ipairs(arp) do
            if step.note then
                local note = NormalizeNote(step.note)
                assert(note ~= "..." and note ~= "---",
                    "Instrument " .. string.format("%02d", id) .. ": ARP steps must be real notes")
                step.note = note
                GetNoteID(note)
            end
        end
    end
end

assert(next_note_id - 1 <= 127,
    "Too many unique notes across the song (max 127 - the pattern stream keeps $80..$FF for commands)")

-- Second pass: emit one variable-length byte stream per pattern, shared by all channels -
-- only a *change* of instrument or duration costs a command byte (encoding map at the top
-- of Code\musicplayer.s). LS marks the loop anchor, LE emits $D2 + count + offset, $00 ends.

local CMD_DUR, CMD_INST, CMD_OFF, CMD_WAIT, CMD_LOOP_END = 0x80, 0xC0, 0xD0, 0xD1, 0xD2
local MAX_DURATION, MAX_INSTRUMENT = 64, 16

local ptr_rows = {}

local function EmitRowTable(label, stream)
    asm(label)
    -- Wrapped rather than one huge .byte run, purely so the listing stays readable.
    for i = 1, #stream, 12 do
        local chunk = {}
        for j = i, math.min(i + 11, #stream) do
            chunk[#chunk + 1] = stream[j]
        end
        asm(".byte " .. table.concat(chunk, ","))
    end
    asm(".byte 0")                  -- end of pattern
    asm("")
end

for _, id in ipairs(pattern_order) do
    local NN = string.format("%02d", id)
    local stream = {}
    local rawlines = patterns[id]

    -- Both start unset so every pattern re-establishes what it uses. Patterns are shared
    -- between channels, so carrying state in from whatever played before would make the same
    -- pattern sound different depending on its context - and different on CH1 than on CH3.
    local cur_inst, cur_dur = nil, nil

    -- The loop anchor, plus the state as it stood when the anchor was passed. LE compares
    -- against these: if the body changed either, the loop has to re-establish it on the way
    -- round or the second pass plays with the first pass's leftovers.
    local ls_target, ls_inst, ls_dur = 0, nil, nil

    local function Emit(b)
        stream[#stream + 1] = b
    end

    local function SetState(inst, dur)
        if inst ~= nil and inst ~= cur_inst then
            assert(inst >= 1 and inst <= MAX_INSTRUMENT,
                "PAT" .. NN .. ": instrument " .. inst .. " is out of range (1.." .. MAX_INSTRUMENT .. ")")
            Emit(CMD_INST + inst - 1)
            cur_inst = inst
        end
        if dur ~= nil and dur ~= cur_dur then
            assert(dur >= 1 and dur <= MAX_DURATION,
                "PAT" .. NN .. ": a row lasts " .. dur .. " ticks; the encoding tops out at " .. MAX_DURATION)
            Emit(CMD_DUR + dur - 1)
            cur_dur = dur
        end
    end

    -- The last line index already swallowed as duration by some earlier note's lookahead.
    -- A "..." past this point belongs to nobody - see the ROW_WAIT branch below.
    local consumed_until = 0

    local idx = 1
    while idx <= #rawlines do
        local raw = rawlines[idx]
        local tu = Trim(raw):upper()

        if tu == "LS" then
            ls_target = #stream                -- byte offset of the next thing emitted
            ls_inst, ls_dur = cur_inst, cur_dur
        elseif IsLE(tu) then
            -- The byte after $D2 is the repeat count: 0 = bare LE, loops forever; N = finite,
            -- the marked section plays N times total before falling through.
            local repeat_count = LERepeatCount(tu)
            assert(repeat_count >= 0 and repeat_count <= 255,
                "PAT" .. NN .. ": LE repeat count must be 0 (bare LE, loops forever) or 1..255: " .. raw)

            -- If the body changed instrument or duration, the jump back would re-enter with the loop's
            -- end state rather than its start. Splice the needed commands in at the anchor so every pass
            -- re-establishes them; nothing is inserted when the body left the state alone.
            local fixup = {}
            if ls_inst ~= nil and cur_inst ~= ls_inst then
                fixup[#fixup + 1] = CMD_INST + ls_inst - 1
            end
            if ls_dur ~= nil and cur_dur ~= ls_dur then
                fixup[#fixup + 1] = CMD_DUR + ls_dur - 1
            end
            for i, b in ipairs(fixup) do
                table.insert(stream, ls_target + i, b)
            end
            -- cur_inst/cur_dur deliberately keep the body's *end* state: the fixup runs at the top of
            -- the loop, so it only affects what each pass starts with. Resetting here would make the
            -- next row think it already had the anchor's instrument.

            Emit(CMD_LOOP_END)
            Emit(repeat_count)
            Emit(ls_target)
        else
            local row = ParseSingleNoteLine(raw)
            assert(row, "PAT" .. NN .. ": cannot parse line: " .. raw)
            local note = NormalizeNote(row.note)

            -- How many "..." lines follow this one, stopping at LS/LE (a note must not
            -- swallow holds from the far side of a loop marker - those only play on the
            -- pass that actually reaches them).
            local function CountHolds(from)
                local n = 0
                local look = from
                while look <= #rawlines do
                    local lu = Trim(rawlines[look]):upper()
                    if lu == "LS" or IsLE(lu) then
                        break
                    end
                    local lrow = ParseSingleNoteLine(rawlines[look])
                    if lrow and NormalizeNote(lrow.note) == "..." then
                        n = n + 1
                        look = look + 1
                    else
                        break
                    end
                end
                return n
            end

            if note == "..." then
                -- A hold no preceding note could absorb - the editor shows it as a row of
                -- its own, so it has to cost time rather than silently vanish. Emitted as a
                -- wait row: spend the ticks, leave whatever is sounding alone.
                if idx > consumed_until then
                    local duration = 1 + CountHolds(idx + 1)
                    SetState(nil, duration)
                    Emit(CMD_WAIT)
                    consumed_until = idx + duration - 1
                end
            else
                local duration = 1 + CountHolds(idx + 1)
                consumed_until = idx + duration - 1

                if note == "---" then
                    -- No instrument: a note-off doesn't use one, so leaving the current
                    -- setting alone is exactly right and costs nothing.
                    SetState(nil, duration)
                    Emit(CMD_OFF)
                else
                    assert(instruments[row.inst] ~= nil,
                        "PAT" .. NN .. ": instrument " .. tostring(row.inst) .. " is not defined in INSTRUMENTS")
                    SetState(row.inst, duration)
                    Emit(GetNoteID(note))
                end
            end
        end

        idx = idx + 1
    end

    -- voice_event and the LE anchor are single bytes, so the terminator's own offset has to
    -- stay addressable. The old fixed format capped this at 85 rows; now it's a byte budget,
    -- and since an unchanged row costs one byte a pattern can hold far more than 85 of them.
    assert(#stream <= 255,
        "PAT" .. NN .. ": encodes to " .. #stream .. " bytes; the player addresses a pattern "
        .. "with a single-byte offset, so 255 is the ceiling")

    EmitRowTable("tbl_rows_pat" .. NN .. ":", stream)
    ptr_rows[#ptr_rows + 1] = "tbl_rows_pat" .. NN
end

-- Pointer table - ONE shared pair indexed by 1-based pattern number (entry 0 is a dummy),
-- not per channel, so a pattern used by several channels points at the same rows.
-- Krikkit syntax: label & 255 / label >> 8

-- pad_zero puts a dummy entry at index 0 so a 1-based id indexes the pair directly.
-- Pattern numbers need it (0 is the song-order end marker, so they can't be 0-based);
-- the song-order pointers don't, being indexed by a 0-based channel number.
local function EmitPointerPair(basename, names, pad_zero)
    local function EmitRow(suffix, expr)
        local s = ".byte "
        if pad_zero then s = s .. "0," end
        for i, name in ipairs(names) do
            if i > 1 then s = s .. "," end
            s = s .. name .. expr
        end
        asm(basename .. suffix)
        asm(s)
    end

    EmitRow("_lo:", " & 255")
    EmitRow("_hi:", " >> 8")
    asm("")
end

EmitPointerPair("tbl_rows", ptr_rows, true)

-- Instrument tables (ad_table / sr_table / wt_start), indexed by instrument number - 1.
-- wt_start points into wt_data, the shared pool of control-byte programs terminated by $00.
-- Identical programs share an entry; offset 0 is the lone $00 meaning "not running".

if instrument_count > 0 then
    local ad_values, sr_values, wt_values = {}, {}, {}
    local wt_pool, wt_offset = { "$00" }, {}   -- offset 0 = the idle sentinel

    for id = 1, instrument_count do
        ad_values[id] = string.format("$%02x", instruments[id].ad)
        sr_values[id] = string.format("$%02x", instruments[id].sr)

        local program = {}
        if instruments[id].wavetable then
            for _, b in ipairs(instruments[id].wavetable) do
                program[#program + 1] = b
            end
        else
            local wave = instruments[id].wave
            if wave % 2 == 0 then wave = wave + 1 end   -- the gate bit is what starts the note
            program[#program + 1] = wave
        end
        program[#program + 1] = 0x00                    -- hold on the last waveform
        local key = table.concat(program, ",")
        if not wt_offset[key] then
            wt_offset[key] = #wt_pool
            for _, b in ipairs(program) do
                wt_pool[#wt_pool + 1] = string.format("$%02x", b)
            end
        end
        wt_values[id] = string.format("$%02x", wt_offset[key])
    end

    assert(#wt_pool <= 256,
        "wavetable pool is " .. #wt_pool .. " bytes - voice_wt_pos is a single byte")

    -- Note pool, same shape as the wavetable pool: offset 0 is the "none" sentinel, identical
    -- programs share one entry. How it ends distinguishes arpeggio from drum: plain -> $00 to
    -- its own start, HOLD -> $00 to its last step, ROOT -> $80, the row's own note.
    local arp_values, arp_pool, arp_offset = {}, { "$00" }, {}
    for id = 1, instrument_count do
        local arp = instruments[id].arp
        if not arp then
            arp_values[id] = "$00"
        else
            local key = {}
            for _, step in ipairs(arp) do
                key[#key + 1] = step.note or (step.root and "ROOT") or (step.loop and "LOOP") or "HOLD"
            end
            key = table.concat(key, " ")

            if not arp_offset[key] then
                local start = #arp_pool
                local last = arp[#arp]
                local resume = start          -- plain: go back to the program's own start

                for _, step in ipairs(arp) do
                    if step.note then
                        arp_pool[#arp_pool + 1] = string.format("$%02x", GetNoteID(step.note))
                    elseif step.root then
                        arp_pool[#arp_pool + 1] = "$80"
                    elseif step.loop then
                        resume = #arp_pool    -- the note about to be emitted is the loop target
                    end
                end

                if not last.root then
                    if last.hold then
                        resume = #arp_pool - 1
                    end
                    assert(resume > 0,
                        "Instrument " .. string.format("%02d", id) ..
                        ": HOLD needs a note before it")
                    arp_pool[#arp_pool + 1] = "$00"
                    arp_pool[#arp_pool + 1] = string.format("$%02x", resume)
                end

                arp_offset[key] = start
            end
            arp_values[id] = string.format("$%02x", arp_offset[key])
        end
    end

    assert(#arp_pool <= 256,
        "arpeggio pool is " .. #arp_pool .. " bytes - voice_arp_pos is a single byte")

    -- Filter pool. $FF introduces a three-byte setup record and $00 ends a program, so a
    -- plain cutoff step has to stay inside $01..$FE - the only value that costs is a cutoff
    -- high byte of exactly 0 or 255, either of which is a degenerate setting anyway.
    local flt_values, flt_pool, flt_offset = {}, { "$00" }, {}
    for id = 1, instrument_count do
        local flt = instruments[id].flt
        if not flt then
            flt_values[id] = "$00"
        else
            local bytes = {}
            for _, item in ipairs(flt) do
                if item.step then
                    assert(item.step >= 0x01 and item.step <= 0xfe,
                        "Instrument " .. string.format("%02d", id) ..
                        ": FLT cutoff step must be $01..$fe, got " .. string.format("$%02x", item.step))
                    bytes[#bytes + 1] = item.step
                else
                    local mode, res, cut = item.setup[1], item.setup[2], item.setup[3]
                    assert(cut >= 0x01 and cut <= 0xfe,
                        "Instrument " .. string.format("%02d", id) ..
                        ": FLT group cutoff must be $01..$fe, got " .. string.format("$%02x", cut))
                    bytes[#bytes + 1] = 0xff
                    bytes[#bytes + 1] = mode
                    bytes[#bytes + 1] = res
                    bytes[#bytes + 1] = item.rate % 256
                    bytes[#bytes + 1] = cut
                end
            end
            bytes[#bytes + 1] = 0x00                        -- end: hold the last cutoff

            local key = table.concat(bytes, ",")
            if not flt_offset[key] then
                flt_offset[key] = #flt_pool
                for _, b in ipairs(bytes) do
                    flt_pool[#flt_pool + 1] = string.format("$%02x", b)
                end
            end
            flt_values[id] = string.format("$%02x", flt_offset[key])
        end
    end

    assert(#flt_pool <= 256,
        "filter pool is " .. #flt_pool .. " bytes - filter_pos is a single byte")

    -- Pulse width. Flat rather than pooled: two bytes per instrument beats a pool offset plus a
    -- record at any instrument count this player can reach. Defaulting to 8 (50% duty) matters -
    -- width 0 is simply silent.
    local pw_init_values, pw_add_values = {}, {}
    for id = 1, instrument_count do
        local pw = instruments[id].pw
        local width = pw and pw.width or 8
        local add   = pw and pw.add   or 0
        -- Bit 6 of the width byte is continuous: a new note leaves the channel's sweep running.
        -- Bit 5 is NOHR: skip the hard-restart AD/SR reset. Unrelated to pulse width, but this is
        -- where the free bits are.
        if pw and pw.continuous then width = width + 0x40 end
        if instruments[id].nohr then width = width + 0x20 end
        pw_init_values[id] = string.format("$%02x", width)
        pw_add_values[id]  = string.format("$%02x", add % 256)
    end

    EmitByteTable("ad_table:", ad_values)
    EmitByteTable("sr_table:", sr_values)
    EmitByteTable("wt_start:", wt_values)
    EmitByteTable("wt_data:", wt_pool)
    EmitByteTable("arp_start:", arp_values)
    EmitByteTable("arp_data:", arp_pool)
    EmitByteTable("flt_start:", flt_values)
    EmitByteTable("flt_data:", flt_pool)
    EmitByteTable("pw_init:", pw_init_values)
    EmitByteTable("pw_add:", pw_add_values)

    -- Vibrato tables, only for a song that uses it - the player's own vibrato code is gated on
    -- the same v_vibrato, so a song without it pays nothing either side. Two flat bytes per
    -- instrument: delay/speed packed in one, depth in the other.
    local any_vibrato = false
    local vib_ds_values, vib_depth_values = {}, {}
    for id = 1, instrument_count do
        local vib = instruments[id].vib
        if vib then any_vibrato = true end
        vib_ds_values[id]    = string.format("$%02x", vib and vib.ds or 0)
        vib_depth_values[id] = string.format("$%02x", vib and vib.depth or 0)
    end
    assert(any_vibrato == (v_vibrato_scan == true),
        "vibrato scan disagrees with the parsed INSTRUMENTS - v_songfile must be set before " ..
        "Tables.s is included")
    if any_vibrato then
        EmitByteTable("vib_ds:", vib_ds_values)
        EmitByteTable("vib_depth:", vib_depth_values)
    end

    -- Slide: one signed byte per instrument, emitted only for a song that uses it.
    local any_slide = false
    local slide_values = {}
    for id = 1, instrument_count do
        local slide = instruments[id].slide
        if slide then any_slide = true end
        slide_values[id] = string.format("$%02x", (slide or 0) % 256)
    end
    assert(any_slide == (v_slide_scan == true),
        "slide scan disagrees with the parsed INSTRUMENTS - v_songfile must be set before " ..
        "Tables.s is included")
    if any_slide then
        EmitByteTable("slide_rate:", slide_values)
    end
end

-- Song-order resolution per channel: a list of pattern numbers, optionally followed by
-- "restart", N (1-based position to loop back to; default 1). The three lists concatenate
-- into tbl_songorder with an absolute restart index, seeding voice_orderpos via v_ord_startN.

local function ResolveOrder(chname, tokens, pattern_count)
    local restart_idx = nil
    for i, tok in ipairs(tokens) do
        if tok:lower() == "restart" then
            assert(restart_idx == nil, chname .. ": more than one 'restart'")
            restart_idx = i
        end
    end

    local pattern_tokens = tokens
    local restart_target = 0 -- 0-based: position 1, the start of the list

    if restart_idx then
        assert(restart_idx == #tokens - 1, chname .. ": 'restart' must be the second-to-last token, followed by its target position")
        local target = tonumber(tokens[restart_idx + 1], 10)
        assert(target ~= nil, chname .. ": 'restart' target must be a number")

        pattern_tokens = {}
        for i = 1, restart_idx - 1 do
            pattern_tokens[i] = tokens[i]
        end

        assert(target >= 1 and target <= #pattern_tokens,
            chname .. ": 'restart' target " .. target .. " is out of range (1.." .. #pattern_tokens .. ")")
        restart_target = target - 1
    end

    local out = {}
    for _, tok in ipairs(pattern_tokens) do
        local n = tonumber(tok, 10)
        assert(n and n >= 1 and n <= 254, chname .. ": invalid pattern number '" .. tostring(tok) .. "'")
        assert(n <= pattern_count, chname .. ": ORDER references undefined pattern " .. n)
        out[#out + 1] = n
    end

    assert(#out > 0, chname .. ": ORDER list must contain at least one pattern")

    out[#out + 1] = 0   -- end of song-order
    out[#out + 1] = restart_target

    assert(#out <= 255, chname .. ": ORDER list exceeds 255 bytes")
    return out
end

local songorder, songorder_start = {}, {}
for ch = 1, 3 do
    local tokens = {}
    for _, line in ipairs(order_raw[ch]) do
        for tok in line:gmatch("[^,]+") do
            tok = Trim(tok)
            if tok ~= "" then
                tok = tok:gsub('^"(.*)"$', "%1")
                tokens[#tokens + 1] = tok
            end
        end
    end

    local resolved = ResolveOrder("ORDER_CH" .. ch, tokens, pattern_count)
    songorder_start[ch] = #songorder
    resolved[#resolved] = resolved[#resolved] + songorder_start[ch]
    for _, b in ipairs(resolved) do songorder[#songorder + 1] = b end
end

assert(#songorder <= 255, "song order totals " .. #songorder .. " bytes - voice_orderpos is one byte")

EmitByteTable("tbl_songorder:", songorder)
for ch = 1, 3 do
    asm("var v_ord_start" .. ch .. " = " .. songorder_start[ch])
end



-- Frequency tables for only the used notes
-- Order = first appearance order

local freq_low = { 0 }
local freq_high = { 0 }

for id = 1, #id_to_note do
    local note = id_to_note[id]
    local idx = NoteIndex(note)

    freq_low[#freq_low + 1] = sid_freq_low[idx]
    freq_high[#freq_high + 1] = sid_freq_high[idx]
end

asm("")
asm("; --------------------------------------")
asm("; SID Frequency Tables")
asm("; --------------------------------------")
asm("")

EmitByteTable("note_freq_low:", freq_low)
EmitByteTable("note_freq_high:", freq_high)

-- Note dictionary id -> chromatic index, for the musicshower's note column. The dictionary
-- is first-occurrence order, so an id says nothing about pitch on its own. v_musicshower is
-- set by Musicshower.s itself, so a .sid export never carries it.

local note_display = { 0 }
for id = 1, #id_to_note do
    note_display[#note_display + 1] = NoteIndex(id_to_note[id]) - 1
end

if v_musicshower then
    EmitByteTable("note_display:", note_display)
end

------------------------------------------------------------
-- Debug note dictionary
------------------------------------------------------------

asm("; --------------------------------------")
asm("; Note Dictionary")
asm("; --------------------------------------")

for id = 1, #id_to_note do
    asm("; " .. tostring(id) .. " = " .. id_to_note[id])
end

asm("; $D0 = NOTE_OFF   $D1 = wait   $D2 = loop-back (LE)")
asm("; $80..$BF = set duration   $C0..$CF = set instrument")
asm("; $00 = end of pattern")
asm("")

} // End LUA
} // End Macro

// Vibrato/slide switch. Runs when Tables.s is INCLUDED - before Code\Musicplayer.s, which
// the m_tables macro is too late for - so the song file is scanned here. A driver building
// something other than Patterns.s must set v_songfile before including this file.
lua {
    local f = io.open("SongData/" .. (v_songfile or "Patterns.s"), "r")
    v_vibrato_scan = false
    v_slide_scan = false
    if f then
        local in_instruments = false
        for line in f:lines() do
            line = line:gsub(";.*$", ""):gsub("//.*$", "")
            local t = line:match("^%s*(.-)%s*$")
            if t:upper() == "INSTRUMENTS:" then
                in_instruments = true
            elseif t:upper() == "END:" then
                in_instruments = false
            elseif in_instruments and t:match("^%d+%s*,") then
                if t:upper():match(",%s*VIB%s") then v_vibrato_scan = true end
                if t:upper():match(",%s*SLIDE%s") then v_slide_scan = true end
            end
        end
        f:close()
    end
    if v_vibrato_scan then
        asm("var v_vibrato = 1")
    end
    if v_slide_scan then
        asm("var v_slide = 1")
    end
    if v_vibrato_scan or v_slide_scan then
        asm("var v_freqmod = 1")
    end
}
