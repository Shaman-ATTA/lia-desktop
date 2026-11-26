#!/usr/bin/env python3
"""Self-check smoke screenshots: head fills ~70-75% of the hologram stage height.

Expects PNGs in CWD: desktop_winui_front.png (and optionally others).
Uses a center-band skin/hair mask so amber orbit rings / 2D HUD arcs do not inflate the bbox.
Exits non-zero if front head height fraction is outside [0.58, 0.90].
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image


def _find_viewport(a: np.ndarray) -> tuple[int, int, int, int]:
    """Locate the hologram stage under title/footer chrome (v0.6 full-bleed HUD)."""
    h, w, _ = a.shape
    # Title ~36px, bottom dock ~96px on 800px window; side glass overlays edges.
    if h >= 650 and w >= 900:
        # title ~36px, bottom dock ~120px on 800px window
        return int(h * 0.055), int(h * 0.85), int(w * 0.20), int(w * 0.80)
    y0, y1 = int(h * 0.08), int(h * 0.86)
    x0, x1 = int(w * 0.22), int(w * 0.78)
    return y0, y1, x0, x1


def _head_rows(vp: np.ndarray) -> np.ndarray:
    """Row indices for contiguous head+neck in the center band.

    Skin defines the vertical core (chin/neck). Hair may extend the crown upward.
    Soft amber ring / HUD glow is excluded so orbits don't inflate height.
    """
    h, w, _ = vp.shape
    x0, x1 = int(w * 0.34), int(w * 0.66)
    band = vp[:, x0:x1]
    r, g, b = band[:, :, 0], band[:, :, 1], band[:, :, 2]
    lum = (r + g + b) / 3.0
    amber = (r > 145) & (g > 95) & (b < 95) & ((r - b) > 70) & ((g - b) > 40) & (r >= g)
    skin = (r > 55) & (g > 30) & (b > 22) & (r >= g * 0.95) & (r > b * 0.85) & ((r - b) > 5) & (~amber)
    hair = (lum > 16) & (lum < 95) & (r >= g) & (g >= b * 0.8) & (r < 110) & (b < 70) & (~amber)
    skin_rows = np.where(skin.sum(axis=1) > 5)[0]
    if len(skin_rows) < 8:
        return skin_rows
    cy = int(h * 0.36)
    skin_set = set(skin_rows.tolist())
    near = int(skin_rows[np.argmin(np.abs(skin_rows - cy))])
    lo = hi = near
    while (lo - 1) in skin_set:
        lo -= 1
    while (hi + 1) in skin_set:
        hi += 1
    hair_rows = set(np.where(hair.sum(axis=1) > 5)[0].tolist())
    while (lo - 1) in hair_rows:
        lo -= 1
    return np.arange(lo, hi + 1)


def viewport_content_frac(path: Path) -> tuple[float, float, float]:
    im = Image.open(path).convert("RGB")
    a = np.asarray(im).astype(np.float32)
    y0, y1, x0, x1 = _find_viewport(a)
    vp = a[y0:y1, x0:x1]
    ys = _head_rows(vp)
    if len(ys) < 8:
        return 0.0, 0.0, 0.0
    frac = (ys[-1] - ys[0] + 1) / max(1, vp.shape[0])
    top_m = ys[0] / max(1, vp.shape[0])
    bot_m = (vp.shape[0] - 1 - ys[-1]) / max(1, vp.shape[0])
    return float(frac), float(top_m), float(bot_m)


def eyes_width_frac(path: Path) -> float:
    """Outer face/eye-band width in the close-up (target ~0.55–0.80 for eyes≈60%)."""
    im = Image.open(path).convert("RGB")
    a = np.asarray(im).astype(np.float32)
    y0, y1, x0, x1 = _find_viewport(a)
    vp = a[y0:y1, x0:x1]
    h, w, _ = vp.shape
    band = vp[int(h * 0.32) : int(h * 0.58), int(w * 0.08) : int(w * 0.92)]
    r, g, b = band[:, :, 0], band[:, :, 1], band[:, :, 2]
    lum = (r + g + b) / 3.0
    amber = (r > 140) & (g > 110) & (b < 100)
    skin = (r > 55) & (g > 30) & (b > 20) & (r >= g * 0.9) & ((r - b) > 4) & (~amber)
    xs = np.where(skin.any(axis=0))[0]
    if len(xs) < 8:
        xs = np.where(((lum > 30) & (~amber)).any(axis=0))[0]
    if len(xs) < 8:
        return 0.0
    return float((xs[-1] - xs[0] + 1) / max(1, band.shape[1]))


def amber_face_frac(path: Path, y0: float = 0.28, y1: float = 0.55) -> float:
    """Fraction of face-band pixels that look like bright amber ring tubes."""
    im = Image.open(path).convert("RGB")
    a = np.asarray(im).astype(np.float32)
    yy0, yy1, x0, x1 = _find_viewport(a)
    vp = a[yy0:yy1, x0:x1]
    h, w, _ = vp.shape
    band = vp[int(h * y0) : int(h * y1), int(w * 0.30) : int(w * 0.70)]
    if band.size == 0:
        return 0.0
    r, g, b = band[:, :, 0], band[:, :, 1], band[:, :, 2]
    amber = (r > 150) & (g > 95) & (b < 95) & ((r - b) > 70) & ((g - b) > 40) & (r >= g)
    return float(amber.mean())


def neck_black_blob_frac(path: Path) -> float:
    """Opaque black neck stump (color multiplied to 0) vs navy clear (~4,3,8)."""
    im = Image.open(path).convert("RGB")
    a = np.asarray(im).astype(np.float32)
    yy0, yy1, x0, x1 = _find_viewport(a)
    vp = a[yy0:yy1, x0:x1]
    h, w, _ = vp.shape
    band = vp[int(h * 0.72) : int(h * 0.95), int(w * 0.35) : int(w * 0.65)]
    if band.size == 0:
        return 0.0
    r, g, b = band[:, :, 0], band[:, :, 1], band[:, :, 2]
    stump = (r < 2.5) & (g < 2.5) & (b < 3.5)
    return float(stump.mean())


def _hud_arc_mask(r: np.ndarray, g: np.ndarray, b: np.ndarray) -> np.ndarray:
    """Bright amber OR cyan HUD strokes (not skin)."""
    amber = (
        (r > 180)
        & (g > 100)
        & (g < 190)
        & (b < 110)
        & ((r - b) > 90)
        & ((r - g) > 25)
        & (r >= g)
    )
    cyan = (b > 180) & (g > 150) & (r < 140) & ((b - r) > 60)
    return amber | cyan


def arc_ring_pixel_count(path: Path) -> tuple[int, int, int, int]:
    """Count HUD arc pixels framing the head, and on the face band.

    Returns (ring_count, face_count, left_n, right_n).
    Ring = amber/cyan in left+right mid-stage bands (outside head), excluding
    top chrome / bottom mic so UI accents can't fake a pass.
    """
    im = Image.open(path).convert("RGB")
    a = np.asarray(im).astype(np.float32)
    yy0, yy1, x0, x1 = _find_viewport(a)
    vp = a[yy0:yy1, x0:x1]
    h, w, _ = vp.shape
    r, g, b = vp[:, :, 0], vp[:, :, 1], vp[:, :, 2]
    arcs = _hud_arc_mask(r, g, b)

    lum = (r + g + b) / 3.0
    skin = (r > 55) & (g > 30) & (b > 22) & (r >= g * 0.95) & (r > b * 0.85) & ((r - b) > 5)
    hair = (lum > 16) & (lum < 95) & (r >= g) & (g >= b * 0.8) & (r < 110) & (b < 70)
    head = skin | hair

    yy, xx = np.mgrid[0:h, 0:w]
    aspect = w / max(1.0, float(h))
    px = ((xx + 0.5) / w - 0.5) * aspect
    py = (yy + 0.5) / h - 0.48
    rad = np.sqrt(px * px + py * py)

    # Mid-height; left/right of head but INSIDE stage (exclude far-right glass chrome)
    mid_y = (yy >= int(h * 0.20)) & (yy <= int(h * 0.75))
    left = (xx >= int(w * 0.04)) & (xx <= int(w * 0.26))
    right = (xx >= int(w * 0.74)) & (xx <= int(w * 0.90))
    ring = arcs & mid_y & (left | right) & (rad >= 0.30) & (rad <= 0.62) & (~head)
    left_n = int((arcs & mid_y & left & (rad >= 0.30) & (rad <= 0.62) & (~head)).sum())
    right_n = int((arcs & mid_y & right & (rad >= 0.30) & (rad <= 0.62) & (~head)).sum())

    # Strict face arc check — bright HUD only (skin can trip the looser mask)
    fr = r[int(h * 0.28) : int(h * 0.55), int(w * 0.30) : int(w * 0.70)]
    fg = g[int(h * 0.28) : int(h * 0.55), int(w * 0.30) : int(w * 0.70)]
    fb = b[int(h * 0.28) : int(h * 0.55), int(w * 0.30) : int(w * 0.70)]
    face = _hud_arc_mask(fr, fg, fb) & (fr > 220)  # core stroke brightness
    return int(ring.sum()), int(face.sum()), left_n, right_n


def main() -> int:
    front = Path("desktop_winui_front.png")
    if not front.is_file():
        print("missing desktop_winui_front.png", file=sys.stderr)
        return 2
    frac, top, bot = viewport_content_frac(front)
    print(f"front head_height_frac={frac:.3f} top_margin={top:.3f} bot_margin={bot:.3f}")
    if frac < 0.58:
        print(f"FAIL: head too small ({frac:.1%}) — expected ~70-75%", file=sys.stderr)
        return 1
    if frac > 0.90:
        print(f"FAIL: head too large / cropped ({frac:.1%})", file=sys.stderr)
        return 1
    if top < 0.02:
        print(f"WARN: top margin thin ({top:.3f}) — may crop crown", file=sys.stderr)

    for name in (
        "desktop_winui_threequarter.png",
        "desktop_winui_speaking.png",
        "desktop_winui_profile.png",
    ):
        p = Path(name)
        if not p.is_file():
            continue
        f, t, b = viewport_content_frac(p)
        print(f"{name}: head_height_frac={f:.3f} top={t:.3f} bot={b:.3f}")
        if f < 0.52:
            print(f"FAIL: {name} head too small ({f:.1%})", file=sys.stderr)
            return 1
        if f > 0.88:
            print(f"FAIL: {name} head too large / cropped ({f:.1%})", file=sys.stderr)
            return 1

    eyes = Path("desktop_winui_eyes.png")
    if eyes.is_file():
        ew = eyes_width_frac(eyes)
        print(f"eyes face_width_frac={ew:.3f}")
        if ew < 0.50:
            print(f"FAIL: eyes close-up too far ({ew:.1%}) — want eyes ~60% width", file=sys.stderr)
            return 1
        er = amber_face_frac(eyes, y0=0.30, y1=0.62)
        print(f"eyes amber_on_face_frac={er:.4f}")
        if er > 0.012:
            print(f"FAIL: rings visible in eyes close-up ({er:.2%})", file=sys.stderr)
            return 1

    for name in ("desktop_winui_front.png", "desktop_winui_speaking.png", "desktop_winui_threequarter.png"):
        p = Path(name)
        if not p.is_file():
            continue
        af = amber_face_frac(p, y0=0.28, y1=0.55)
        print(f"{name}: amber_on_face_frac={af:.4f}")
        # v0.6.1: zero HUD/ring pixels on skin — keep tight
        if af > 0.020:
            print(f"FAIL: {name} rings/HUD on face ({af:.2%})", file=sys.stderr)
            return 1
        nb = neck_black_blob_frac(p)
        print(f"{name}: neck_black_blob_frac={nb:.4f}")
        if nb > 0.08:
            print(f"FAIL: {name} opaque black neck silhouette ({nb:.2%})", file=sys.stderr)
            return 1

        ring_n, face_n, left_n, right_n = arc_ring_pixel_count(p)
        print(
            f"{name}: arc_ring_pixels={ring_n} (L={left_n} R={right_n}) "
            f"arc_on_face_pixels={face_n}"
        )
        # Dense arcs must frame the head on BOTH sides (not silently culled/cleared).
        if ring_n < 300 or left_n < 80 or right_n < 80:
            print(
                f"FAIL: {name} arc ring too sparse "
                f"(ring={ring_n} L={left_n} R={right_n}) — arcs missing around head",
                file=sys.stderr,
            )
            return 1
        if face_n > 80:
            print(
                f"FAIL: {name} arc pixels on face ({face_n} > 80)",
                file=sys.stderr,
            )
            return 1

    print("framing check ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
