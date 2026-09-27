macro m_tables {
lua {

------------------------------------------------------------
-- Compact first-occurrence Tables.s
-- Notes are case-insensitive:
--   C-1 = c-1 = C-1
-- Only notes actually used in the song are stored in
-- note_freq_low / note_freq_high, in first-occurrence order.
------------------------------------------------------------

local NOTE_OFF = 255

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

local function ParseUintAfterKey(line, key)
    local p = line:find(key, 1, true)
    if not p then
        return nil
    end

    p = p + #key
    local digits = ""

    while p <= #line do
        local c = line:sub(p, p)
        if c >= "0" and c <= "9" then
            digits = digits .. c
            p = p + 1
        else
            break
        end
    end

    if digits == "" then
        return nil
    end

    return tonumber(digits, 10)
end

local function ParseNoteLine(line)
    line = line:gsub(" ", "")
    if line == "" then
        return nil
    end

    -- Expected format:
    -- "C-5",01,"D-2",02,"D#3",03
    local n1, i1, n2, i2, n3, i3 =
        line:match('^"([^"]+)",([^,]+),"([^"]+)",([^,]+),"([^"]+)",([^,]+)$')

    if not n1 then
        return nil
    end

    return {
        { note = n1, inst = tonumber(i1, 10) },
        { note = n2, inst = tonumber(i2, 10) },
        { note = n3, inst = tonumber(i3, 10) }
    }
end

------------------------------------------------------------
-- Read Patterns.s
------------------------------------------------------------

local f = io.open("SongData/Patterns.s", "r")
assert(f, "Cannot open SongData/Patterns.s")

local txt = f:read("*all")
f:close()

------------------------------------------------------------
-- Song speed
------------------------------------------------------------

local songspeed = txt:match("var%s+v_Song_Speed%s*=%s*(%d+)")

if songspeed then
    asm("var v_Song_Speed = " .. songspeed)
end

--
--local songspeed = ParseUintAfterKey(txt, "var v_Song_Speed =")
--if songspeed then
--    asm("var v_Song_Speed = " .. songspeed)
--end
--
asm("")
asm("var NOTE_OFF = 255")
asm("")

------------------------------------------------------------
-- Load SID PAL note table from external file
-- File format:
--   note_freq_hb:
--     byte $01,$01,...
--   note_freq_lb:
--     byte $12,$23,...
------------------------------------------------------------

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

------------------------------------------------------------
-- Fixed note index helper for the imported SID table
-- C-0..B-7 = 96 notes
------------------------------------------------------------

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

------------------------------------------------------------
-- Dynamic note dictionary
-- First occurrence order across the whole song
------------------------------------------------------------

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

------------------------------------------------------------
-- Collect Song Positions in file order
------------------------------------------------------------

local songpositions = {}
local songpos_order = {}
local current_sp = nil

for line in txt:gmatch("[^\r\n]+") do
    local t = Trim(line)
    if #t >= 5 and t:sub(1, 2):upper() == "SP" and t:sub(5, 5) == ":" then
        current_sp = t:sub(1, 4):upper()
        songpositions[current_sp] = {}
        songpos_order[#songpos_order + 1] = current_sp
    elseif current_sp then
        songpositions[current_sp][#songpositions[current_sp] + 1] = t
    end
end

assert(#songpos_order > 0, "No song positions found")

------------------------------------------------------------
-- Parse rows per song position
------------------------------------------------------------

local parsed_positions = {}

for _, spname in ipairs(songpos_order) do
    local rows = {}

    for _, line in ipairs(songpositions[spname]) do
        local row = ParseNoteLine(line)
        if row then
            rows[#rows + 1] = row
        end
    end

    parsed_positions[spname] = rows
end

------------------------------------------------------------
-- First pass:
-- Build the note dictionary in first-occurrence order
-- Scan row-major, channel 1 -> 3.
------------------------------------------------------------

for _, spname in ipairs(songpos_order) do
    local rows = parsed_positions[spname]

    for row = 1, #rows do
        for ch = 1, 3 do
            local note = NormalizeNote(rows[row][ch].note)
            if note ~= "..." and note ~= "---" then
                GetNoteID(note)
            end
        end
    end
end

------------------------------------------------------------
-- Storage for pointer tables
------------------------------------------------------------

local ptr_note = {}
local ptr_inst = {}
local ptr_dur  = {}

for ch = 1, 3 do
    ptr_note[ch] = {}
    ptr_inst[ch] = {}
    ptr_dur[ch]  = {}
end

------------------------------------------------------------
-- Second pass:
-- Emit per song-position tables
------------------------------------------------------------

for _, spname in ipairs(songpos_order) do
    local rows = parsed_positions[spname]

    for ch = 1, 3 do
        local note_tbl = {}
        local inst_tbl = {}
        local dur_tbl  = {}

        local row = 1
        while row <= #rows do
            local note = NormalizeNote(rows[row][ch].note)
            local inst = rows[row][ch].inst

            if note ~= "..." then
                local duration = 1
                local look = row + 1

                while look <= #rows do
                    local nextnote = NormalizeNote(rows[look][ch].note)
                    if nextnote == "..." then
                        duration = duration + 1
                        look = look + 1
                    else
                        break
                    end
                end

                if note == "---" then
                    note_tbl[#note_tbl + 1] = NOTE_OFF
                    inst_tbl[#inst_tbl + 1] = 0
                else
                    local id = GetNoteID(note)
                    note_tbl[#note_tbl + 1] = id
                    inst_tbl[#inst_tbl + 1] = inst
                end

                dur_tbl[#dur_tbl + 1] = duration
            end

            row = row + 1
        end

        local splabel = spname:lower()

        EmitByteTable("tbl_note_ch" .. ch .. "_" .. splabel .. ":", note_tbl)
        EmitByteTable("tbl_inst_ch" .. ch .. "_" .. splabel .. ":", inst_tbl)
        EmitByteTable("tbl_dur_ch"  .. ch .. "_" .. splabel .. ":", dur_tbl)

        asm("var tbl_count_ch" .. ch .. "_" .. splabel .. " = " .. #note_tbl)
        asm("")

        ptr_note[ch][#ptr_note[ch] + 1] = "tbl_note_ch" .. ch .. "_" .. splabel
        ptr_inst[ch][#ptr_inst[ch] + 1] = "tbl_inst_ch" .. ch .. "_" .. splabel
        ptr_dur[ch][#ptr_dur[ch] + 1]   = "tbl_dur_ch" .. ch .. "_" .. splabel
    end
end

------------------------------------------------------------
-- Pointer tables
-- Krikkit syntax:
--   label & 255
--   label >> 8
------------------------------------------------------------

for ch = 1, 3 do
    asm("tbl_ch" .. ch .. "_note_lo:")
    do
        local s = ".byte "
        for i, name in ipairs(ptr_note[ch]) do
            if i > 1 then s = s .. "," end
            s = s .. name .. " & 255"
        end
        asm(s)
    end

    asm("tbl_ch" .. ch .. "_note_hi:")
    do
        local s = ".byte "
        for i, name in ipairs(ptr_note[ch]) do
            if i > 1 then s = s .. "," end
            s = s .. name .. " >> 8"
        end
        asm(s)
    end
    asm("")

    asm("tbl_ch" .. ch .. "_inst_lo:")
    do
        local s = ".byte "
        for i, name in ipairs(ptr_inst[ch]) do
            if i > 1 then s = s .. "," end
            s = s .. name .. " & 255"
        end
        asm(s)
    end

    asm("tbl_ch" .. ch .. "_inst_hi:")
    do
        local s = ".byte "
        for i, name in ipairs(ptr_inst[ch]) do
            if i > 1 then s = s .. "," end
            s = s .. name .. " >> 8"
        end
        asm(s)
    end
    asm("")

    asm("tbl_ch" .. ch .. "_dur_lo:")
    do
        local s = ".byte "
        for i, name in ipairs(ptr_dur[ch]) do
            if i > 1 then s = s .. "," end
            s = s .. name .. " & 255"
        end
        asm(s)
    end

    asm("tbl_ch" .. ch .. "_dur_hi:")
    do
        local s = ".byte "
        for i, name in ipairs(ptr_dur[ch]) do
            if i > 1 then s = s .. "," end
            s = s .. name .. " >> 8"
        end
        asm(s)
    end
    asm("")
end

------------------------------------------------------------
-- Frequency tables for only the used notes
-- Order = first appearance order
------------------------------------------------------------

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

------------------------------------------------------------
-- Debug note dictionary
------------------------------------------------------------

asm("; --------------------------------------")
asm("; Note Dictionary")
asm("; --------------------------------------")

for id = 1, #id_to_note do
    asm("; " .. tostring(id) .. " = " .. id_to_note[id])
end

asm("; 255 = NOTE_OFF")
asm("")

} // End LUA
} // End Macro
