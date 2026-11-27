#!/usr/bin/env python3
"""Print golden Sec-MS-GEC v2 tokens from edge-tts for C# port verification."""

from __future__ import annotations

import json
import sys
import time

from edge_tts.drm import generate_sec_ms_gec_v2


def main() -> int:
    timestamps = [
        1732650000.0,
        1700000000.0,
        1730000000.0,
        1600000000.0,
        time.time(),
    ]
    if len(sys.argv) > 1:
        timestamps = [float(x) for x in sys.argv[1:]]

    rows = []
    for ts in timestamps:
        token = generate_sec_ms_gec_v2(ts)
        rows.append({"unix": ts, "token": token})
        print(f"{ts:.6f}\t{token}")

    print("---json---")
    print(json.dumps(rows))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
