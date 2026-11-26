#!/usr/bin/env python3
"""Local Gemini v1beta mock for Lia Desktop CI selftest.

Endpoints:
  GET  /v1beta/models
  POST /v1beta/models/{id}:generateContent
  POST /_control/scenario/{name}   — reset counters + set scenario
  GET  /_control/health

Scenarios:
  normal       — 200 with Russian reply
  overload503  — 503 once, then 200 (matches client: retry once)
  retry429     — 429 + Retry-After: 1, then 200
  slow20       — sleep 20s, then 200
  badmodel     — first generateContent → 404 + suggested model; then 200
"""
from __future__ import annotations

import json
import re
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 18765
STATE = {"scenario": "normal", "hits": 0}
LOCK = threading.Lock()

MODELS = {
    "models": [
        {
            "name": "models/gemini-flash-lite-latest",
            "displayName": "Gemini Flash Lite Latest",
            "supportedGenerationMethods": ["generateContent"],
        },
        {
            "name": "models/gemini-3.1-flash-lite",
            "displayName": "Gemini 3.1 Flash Lite",
            "supportedGenerationMethods": ["generateContent"],
        },
        {
            "name": "models/gemini-3.5-flash-lite",
            "displayName": "Gemini 3.5 Flash Lite",
            "supportedGenerationMethods": ["generateContent"],
        },
        {
            "name": "models/gemini-3.8-flash",
            "displayName": "Gemini 3.8 Flash",
            "supportedGenerationMethods": ["generateContent"],
        },
        {
            "name": "models/gemini-3.7-flash",
            "displayName": "Gemini 3.7 Flash",
            "supportedGenerationMethods": ["generateContent"],
        },
        {
            "name": "models/embedding-001",
            "supportedGenerationMethods": ["embedContent"],
        },
    ]
}

OK_REPLY = {
    "candidates": [
        {
            "content": {
                "role": "model",
                "parts": [{"text": "Привет! Я Лиа, на связи. Чем помочь?"}],
            }
        }
    ]
}


def set_scenario(name: str) -> None:
    with LOCK:
        STATE["scenario"] = name
        STATE["hits"] = 0


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):  # quieter CI logs
        sys.stderr.write("[mock-gemini] " + (fmt % args) + "\n")

    def _json(self, code: int, obj, extra_headers: dict | None = None):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        if extra_headers:
            for k, v in extra_headers.items():
                self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def _err(self, code: int, message: str, extra_headers: dict | None = None):
        self._json(code, {"error": {"code": code, "message": message, "status": "ERROR"}}, extra_headers)

    def do_GET(self):
        path = urlparse(self.path).path
        if path in ("/_control/health", "/health"):
            self._json(200, {"ok": True, **STATE})
            return
        if path.endswith("/models") or path.endswith("/v1beta/models"):
            self._json(200, MODELS)
            return
        self._err(404, f"no GET {path}")

    def do_POST(self):
        parsed = urlparse(self.path)
        path = parsed.path
        length = int(self.headers.get("Content-Length", "0") or 0)
        _ = self.rfile.read(length) if length else b""

        m = re.match(r"^/_control/scenario/([\w\-]+)$", path)
        if m:
            set_scenario(m.group(1))
            self._json(200, {"scenario": STATE["scenario"], "hits": STATE["hits"]})
            return

        # /v1beta/models/{id}:generateContent  (id may be URL-encoded)
        gen = re.search(r"/models/([^/:]+):generateContent", path)
        if not gen:
            self._err(404, f"no POST {path}")
            return

        model = gen.group(1)
        # Phantom / retired names → 404 with suggestion (ListModels fallback path)
        # Hard-404 only for names outside the preferred mock catalogue
        if re.search(r"gemini-bad|not-a-model|2\.0-flash|2\.5-flash$", model, re.I):
            self._err(
                404,
                f"This model models/{model} is no longer available. "
                f"Please update your code to use models/gemini-flash-lite-latest",
            )
            return

        with LOCK:
            scenario = STATE["scenario"]
            STATE["hits"] += 1
            hit = STATE["hits"]

        if scenario == "overload503":
            if hit == 1:
                self._err(503, "The model is overloaded. Please try again later.")
                return
        elif scenario == "retry429":
            if hit == 1:
                self._err(
                    429,
                    "Resource exhausted. Please try again later.",
                    {"Retry-After": "1"},
                )
                return
        elif scenario == "slow20":
            time.sleep(20)
        elif scenario == "badmodel":
            if hit == 1:
                self._err(
                    404,
                    f"This model models/{model} is no longer available. "
                    f"Please update your code to use models/gemini-flash-lite-latest",
                )
                return

        # normal / after retries / slow20 / badmodel fallback complete
        self._json(200, OK_REPLY)


def main() -> None:
    httpd = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"mock-gemini listening on 127.0.0.1:{PORT}", flush=True)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
