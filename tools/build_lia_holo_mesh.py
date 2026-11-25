#!/usr/bin/env python3
"""Build lia_holo_mesh.bin (+ hair/lash companion bins) from argus-mask stock-02 (CC0).

Provenance: MakeHuman / MPFB CC0 female ~25 with 52 ARKit blendshapes + short02 hair.
Source GLB: https://github.com/smk762/argus-mask/releases/download/stock-heads/stock-02-r3.glb
License: CC0-1.0
"""
from __future__ import annotations

import json
import struct
import sys
from pathlib import Path

import numpy as np
from pygltflib import GLTF2

ROOT = Path(__file__).resolve().parents[1]
OUT_DIR = ROOT / "LiaDesktop.WinUI/src/LiaDesktop/Assets/models"
STOCK_URL = "https://github.com/smk762/argus-mask/releases/download/stock-heads/stock-02-r3.glb"
STOCK_SHA = "aafedb7eb871dc697fa484de6c3c700652640f2f24dfa8031b9290aef2a08729"

# Match previous lia bust framing: head ~ unit-ish, eyes around y≈0.25 z≈0.39
TARGET_EYE_Y = 0.25
TARGET_EYE_Z = 0.40
TARGET_HEAD_HEIGHT = 1.55  # crown-to-chin after scale


def load_glb(path: Path) -> tuple[GLTF2, bytes]:
    g = GLTF2().load(str(path))
    return g, g.binary_blob()


def read_accessor(g: GLTF2, blob: bytes, acc_idx: int) -> np.ndarray:
    acc = g.accessors[acc_idx]
    ncomp = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[acc.type]
    ctype = {
        5120: np.int8,
        5121: np.uint8,
        5122: np.int16,
        5123: np.uint16,
        5125: np.uint32,
        5126: np.float32,
    }[acc.componentType]

    if acc.sparse is not None:
        base = np.zeros((acc.count, ncomp), dtype=np.float32)
        if acc.bufferView is not None:
            bv = g.bufferViews[acc.bufferView]
            off = (bv.byteOffset or 0) + (acc.byteOffset or 0)
            raw = np.frombuffer(blob, dtype=ctype, count=acc.count * ncomp, offset=off)
            base = raw.astype(np.float32).reshape(acc.count, ncomp)
        sp = acc.sparse
        ibv = g.bufferViews[sp.indices.bufferView]
        ioff = (ibv.byteOffset or 0) + (sp.indices.byteOffset or 0)
        ictype = {5121: np.uint8, 5123: np.uint16, 5125: np.uint32}[sp.indices.componentType]
        indices = np.frombuffer(blob, dtype=ictype, count=sp.count, offset=ioff)
        vbv = g.bufferViews[sp.values.bufferView]
        voff = (vbv.byteOffset or 0) + (sp.values.byteOffset or 0)
        vals = np.frombuffer(blob, dtype=ctype, count=sp.count * ncomp, offset=voff)
        vals = vals.astype(np.float32).reshape(sp.count, ncomp)
        base[indices] = vals
        return base

    bv = g.bufferViews[acc.bufferView]
    off = (bv.byteOffset or 0) + (acc.byteOffset or 0)
    raw = np.frombuffer(blob, dtype=ctype, count=acc.count * ncomp, offset=off)
    arr = raw.astype(np.float32)
    return arr.reshape(acc.count, ncomp) if ncomp > 1 else arr.reshape(acc.count, 1)


def read_indices(g: GLTF2, blob: bytes, prim) -> np.ndarray:
    idx = read_accessor(g, blob, prim.indices).reshape(-1).astype(np.int32)
    return idx


def expand_morph(g: GLTF2, blob: bytes, prim, target_idx: int, n_verts: int) -> np.ndarray:
    t = prim.targets[target_idx]
    pos_acc = t["POSITION"] if isinstance(t, dict) else t.POSITION
    return read_accessor(g, blob, pos_acc).astype(np.float32)


def find_mesh(g: GLTF2, name: str):
    for i, m in enumerate(g.meshes):
        if m.name == name:
            return i, m
    raise KeyError(name)


def subdivide_once(pos: np.ndarray, indices: np.ndarray, *morphs: np.ndarray):
    """Simple mid-edge subdivision (×4 tris) — smoother female bust for hologram."""
    tris = indices.reshape(-1, 3)
    edge_mid: dict[tuple[int, int], int] = {}
    new_pos = [pos[i] for i in range(len(pos))]
    new_morphs = [[m[i] for i in range(len(pos))] for m in morphs]

    def midpoint(a: int, b: int) -> int:
        key = (a, b) if a < b else (b, a)
        if key in edge_mid:
            return edge_mid[key]
        idx = len(new_pos)
        new_pos.append(0.5 * (pos[a] + pos[b]))
        for mi, m in enumerate(morphs):
            new_morphs[mi].append(0.5 * (m[a] + m[b]))
        edge_mid[key] = idx
        return idx

    new_tris = []
    for a, b, c in tris:
        ab, bc, ca = midpoint(a, b), midpoint(b, c), midpoint(c, a)
        new_tris.extend([a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca])
    pos2 = np.asarray(new_pos, dtype=np.float32)
    morphs2 = [np.asarray(m, dtype=np.float32) for m in new_morphs]
    idx2 = np.asarray(new_tris, dtype=np.int32)
    return pos2, idx2, morphs2


def recompute_normals(pos: np.ndarray, indices: np.ndarray) -> np.ndarray:
    nrm = np.zeros_like(pos)
    tris = indices.reshape(-1, 3)
    v0 = pos[tris[:, 0]]
    v1 = pos[tris[:, 1]]
    v2 = pos[tris[:, 2]]
    fn = np.cross(v1 - v0, v2 - v0)
    for i in range(3):
        np.add.at(nrm, tris[:, i], fn)
    lens = np.linalg.norm(nrm, axis=1, keepdims=True)
    lens = np.maximum(lens, 1e-8)
    return (nrm / lens).astype(np.float32)


def feature_colors(pos: np.ndarray, eye_l, eye_r, mouth_c) -> np.ndarray:
    """Bake subtle feature weights into vertex color (g=feature). Keep lips mild — no bloom bake."""
    col = np.zeros((len(pos), 4), dtype=np.float32)
    col[:, 0:3] = 0.50  # base
    col[:, 3] = 1.0
    d_mouth = np.linalg.norm(pos - mouth_c, axis=1)
    lip = np.clip(1.0 - d_mouth / 0.10, 0, 1) * np.clip((pos[:, 2] - mouth_c[2] + 0.04) * 6, 0, 1)
    brow_y = eye_l[1] + 0.055
    brow = (
        np.clip(1.0 - np.abs(pos[:, 1] - brow_y) / 0.03, 0, 1)
        * np.clip(1.0 - (np.abs(pos[:, 0]) - 0.05) / 0.11, 0, 1)
        * np.clip((pos[:, 2] - 0.15) * 4, 0, 1)
    )
    feat = np.maximum(lip * 0.35, brow * 0.4)
    col[:, 1] = np.clip(0.32 + feat, 0, 0.7)
    col[:, 0] = np.clip(0.48 + lip * 0.12, 0, 0.65)
    return col


def write_bin(
    path: Path,
    pos: np.ndarray,
    nrm: np.ndarray,
    uvs: np.ndarray,
    col: np.ndarray,
    jaw: np.ndarray,
    funnel: np.ndarray,
    wide: np.ndarray,
    smile: np.ndarray,
    brow: np.ndarray,
    indices: np.ndarray,
    meta: dict,
):
    meta = dict(meta)
    meta.update(
        {
            "vertexCount": int(len(pos)),
            "indexCount": int(len(indices)),
            "has_extra_morphs": True,
            "has_expression_morphs": True,
        }
    )
    meta_bytes = json.dumps(meta, separators=(",", ":")).encode("utf-8")
    parts = [
        struct.pack("<i", len(meta_bytes)),
        meta_bytes,
        pos.astype(np.float32).tobytes(),
        nrm.astype(np.float32).tobytes(),
        uvs.astype(np.float32).tobytes(),
        col.astype(np.float32).tobytes(),
        jaw.astype(np.float32).tobytes(),
        funnel.astype(np.float32).tobytes(),
        wide.astype(np.float32).tobytes(),
        smile.astype(np.float32).tobytes(),
        brow.astype(np.float32).tobytes(),
        indices.astype(np.int32).tobytes(),
    ]
    path.write_bytes(b"".join(parts))
    print(f"wrote {path} ({path.stat().st_size} bytes, verts={len(pos)}, tris={len(indices)//3})")


def write_part_bin(path: Path, pos: np.ndarray, nrm: np.ndarray, indices: np.ndarray, meta: dict):
    """Companion mesh without morphs (hair / lashes)."""
    meta = dict(meta)
    meta.update({"vertexCount": int(len(pos)), "indexCount": int(len(indices)), "kind": meta.get("kind", "part")})
    meta_bytes = json.dumps(meta, separators=(",", ":")).encode("utf-8")
    col = np.ones((len(pos), 4), dtype=np.float32)
    col[:, 0:3] = [1.0, 0.62, 0.25]
    uvs = np.zeros((len(pos), 2), dtype=np.float32)
    parts = [
        struct.pack("<i", len(meta_bytes)),
        meta_bytes,
        pos.astype(np.float32).tobytes(),
        nrm.astype(np.float32).tobytes(),
        uvs.tobytes(),
        col.tobytes(),
        indices.astype(np.int32).tobytes(),
    ]
    path.write_bytes(b"".join(parts))
    print(f"wrote {path} ({path.stat().st_size} bytes, verts={len(pos)})")


def transform_mesh(pos, nrm, morphs, scale, offset):
    pos2 = pos * scale + offset
    nrm2 = nrm.copy()
    # uniform scale — normals unchanged direction
    morphs2 = [m * scale for m in morphs]
    return pos2, nrm2, morphs2


def main():
    src = Path("/tmp/stock-02.glb")
    if not src.exists():
        print("missing", src, file=sys.stderr)
        sys.exit(1)
    import hashlib

    sha = hashlib.sha256(src.read_bytes()).hexdigest()
    if sha != STOCK_SHA:
        print("SHA mismatch", sha, file=sys.stderr)
        sys.exit(1)

    g, blob = load_glb(src)
    _, head = find_mesh(g, "head")
    _, hair = find_mesh(g, "hair")
    _, lashes = find_mesh(g, "eyelashes")
    _, brows = find_mesh(g, "eyebrows")

    hprim = head.primitives[0]
    names = head.extras["targetNames"]

    pos = read_accessor(g, blob, hprim.attributes.POSITION)
    nrm = read_accessor(g, blob, hprim.attributes.NORMAL)
    if hprim.attributes.TEXCOORD_0 is not None:
        uvs = read_accessor(g, blob, hprim.attributes.TEXCOORD_0)[:, :2]
    else:
        uvs = np.zeros((len(pos), 2), dtype=np.float32)
    indices = read_indices(g, blob, hprim)

    def morph(name: str) -> np.ndarray:
        if name not in names:
            return np.zeros_like(pos)
        return expand_morph(g, blob, hprim, names.index(name), len(pos))

    jaw = morph("jawOpen")
    funnel = morph("mouthFunnel")
    wide = morph("mouthStretchLeft") + morph("mouthStretchRight")
    smile = morph("mouthSmileLeft") + morph("mouthSmileRight")
    brow = morph("browInnerUp") * 0.6 + morph("browOuterUpLeft") * 0.4 + morph("browOuterUpRight") * 0.4

    # Landmarks in source space
    # Approximate eyes: densest forward vertices around typical MakeHuman eye height
    # Use morph eyeBlink residual regions — or geometric heuristic
    front = pos[:, 2] > np.percentile(pos[:, 2], 70)
    upper = (pos[:, 1] > np.percentile(pos[front, 1], 55)) & (pos[:, 1] < np.percentile(pos[front, 1], 85))
    eye_band = front & upper
    left_mask = eye_band & (pos[:, 0] < -0.02)
    right_mask = eye_band & (pos[:, 0] > 0.02)
    eye_l = pos[left_mask].mean(0) if left_mask.any() else np.array([-0.03, 0.02, 0.07])
    eye_r = pos[right_mask].mean(0) if right_mask.any() else np.array([0.03, 0.02, 0.07])

    mouth_mask = (pos[:, 2] > np.percentile(pos[:, 2], 60)) & (
        pos[:, 1] < eye_l[1] - 0.04
    ) & (pos[:, 1] > pos[:, 1].min() + 0.04)
    mouth_c = pos[mouth_mask].mean(0) if mouth_mask.any() else np.array([0.0, -0.06, 0.08])

    # Scale/offset to match prior framing
    src_height = float(pos[:, 1].max() - pos[:, 1].min())
    scale = TARGET_HEAD_HEIGHT / max(src_height, 1e-4)
    # Place eyes at TARGET_EYE_Y / TARGET_EYE_Z
    mid_eye = (eye_l + eye_r) * 0.5
    offset = np.array(
        [
            -mid_eye[0] * scale,
            TARGET_EYE_Y - mid_eye[1] * scale,
            TARGET_EYE_Z - mid_eye[2] * scale,
        ],
        dtype=np.float32,
    )

    morph_list = [jaw, funnel, wide, smile, brow]
    pos, nrm, morph_list = transform_mesh(pos, nrm, morph_list, scale, offset)
    jaw, funnel, wide, smile, brow = morph_list
    eye_l = eye_l * scale + offset
    eye_r = eye_r * scale + offset
    mouth_c = mouth_c * scale + offset

    # One subdivision pass for smoother high-poly silhouette
    pos, indices, morph_list = subdivide_once(pos, indices, jaw, funnel, wide, smile, brow)
    jaw, funnel, wide, smile, brow = morph_list
    # UVs — expand with zeros for new verts
    if len(uvs) != len(pos):
        uvs = np.zeros((len(pos), 2), dtype=np.float32)

    nrm = recompute_normals(pos, indices)
    col = feature_colors(pos, eye_l, eye_r, mouth_c)

    # Flip winding if needed for FrontCounterClockwise — check cross vs outward
    # MakeHuman faces outward with CCW from outside typically; Vortice uses FrontCounterClockwise=true

    y_cut = float(pos[:, 1].min() + 0.02)
    meta = {
        "source": "argus-mask stock-02-r3 (MakeHuman/MPFB CC0)",
        "license": "CC0-1.0",
        "source_url": STOCK_URL,
        "source_sha256": STOCK_SHA,
        "eye_L": eye_l.tolist(),
        "eye_R": eye_r.tolist(),
        "mouth_center": mouth_c.tolist(),
        "lip_y": float(mouth_c[1]),
        "y_cut": y_cut,
        "pos_min": pos.min(0).tolist(),
        "pos_max": pos.max(0).tolist(),
        "scale": float(scale),
    }

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    write_bin(
        OUT_DIR / "lia_holo_mesh.bin",
        pos,
        nrm,
        uvs,
        col,
        jaw,
        funnel,
        wide,
        smile,
        brow,
        indices,
        meta,
    )

    # Hair
    hprim = hair.primitives[0]
    hpos = read_accessor(g, blob, hprim.attributes.POSITION)
    hnrm = read_accessor(g, blob, hprim.attributes.NORMAL)
    hidx = read_indices(g, blob, hprim)
    hpos = hpos * scale + offset
    hnrm = recompute_normals(hpos, hidx)
    write_part_bin(
        OUT_DIR / "lia_holo_hair.bin",
        hpos,
        hnrm,
        hidx,
        {"kind": "hair", "license": "CC0-1.0", "source": "stock-02 short02 hair"},
    )

    # Lashes only (thin lid line)
    lprim = lashes.primitives[0]
    lpos = read_accessor(g, blob, lprim.attributes.POSITION) * scale + offset
    lidx = read_indices(g, blob, lprim)
    lnrm = recompute_normals(lpos, lidx)
    write_part_bin(
        OUT_DIR / "lia_holo_lashes.bin",
        lpos,
        lnrm,
        lidx,
        {"kind": "lashes", "license": "CC0-1.0", "source": "stock-02 eyelashes"},
    )

    # Brows separate — thinner holo arches (scale Y slightly toward brow ridge)
    bprim = brows.primitives[0]
    bpos = read_accessor(g, blob, bprim.attributes.POSITION) * scale + offset
    # Flatten thickness toward skin: pull toward mean Y of each side
    mid_y = float(bpos[:, 1].mean())
    bpos[:, 1] = mid_y + (bpos[:, 1] - mid_y) * 0.55
    bidx = read_indices(g, blob, bprim)
    bnrm = recompute_normals(bpos, bidx)
    write_part_bin(
        OUT_DIR / "lia_holo_brows.bin",
        bpos,
        bnrm,
        bidx,
        {"kind": "brows", "license": "CC0-1.0", "source": "stock-02 eyebrow002"},
    )

    # Save landmarks JSON for C# sync
    (OUT_DIR / "lia_holo_meta.json").write_text(json.dumps(meta, indent=2))
    print("eye_L", eye_l)
    print("eye_R", eye_r)
    print("mouth", mouth_c)
    print("done")


if __name__ == "__main__":
    main()
