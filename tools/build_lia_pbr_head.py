#!/usr/bin/env python3
"""Bake Microsoft Rocketbox Female_Adult_07 (MIT) head → Lia PBR runtime assets.

Source: https://github.com/microsoft/Microsoft-Rocketbox
License: MIT (see THIRD_PARTY.md)
"""
from __future__ import annotations

import json
import struct
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SRC_ROOT = Path("/tmp/v050/rocketbox/Assets/Avatars/Adults/Female_Adult_07")
FBX = SRC_ROOT / "Export" / "Female_Adult_07_facial.fbx"
TEX = SRC_ROOT / "Textures"
OUT = ROOT / "LiaDesktop.WinUI/src/LiaDesktop/Assets/models"
OUT.mkdir(parents=True, exist_ok=True)

# ARKit / viseme names we keep (Rocketbox Headbox naming)
KEEP_SHAPES = {
    "AK_25_JawOpen": "jawOpen",
    "AK_32_MouthFunnel": "mouthFunnel",
    "AK_38_MouthPucker": "mouthPucker",
    "AK_44_MouthSmileLeft": "mouthSmileLeft",
    "AK_45_MouthSmileRight": "mouthSmileRight",
    "AK_09_EyeBlinkLeft": "eyeBlinkLeft",
    "AK_10_EyeBlinkRight": "eyeBlinkRight",
    "AK_03_BrowInnerUp": "browInnerUp",
    "AA_VI_10_aa": "viseme_aa",
    "AA_VI_11_E": "viseme_E",
    "AA_VI_13_O": "viseme_O",
}


def ensure_source() -> None:
    if FBX.is_file() and (TEX / "f007_head_color.tga").is_file():
        return
    raise SystemExit(
        f"Rocketbox Female_Adult_07 missing under {SRC_ROOT}. "
        "Sparse-clone Microsoft-Rocketbox first."
    )


def write_png(src: Path, dst: Path, size: int = 2048, mode: str = "RGB") -> None:
    img = Image.open(src).convert(mode)
    if img.size[0] != size:
        img = img.resize((size, size), Image.Resampling.LANCZOS)
    dst.parent.mkdir(parents=True, exist_ok=True)
    img.save(dst, optimize=True)
    print(f"wrote {dst} {img.size} {mode}")


def build_orm(spec_path: Path, dst: Path, size: int = 2048) -> None:
    """Pack ORM: R=AO(1), G=roughness from inverted specular, B=metallic(0)."""
    spec = Image.open(spec_path).convert("L").resize((size, size), Image.Resampling.LANCZOS)
    arr = np.asarray(spec, dtype=np.float32) / 255.0
    # Rocketbox specular is glossy highlight amount → rough ≈ 1 - spec*0.85
    rough = np.clip(1.0 - arr * 0.85, 0.18, 0.95)
    orm = np.zeros((size, size, 3), dtype=np.uint8)
    orm[:, :, 0] = 255
    orm[:, :, 1] = (rough * 255).astype(np.uint8)
    orm[:, :, 2] = 0
    Image.fromarray(orm, "RGB").save(dst, optimize=True)
    print(f"wrote {dst} ORM")


BLENDER_SCRIPT = r'''
import bpy, json, struct, math
from mathutils import Vector, Matrix
from pathlib import Path

fbx = Path(r"__FBX__")
out_dir = Path(r"__OUT__")
keep = __KEEP__

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=str(fbx))

# Find skinned mesh
mesh_obj = None
for o in bpy.data.objects:
    if o.type == "MESH" and o.data.shape_keys:
        mesh_obj = o
        break
if mesh_obj is None:
    raise RuntimeError("no morph mesh")

# Apply armature rest, convert to world, center head
bpy.context.view_layer.objects.active = mesh_obj
mesh_obj.select_set(True)

# Drop armature modifier influence by applying visual + clear parents later via mesh copy
deps = bpy.context.evaluated_depsgraph_get()
eval_obj = mesh_obj.evaluated_get(deps)
me = bpy.data.meshes.new_from_object(eval_obj)

# Build material index sets from original
mat_names = [slot.material.name if slot.material else "" for slot in mesh_obj.material_slots]
head_idxs, opac_idxs, body_idxs = set(), set(), set()
for pi, poly in enumerate(mesh_obj.data.polygons):
    mi = poly.material_index
    name = mat_names[mi] if mi < len(mat_names) else ""
    if "opacity" in name.lower():
        opac_idxs.add(pi)
    elif "head" in name.lower():
        head_idxs.add(pi)
    else:
        body_idxs.add(pi)

# Collect shape key deltas from ORIGINAL mesh (same vertex order as evaluated base)
sk = mesh_obj.data.shape_keys
basis = sk.key_blocks["Basis"].data
n = len(basis)

def shape_delta(name):
    kb = sk.key_blocks.get(name)
    if kb is None:
        return None
    d = [0.0] * (n * 3)
    for i in range(n):
        b = basis[i].co
        c = kb.data[i].co
        d[i*3+0] = c.x - b.x
        d[i*3+1] = c.y - b.y
        d[i*3+2] = c.z - b.z
    return d

# World positions from evaluated mesh (Rocketbox FBX stays Z-up after import)
me.transform(eval_obj.matrix_world)
# Z-up → Y-up, then yaw 180 so face looks +Z
rot_x = Matrix.Rotation(-math.pi / 2, 4, "X")
flip = Matrix.Diagonal((-1.0, 1.0, -1.0, 1.0))
xform = flip @ rot_x
coords = [xform @ v.co for v in me.vertices]
nmat = (flip @ rot_x).to_3x3()

ys = [c.y for c in coords]
miny, maxy = min(ys), max(ys)
height = maxy - miny
scale = 1.70 / height if height > 1e-6 else 1.0
cx = sum(c.x for c in coords) / len(coords)
# Head top ~1.70 after scale from feet; put neck/chin near y=-0.35, crown ~0.55
for i, c in enumerate(coords):
    coords[i] = Vector(((c.x - cx) * scale, (c.y - miny) * scale - 1.15, c.z * scale))

def xform_delta(dx, dy, dz):
    v = nmat @ Vector((dx, dy, dz))
    return Vector((v.x * scale, v.y * scale, v.z * scale))

def gather(poly_set, y_min=-0.40):
    """Expand polygons to unique corners; filter by centroid Y (head+neck)."""
    uv_layer = mesh_obj.data.uv_layers.active
    positions=[]; normals=[]; uvs=[]; indices=[]; old_of=[]
    keymap={}
    for pi in poly_set:
        poly = mesh_obj.data.polygons[pi]
        cents = [coords[vi] for vi in poly.vertices]
        cy = sum(c.y for c in cents) / len(cents)
        if cy < y_min:
            continue
        loop_ids = list(poly.loop_indices)
        tri_verts = list(poly.vertices)
        for t in range(1, len(tri_verts) - 1):
            tri = [tri_verts[0], tri_verts[t], tri_verts[t + 1]]
            tri_loops = [loop_ids[0], loop_ids[t], loop_ids[t + 1]]
            for vi, li in zip(tri, tri_loops):
                uv = uv_layer.data[li].uv
                key = (vi, round(uv.x, 5), round(uv.y, 5))
                if key not in keymap:
                    keymap[key] = len(positions)
                    p = coords[vi]
                    nrm = mesh_obj.data.vertices[vi].normal.copy()
                    nrm = (nmat @ (eval_obj.matrix_world.to_3x3() @ nrm)).normalized()
                    positions.append((p.x, p.y, p.z))
                    normals.append((nrm.x, nrm.y, nrm.z))
                    uvs.append((uv.x, 1.0 - uv.y))  # DX UV flip
                    old_of.append(vi)
                indices.append(keymap[key])
    return positions, normals, uvs, indices, old_of

# Head atlas only — body material uses a different UV atlas
head = gather(head_idxs, y_min=-0.55)
opac = gather(opac_idxs, y_min=-0.55)

shape_out = {}
for src_name, dst_name in keep.items():
    d = shape_delta(src_name)
    if d is None:
        print("missing shape", src_name)
        continue
    mapped = []
    for old_i in head[4]:
        v = xform_delta(d[old_i * 3], d[old_i * 3 + 1], d[old_i * 3 + 2])
        mapped.extend([v.x, v.y, v.z])
    shape_out[dst_name] = mapped
    print("shape", dst_name, "ok")
def write_bin(path, positions, normals, uvs, indices, shapes: dict):
    # format: magic 'LIAH' ver1
    # u32 nV, nI, nShapes
    # for each shape: u8 namelen, name utf8, then nV * 3 f32 deltas
    # then positions f32*3, normals f32*3, uvs f32*2, indices u32
    nV = len(positions)
    nI = len(indices)
    blob = bytearray()
    blob += b"LIAH"
    blob += struct.pack("<I", 1)
    blob += struct.pack("<III", nV, nI, len(shapes))
    for name, deltas in shapes.items():
        nb = name.encode("utf-8")
        blob += struct.pack("<B", len(nb))
        blob += nb
        blob += struct.pack("<" + "f"*len(deltas), *deltas)
    for p in positions:
        blob += struct.pack("<fff", *p)
    for n in normals:
        blob += struct.pack("<fff", *n)
    for u in uvs:
        blob += struct.pack("<ff", *u)
    blob += struct.pack("<" + "I"*nI, *indices)
    path.write_bytes(blob)
    print("wrote", path, "verts", nV, "idx", nI, "shapes", list(shapes))

write_bin(out_dir / "lia_pbr_head.bin", head[0], head[1], head[2], head[3], shape_out)
write_bin(out_dir / "lia_pbr_hair.bin", opac[0], opac[1], opac[2], opac[3], {})

eyes_l=[]; eyes_r=[]
for p in head[0]:
    x,y,z = p
    if y < 0.12 or y > 0.42: continue
    if z < 0.02: continue
    if -0.14 < x < -0.04: eyes_l.append(p)
    if 0.04 < x < 0.14: eyes_r.append(p)

def avg(pts):
    if not pts: return [0.0, 0.28, 0.14]
    return [sum(p[i] for p in pts)/len(pts) for i in range(3)]

meta = {
    "source": "Microsoft Rocketbox Female_Adult_07",
    "license": "MIT",
    "url": "https://github.com/microsoft/Microsoft-Rocketbox",
    "eyeL": avg(eyes_l),
    "eyeR": avg(eyes_r),
    "shapes": list(shape_out.keys()),
    "verts_head": len(head[0]),
    "verts_hair": len(opac[0]),
}
(out_dir / "lia_pbr_meta.json").write_text(json.dumps(meta, indent=2))
print("meta", meta)
'''


def run_blender() -> None:
    script = BLENDER_SCRIPT
    script = script.replace("__FBX__", str(FBX))
    script = script.replace("__OUT__", str(OUT))
    script = script.replace("__KEEP__", repr(KEEP_SHAPES))
    py = Path("/tmp/v050/bake_pbr_head.py")
    py.write_text(script)
    cmd = ["blender", "-b", "--python", str(py)]
    print("running", " ".join(cmd))
    r = subprocess.run(cmd, capture_output=True, text=True)
    Path("/tmp/v050/bake_pbr_log.txt").write_text(r.stdout + "\n" + r.stderr)
    print(r.stdout[-3000:] if r.stdout else "")
    if r.returncode != 0:
        print(r.stderr[-2000:])
        raise SystemExit(f"blender failed: {r.returncode}")


def main() -> None:
    ensure_source()
    write_png(TEX / "f007_head_color.tga", OUT / "lia_pbr_albedo.png", 2048)
    write_png(TEX / "f007_head_normal.tga", OUT / "lia_pbr_normal.png", 2048)
    write_png(TEX / "f007_opacity_color.tga", OUT / "lia_pbr_opacity.png", 1024, mode="RGBA")
    build_orm(TEX / "f007_head_specular.tga", OUT / "lia_pbr_orm.png", 1024)
    # also body albedo for neck continuity — sample head is enough if body mats included
    if (TEX / "f007_body_color.tga").is_file():
        # merge unused; head material uses head atlas. Body uses body atlas — need 2nd set
        write_png(TEX / "f007_body_color.tga", OUT / "lia_pbr_body_albedo.png", 1024)
        write_png(TEX / "f007_body_normal.tga", OUT / "lia_pbr_body_normal.png", 1024)
        build_orm(TEX / "f007_body_specular.tga", OUT / "lia_pbr_body_orm.png", 1024)
    run_blender()
    print("done →", OUT)


if __name__ == "__main__":
    main()
