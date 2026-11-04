# Exports the Dire Guys from DireGuys_new.blend into src/MagPlaySID/Player/DireGuys.bin, the model
# the Dire Guys meter dances. Run it with Blender (3.x) from the repository's root:
#
#   blender -b 3d-objects/DireGuys_new.blend --python 3d-objects/export-direguys.py -- src/MagPlaySID/Player/DireGuys.bin
#
# The file: "DIRE", the number of parts, then each mesh object as a part - its name, which guy (0 the
# one under RootBlue, 1 under RootRed), the part it hangs from (-1 for none), its origin - the joint it
# turns about - and its triangles: a normal, a colour (its material's viewport colour, in sRGB) and
# three corners. Little-endian; positions in metres, turned from Blender's Z up to Y up, so the
# guys face +Z.
import bpy, struct, sys
from mathutils import Vector
out = sys.argv[sys.argv.index('--') + 1]
def srgb(c):
    return 255 * (12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055)
def colour(mat):
    if mat is None:
        return 0xFF808080
    r, g, b, _ = mat.diffuse_color
    return 0xFF000000 | (round(srgb(r)) << 16) | (round(srgb(g)) << 8) | round(srgb(b))
def yup(v):
    return (v.x, v.z, -v.y)
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
    tris = m.loop_triangles
    parent = names.index(o.parent.name) if o.parent and o.parent.name in names else -1
    who = 0 if root(o) == 'RootBlue' else 1
    name = o.name.encode()
    data += struct.pack('<B', len(name)) + name + struct.pack('<ii3fI', who, parent, *yup(mw.translation), len(tris))
    lo = Vector((1e9,)*3); hi = Vector((-1e9,)*3)
    for t in tris:
        n = (nm @ t.normal).normalized()
        mat = o.material_slots[t.material_index].material if o.material_slots else None
        data += struct.pack('<3fI', *yup(n), colour(mat))
        for vi in t.vertices:
            p = mw @ m.vertices[vi].co
            data += struct.pack('<3f', *yup(p))
            for k in range(3):
                q = yup(p)[k]
                lo[k] = min(lo[k], q); hi[k] = max(hi[k], q)
    org = yup(mw.translation)
    print('PART', o.name, 'who', who, 'parent', o.parent.name if o.parent else None, 'tris', len(tris),
          'origin', tuple(round(v, 3) for v in org), 'lo', tuple(round(v, 3) for v in lo), 'hi', tuple(round(v, 3) for v in hi))
    ev.to_mesh_clear()
open(out, 'wb').write(data)
print('BYTES', len(data))
