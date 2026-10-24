"""Compares ImportBench runs, the first named as the base.
Usage: python compare.py <name> [<name> ...]   (reads results/<name>.sng.txt and .pitch.txt)"""
import os
import re
import statistics
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
names = sys.argv[1:]


def path(name, part):
    return os.path.join(HERE, 'results', '%s.%s.txt' % (name, part))


def sng(name):
    songs = []
    for l in open(path(name, 'sng'), encoding='utf-8'):
        d = dict((m.group(1), float(m.group(2))) for m in re.finditer(r'(\w+) ([\d.]+)', l))
        if 'want' in d:
            d['title'] = l.split('\t')[0]
            songs.append(d)
    rows, seen = [], set()
    for l in open(path(name, 'sng') + '.instruments', encoding='utf-8'):
        x = l.rstrip('\r\n').split('\t')
        if len(x) < 13 or (x[0], x[1]) in seen:
            continue
        seen.add((x[0], x[1]))
        rows.append(dict((m.group(1), float(m.group(2))) for m in re.finditer(r'(\w+) (-?[\d.]+)', '\t'.join(x[4:13]))))
    w = sum(d['want'] for d in songs)
    placed = sum(d['want'] * d['placed'] / 100 for d in songs)
    transposed = sum(d['want'] * d['placed'] / 100 * d['rightT'] / 100 for d in songs)
    judged = sum(d['judged'] for d in songs)
    sounding = sum(d['judged'] * d['sound'] / 100 for d in songs)
    return dict(songs={d['title']: d for d in songs}, notes=w, placed=placed / w * 100, transpose=transposed / w * 100,
                heard=sum(r['rightA'] for r in rows) / sum(r['placed'] for r in rows) * 100,
                sid=sounding / judged * 100, extra=sum(d['extra'] for d in songs), bytes=sum(d['bytes'] for d in songs))


def pitch(name):
    tunes = {}
    for l in open(path(name, 'pitch'), encoding='utf-8', errors='replace'):
        m = re.match(r'(.{44}) tune \S+ pitch ([\d.]+) .* bytes (\d+)', l)
        if m:
            tunes[m.group(1).strip() + '#' + str(len(tunes))] = (float(m.group(2)), int(m.group(3)))
    return tunes


def changes(base, now):
    return sorted((round(now[k] - base[k], 1), k.split('#')[0]) for k in base if k in now and abs(now[k] - base[k]) > 0.5)


base_s, base_p = sng(names[0]), pitch(names[0])
print('%-10s %6s %7s %7s %8s %8s %6s %7s | %7s %6s %7s | %s' % (
    'run', 'notes', 'placed', 'transp', 'asheard', 'sidpitch', 'extra', 'bytes', 'pitch', 'median', 'bytes', 'changed vs base'))
for n in names:
    s, p = sng(n), pitch(n)
    keys = [k for k in base_p if k in p]
    heard = {k: p[k][0] for k in keys}
    by_pitch = changes({k: base_p[k][0] for k in keys}, heard)
    by_song = changes({t: d['rightA'] for t, d in base_s['songs'].items()}, {t: d['rightA'] for t, d in s['songs'].items()})
    print('%-10s %6d %6.1f%% %6.1f%% %7.1f%% %7.1f%% %6d %7d | %6.2f%% %5.1f%% %7d | pitch %d+ %d-  songs %d+ %d-' % (
        n, s['notes'], s['placed'], s['transpose'], s['heard'], s['sid'], s['extra'], s['bytes'],
        statistics.mean(heard.values()), statistics.median(heard.values()), sum(p[k][1] for k in keys),
        sum(c[0] > 0 for c in by_pitch), sum(c[0] < 0 for c in by_pitch), sum(c[0] > 0 for c in by_song), sum(c[0] < 0 for c in by_song)))
    for label, ch in (('pitch', by_pitch), ('song', by_song)):
        for c in ch[:3] + [c for c in ch[-3:] if c not in ch[:3]]:
            print('           %s %+5.1f %s' % (label, c[0], c[1]))
