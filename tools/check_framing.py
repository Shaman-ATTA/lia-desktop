#!/usr/bin/env python3
"""Self-check smoke screenshots: head fills ~70-75% of the 3D viewport height.

Expects PNGs in CWD: desktop_winui_front.png (and optionally others).
Exits non-zero if front head height fraction is outside [0.55, 0.90].
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image


def viewport_content_frac(path: Path) -> tuple[float, float, float]:
    im = Image.open(path).convert("RGB")
    a = np.asarray(im).astype(np.float32)
    h, w, _ = a.shape
    # Approximate center 3D panel (matches WinUI chrome on 1040x736 smoke shots)
    y0, y1 = int(h * 0.10), int(h * 0.78)
    x0, x1 = int(w * 0.27), int(w * 0.73)
    vp = a[y0:y1, x0:x1]
    lum = vp.mean(axis=2)
    # Skin/hair vs near-black clear (0.015*255≈4)
    mask = lum > 22
    ys = np.where(mask.any(axis=1))[0]
    if len(ys) < 8:
        return 0.0, 0.0, 0.0
    # Trim thin noise rows at edges
    frac = (ys[-1] - ys[0] + 1) / max(1, vp.shape[0])
    top_m = ys[0] / max(1, vp.shape[0])
    bot_m = (vp.shape[0] - 1 - ys[-1]) / max(1, vp.shape[0])
    return float(frac), float(top_m), float(bot_m)


def eyes_width_frac(path: Path) -> float:
    """Rough: bright iris-ish content horizontal span in center band."""
    im = Image.open(path).convert("RGB")
    a = np.asarray(im).astype(np.float32)
    h, w, _ = a.shape
    y0, y1 = int(h * 0.28), int(h * 0.55)
    x0, x1 = int(w * 0.27), int(w * 0.73)
    vp = a[y0:y1, x0:x1]
    # Face content
    lum = vp.mean(axis=2)
    mask = lum > 30
    xs = np.where(mask.any(axis=0))[0]
    if len(xs) < 8:
        return 0.0
    return float((xs[-1] - xs[0] + 1) / max(1, vp.shape[1]))


def main() -> int:
    front = Path("desktop_winui_front.png")
    if not front.is_file():
        print("missing desktop_winui_front.png", file=sys.stderr)
        return 2
    frac, top, bot = viewport_content_frac(front)
    print(f"front head_height_frac={frac:.3f} top_margin={top:.3f} bot_margin={bot:.3f}")
    # Target 70-75%; allow 55-90% so CI isn't brittle to HUD crop estimate
    if frac < 0.55:
        print(f"FAIL: head too small ({frac:.1%}) — expected ~70-75%", file=sys.stderr)
        return 1
    if frac > 0.92:
        print(f"FAIL: head too large / cropped ({frac:.1%})", file=sys.stderr)
        return 1
    if top < 0.02:
        print(f"WARN: top margin thin ({top:.3f}) — may crop crown", file=sys.stderr)
    eyes = Path("desktop_winui_eyes.png")
    if eyes.is_file():
        ew = eyes_width_frac(eyes)
        print(f"eyes face_width_frac={ew:.3f}")
        if ew < 0.40:
            print(f"FAIL: eyes close-up too wide/far ({ew:.1%}) — want eyes ~60% width", file=sys.stderr)
            return 1
    print("framing check ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
