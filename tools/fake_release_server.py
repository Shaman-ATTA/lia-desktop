#!/usr/bin/env python3
"""Minimal GitHub-Releases-shaped HTTP server for update_* selftest gates.

Usage:
  python tools/fake_release_server.py <port> --tag v0.7.7 --setup path/to/setup.exe [--sha256 HEX]
       [--bad-sha] [--offline-hang]

Serves:
  GET /releases/latest          → JSON like api.github.com/.../releases/latest
  GET /assets/<name>            → file bytes
  GET /SHA256SUMS               → checksums
  GET /_control/health          → ok
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("port", type=int)
    ap.add_argument("--tag", required=True)
    ap.add_argument("--setup", required=True, type=Path)
    ap.add_argument("--sha256", default="")
    ap.add_argument("--bad-sha", action="store_true", help="publish wrong checksum")
    ap.add_argument("--name", default="", help="override setup asset name")
    args = ap.parse_args()

    setup = args.setup.resolve()
    if not setup.is_file():
        print(f"setup missing: {setup}", file=sys.stderr)
        return 2

    setup_name = args.name or setup.name
    real = sha256_file(setup)
    published = ("0" * 64) if args.bad_sha else (args.sha256.strip().lower() or real)
    sums = f"{published}  {setup_name}\n"
    host = "127.0.0.1"
    base = f"http://{host}:{args.port}"

    payload = {
        "tag_name": args.tag if args.tag.startswith("v") else f"v{args.tag}",
        "html_url": f"{base}/release",
        "assets": [
            {
                "name": setup_name,
                "browser_download_url": f"{base}/assets/{setup_name}",
            },
            {
                "name": "SHA256SUMS",
                "browser_download_url": f"{base}/SHA256SUMS",
            },
        ],
    }

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt: str, *a) -> None:
            sys.stderr.write("%s - %s\n" % (self.address_string(), fmt % a))

        def _send(self, code: int, body: bytes, ctype: str) -> None:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self) -> None:  # noqa: N802
            path = unquote(self.path.split("?", 1)[0])
            if path in ("/_control/health", "/health"):
                self._send(200, b"ok", "text/plain")
                return
            if path in ("/releases/latest", "/repos/Shaman-ATTA/lia-desktop/releases/latest"):
                self._send(200, json.dumps(payload).encode("utf-8"), "application/json")
                return
            if path == "/SHA256SUMS":
                self._send(200, sums.encode("utf-8"), "text/plain; charset=utf-8")
                return
            if path.startswith("/assets/"):
                name = path[len("/assets/") :]
                if name != setup_name:
                    self._send(404, b"missing", "text/plain")
                    return
                data = setup.read_bytes()
                self._send(200, data, "application/octet-stream")
                return
            self._send(404, b"not found", "text/plain")

    httpd = ThreadingHTTPServer((host, args.port), Handler)
    print(f"fake_release listening on {base} tag={payload['tag_name']} setup={setup_name} sha={published[:12]}…", flush=True)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
