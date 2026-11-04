# Exports the Dire Guys from DireGuys_new.blend into src/MagPlaySID/Player/DireGuys.bin, the model
# the Dire Guys meter dances. Run it with Blender (3.x) from the repository's root:
#
#   blender -b 3d-objects/DireGuys_new.blend --python 3d-objects/export-direguys.py -- src/MagPlaySID/Player/DireGuys.bin
#
# The file: "DIRE", the number of parts, then each mesh object as a part - its name, which guy (0 the
# one under RootBlue, 1 under RootRed), the part it hangs from (-1 for none), its origin - the joint it
# turns about - and its triangles: a normal, a colour (its material's viewport colour, in sRGB, with
# the triangle's layer in the top byte where the alpha would be) and three corners. Little-endian;
# positions in metres, turned from Blender's Z up to Y up, so the guys face +Z.
#
# Layers: the app paints triangles back to front by their middles, which cannot tell a surface from
# one lying a few millimetres on top of it - the blue guy's bib on his shirt, a button on the bib.
# So each triangle lying on another of its part, within LAYER metres outward of it, is a layer above
# that one (1, or 2 for a button on the bib), and is painted as nearer by that much; and the
# triangles of such layers are cut into pieces no longer than PIECE, so their middles are near
# where they lie.
import bpy, struct, sys
from mathutils import Vector
from mathutils.bvhtree import BVHTree
LAYER = 0.015
PIECE = 0.08
out = sys.argv[sys.argv.index('--') + 1]
def srgb(c):
    return 255 * (12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055)
def colour(mat):
    if mat is None:
        return 0x808080
    r, g, b, _ = mat.diffuse_color
    return (round(srgb(r)) << 16) | (round(srgb(g)) << 8) | round(srgb(b))
def yup(v):
    return (v.x, v.z, -v.y)
def pieces(a, b, c):
    """The triangle cut at its longest edge's middle until no edge is longer than PIECE."""
    edges = [((a - b).length, 0), ((b - c).length, 1), ((c - a).length, 2)]
    longest, which = max(edges)
    if longest <= PIECE:
        return [(a, b, c)]
    if which == 0:
        m = (a + b) / 2
        return pieces(a, m, c) + pieces(m, b, c)
    if which == 1:
        m = (b + c) / 2
        return pieces(a, b, m) + pieces(a, m, c)
    m = (c + a) / 2
    return pieces(a, b, m) + pieces(m, b, c)
deps = bpy.context.evaluated_depsgraph_get()
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
def root(o):
    while o.parent:
        o = o.parent
    return o.name
names = [o.name for o in meshes]
data = bytearray()
data += struct.pack('<4sI', b'DIRE', len(meshes))
for o in meshes:
    ev = o.evaluated_get(deps)
    m = ev.to_mesh()
    m.calc_loop_triangles()
    mw = o.matrix_world
    nm = mw.to_3x3().inverted().transposed()
    world = [mw @ v.co for v in m.vertices]
    tris = list(m.loop_triangles)
    tree = BVHTree.FromPolygons(world, [tuple(t.vertices) for t in tris])
    lo = Vector([min(p[k] for p in world) for k in range(3)])
    hi = Vector([max(p[k] for p in world) for k in range(3)])
    centre = (lo + hi) / 2

    # Each triangle's normal turned outward - some of the model's point in - and what it lies on.
    corners = [[world[v] for v in t.vertices] for t in tris]
    middles = [sum(c, Vector()) / 3 for c in corners]
    normals = []
    for t, mid in zip(tris, middles):
        n = (nm @ t.normal).normalized()
        normals.append(n if n.dot(mid - centre) >= 0 else -n)
    under = []
    for i, (mid, n) in enumerate(zip(middles, normals)):
        hit, normal, index, dist = tree.ray_cast(mid - n * 1e-4, -n, LAYER)
        under.append(index if hit is not None and index != i else None)
    levels = [None] * len(tris)
    def level(i, depth=0):
        if levels[i] is None:
            levels[i] = 0 if under[i] is None or depth > 8 else min(3, 1 + level(under[i], depth + 1))
        return levels[i]
    layered = set()
    for i in range(len(tris)):
        if level(i) > 0:
            layered.add(i)
            layered.add(under[i])

    out_tris = []
    for i, t in enumerate(tris):
        mat = o.material_slots[t.material_index].material if o.material_slots else None
        rgb = colour(mat) | (level(i) << 24)
        a, b, c = corners[i]
        for piece in (pieces(a, b, c) if i in layered else [(a, b, c)]):
            out_tris.append((normals[i], rgb, piece))

    parent = names.index(o.parent.name) if o.parent and o.parent.name in names else -1
    who = 0 if root(o) == 'RootBlue' else 1
    name = o.name.encode()
    data += struct.pack('<B', len(name)) + name + struct.pack('<ii3fI', who, parent, *yup(mw.translation), len(out_tris))
    for n, rgb, piece in out_tris:
        data += struct.pack('<3fI', *yup(n), rgb)
        for p in piece:
            data += struct.pack('<3f', *yup(p))
    layers = sum(1 for i in range(len(tris)) if level(i) > 0)
    print('PART', o.name, 'who', who, 'triangles', len(tris), '->', len(out_tris), 'layered', layers)
    ev.to_mesh_clear()
open(out, 'wb').write(data)
print('BYTES', len(data))
