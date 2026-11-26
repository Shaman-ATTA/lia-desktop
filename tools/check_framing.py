#!/usr/bin/env python3
"""Self-check smoke screenshots: head fills ~70-75% of the 3D viewport height.

Expects PNGs in CWD: desktop_winui_front.png (and optionally others).
Uses a center-band skin/hair mask so amber orbit rings do not inflate the bbox.
Exits non-zero if front head height fraction is outside [0.58, 0.85].
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image


def _find_viewport(a: np.ndarray) -> tuple[int, int, int, int]:
    """Locate the dark 3D panel by scanning for a large near-black region."""
    h, w, _ = a.shape
    lum = a.mean(axis=2)
    # Prefer fixed crop tuned to 1040x736 smoke chrome; fall back if odd size
    if 700 <= h <= 800 and 1000 <= w <= 1100:
        return 78, min(h - 160, 575), int(w * 0.29), int(w * 0.71)
    y0, y1 = int(h * 0.10), int(h * 0.78)
    x0, x1 = int(w * 0.28), int(w * 0.72)
    return y0, y1, x0, x1


def _head_rows(vp: np.ndarray) -> np.ndarray:
    """Row indices belonging to the contiguous head+neck mass in the center band."""
    h, w, _ = vp.shape
    x0, x1 = int(w * 0.32), int(w * 0.68)
    band = vp[:, x0:x1]
    r, g, b = band[:, :, 0], band[:, :, 1], band[:, :, 2]
    lum = (r + g + b) / 3.0
    amber = (r > 130) & (g > 95) & (b < 100) & ((r + g) > (2.0 * b + 60))
    skin = (r > 50) & (g > 28) & (b > 18) & (r >= g * 0.95) & (r > b * 0.85) & ((r - b) > 4)
    hair = (lum > 14) & (lum < 105) & (r >= g) & (g >= b * 0.75) & (r < 125) & (b < 80)
    mask = (skin | hair) & (~amber) & (lum > 14)
    row_counts = mask.sum(axis=1)
    ys = np.where(row_counts > 6)[0]
    if len(ys) < 8:
        return ys
    # Contiguous run through the face center (ignore stray ring/noise rows)
    cy = int(h * 0.38)
    ys_set = set(ys.tolist())
    near = int(ys[np.argmin(np.abs(ys - cy))])
    lo = hi = near
    while (lo - 1) in ys_set:
        lo -= 1
    while (hi + 1) in ys_set:
        hi += 1
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
    # Prefer skin in the eye strip; ignore amber ring slash
    skin = (r > 55) & (g > 30) & (b > 20) & (r >= g * 0.9) & ((r - b) > 4) & (~amber)
    xs = np.where(skin.any(axis=0))[0]
    if len(xs) < 8:
        xs = np.where(((lum > 30) & (~amber)).any(axis=0))[0]
    if len(xs) < 8:
        return 0.0
    return float((xs[-1] - xs[0] + 1) / max(1, band.shape[1]))


def main() -> int:
    front = Path("desktop_winui_front.png")
    if not front.is_file():
        print("missing desktop_winui_front.png", file=sys.stderr)
        return 2
    frac, top, bot = viewport_content_frac(front)
    print(f"front head_height_frac={frac:.3f} top_margin={top:.3f} bot_margin={bot:.3f}")
    # Target 70-75%; allow 58-85% so CI isn't brittle to HUD crop / hair detection
    if frac < 0.58:
        print(f"FAIL: head too small ({frac:.1%}) — expected ~70-75%", file=sys.stderr)
        return 1
    if frac > 0.85:
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
        # Face band is wider than iris-to-iris (~60%); require a real close-up, not a bust shot
        if ew < 0.50:
            print(f"FAIL: eyes close-up too far ({ew:.1%}) — want eyes ~60% width", file=sys.stderr)
            return 1
    print("framing check ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
