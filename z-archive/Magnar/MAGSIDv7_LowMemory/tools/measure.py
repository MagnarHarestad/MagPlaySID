"""Measures every labelled region of the player by subtracting adjacent symbol addresses.

    python tools\\measure.py program.sym 12aa

The end address is exclusive, and comes from the assembler's own memory map - the line for the
last "M_Music Player" segment. Subtracting symbol addresses is the only reliable way to size a
region here; counting instructions by hand goes wrong as soon as a branch changes length.

Local labels (Voice_Tick:_nextcmd and friends) sit inside their parent's region and are skipped,
since including them would just cut one routine into arbitrary pieces. Pass --detail to keep
them, which is how you break a large routine down internally.
"""
import re
import sys

CODE = {
    "Voice_NoteOff", "Voice_AdvanceOrder", "Voice_SetFreq", "Voice_PlayCurrent",
    "Voice_Automation", "music_init", "music_play", "ONbeat_PlaySong", "Player_Play",
    "Voice_Tick", "Filter_Play",
}
GLOBAL_STATE = {"Song_Speed", "SID_channel_offset"}

JMP_TABLE_BYTES = 6      # the two JMPs at $1000 carry no label of their own


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    detail = "--detail" in sys.argv

    if len(args) != 2:
        print(__doc__)
        return 2

    sym_path, end = args[0], int(args[1], 16)

    labels = []
    for line in open(sym_path, encoding="utf-8", errors="replace"):
        m = re.match(r"add_label ([0-9a-f]+) \.(\S+)", line)
        if not m:
            continue
        addr, name = int(m.group(1), 16), m.group(2)
        if ":_" in name and not detail:
            continue
        if 0x1000 <= addr < end:
            labels.append((addr, name))

    labels.sort()

    # Two labels can share an address when one immediately follows the other.
    merged = []
    for addr, name in labels:
        if merged and merged[-1][0] == addr:
            merged[-1] = (addr, merged[-1][1] + " / " + name)
        else:
            merged.append((addr, name))

    code = state = data = 0
    print(f"{'addr':>6}  {'size':>4}  name")
    for i, (addr, name) in enumerate(merged):
        size = (merged[i + 1][0] if i + 1 < len(merged) else end) - addr
        print(f" ${addr:04x}  {size:4d}  {name}")

        head = name.split(":")[0]
        if head in CODE:
            code += size
        elif head.startswith("voice_") or head.startswith("filter_") or head in GLOBAL_STATE:
            state += size
        else:
            data += size

    code += JMP_TABLE_BYTES
    print()
    print(f"code                 {code:5d}")
    print(f"state                {state:5d}   (plus 3 zero-page: ZP_lb, ZP_hb, ZP_ch)")
    print(f"player, song-free    {code + state:5d}")
    print(f"song data            {data:5d}")
    print(f"total                {code + state + data:5d}   (${0x1000:04x}-${end - 1:04x})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
