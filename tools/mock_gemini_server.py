#!/usr/bin/env python3
"""Local Gemini v1beta mock for Lia Desktop CI selftest.

Endpoints:
  GET  /v1beta/models
  POST /v1beta/models/{id}:generateContent
  POST /_control/scenario/{name}   — reset counters + set scenario
  GET  /_control/health

Scenarios:
  normal       — 200 with Russian reply (memory-aware)
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
from urllib.parse import urlparse

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 18765
STATE = {"scenario": "normal", "hits": 0, "stt_text": "какая сейчас погода", "last_user": ""}
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


def set_scenario(name: str) -> None:
    with LOCK:
        STATE["scenario"] = name
        STATE["hits"] = 0


def extract_texts(body: dict) -> list[str]:
    out: list[str] = []
    for c in body.get("contents") or []:
        for p in c.get("parts") or []:
            t = p.get("text")
            if isinstance(t, str) and t.strip():
                out.append(t)
            fr = p.get("functionResponse") or {}
            resp = fr.get("response") or {}
            content = resp.get("content")
            if isinstance(content, str) and content.strip():
                out.append(content)
    return out


def has_inline_image(body: dict) -> bool:
    for c in body.get("contents") or []:
        for p in c.get("parts") or []:
            idata = p.get("inline_data") or p.get("inlineData") or {}
            mime = (idata.get("mime_type") or idata.get("mimeType") or "").lower()
            if idata and mime.startswith("image/"):
                return True
    return False


def has_inline_audio(body: dict) -> bool:
    for c in body.get("contents") or []:
        for p in c.get("parts") or []:
            idata = p.get("inline_data") or p.get("inlineData") or {}
            mime = (idata.get("mime_type") or idata.get("mimeType") or "").lower()
            if idata and (mime.startswith("audio/") or "wav" in mime):
                return True
    return False


def is_stt_request(body: dict) -> bool:
    blob = " ".join(extract_texts(body)).lower()
    return has_inline_audio(body) and ("распознай" in blob or "транскриб" in blob)


def last_user_text(body: dict) -> str:
    texts = []
    for c in body.get("contents") or []:
        if (c.get("role") or "") != "user":
            continue
        for p in c.get("parts") or []:
            t = p.get("text")
            if isinstance(t, str) and t.strip() and not t.startswith("Изображение экрана"):
                texts.append(t)
    return texts[-1] if texts else ""


def smart_reply(body: dict) -> dict:
    """Memory + vision-aware reply for normal scenario."""
    all_text = " ".join(extract_texts(body))
    last = last_user_text(body)
    low = last.lower()
    blob = all_text.lower()

    # STT / TranscribeRuAsync — return controlled transcription text
    if is_stt_request(body):
        with LOCK:
            text = STATE.get("stt_text") or "какая сейчас погода"
            STATE["last_user"] = f"[stt]{text}"
        return {
            "candidates": [
                {"content": {"role": "model", "parts": [{"text": text}]}}
            ]
        }

    with LOCK:
        if last.strip():
            STATE["last_user"] = last

    # Vision: ask to look → functionCall first; after image → read marker
    vision_ask = any(
        k in low
        for k in (
            "что на экране",
            "посмотри",
            "что написано",
            "написано на экране",
        )
    )
    if vision_ask and not has_inline_image(body):
        return {
            "candidates": [
                {
                    "content": {
                        "role": "model",
                        "parts": [
                            {
                                "functionCall": {
                                    "name": "look_at_screen",
                                    "args": {},
                                }
                            }
                        ],
                    }
                }
            ]
        }
    if vision_ask and has_inline_image(body):
        # Live OCR not available in mock — if marker leaked via prior text, echo it;
        # otherwise a generic screen description (vision_live uses real Gemini).
        marker = "7429" if "7429" in all_text else "экран"
        return {
            "candidates": [
                {
                    "content": {
                        "role": "model",
                        "parts": [
                            {
                                "text": f"На экране вижу тест: ЛИА ТЕСТ {marker}."
                                if marker == "7429"
                                else "На экране вижу рабочий стол."
                            }
                        ],
                    }
                }
            ]
        }

    # Memory recall
    if ("как меня зовут" in low) or ("какое число" in low) or (
        "зовут" in low and "число" in low
    ):
        if "слава" in blob and "58" in blob and "[прервано" not in low:
            # After clear, history should not contain both — but also check turn count
            # Prefer: if user asked to forget earlier and no remember after
            forgot = "забудь" in blob or "новый разговор" in blob or "память очищена" in blob
            # Find last remember vs forget
            remember_idx = max(blob.rfind("меня зовут"), blob.rfind("запомни"))
            forget_idx = max(blob.rfind("забудь"), blob.rfind("новый разговор"))
            if forget_idx > remember_idx >= 0:
                text = "Не помню — память очищена, числа нет."
            elif "слава" in blob and "58" in all_text:
                text = "Тебя зовут Слава, число 58."
            else:
                text = "Не помню имени и числа."
        else:
            text = "Не помню — память пуста."
        return {
            "candidates": [
                {"content": {"role": "model", "parts": [{"text": text}]}}
            ]
        }

    if "меня зовут" in low or "запомни" in low:
        return {
            "candidates": [
                {
                    "content": {
                        "role": "model",
                        "parts": [{"text": "Запомнила, Слава, число 58."}],
                    }
                }
            ]
        }

    # Weather / wake_e2e follow-up
    if "погод" in low:
        return {
            "candidates": [
                {
                    "content": {
                        "role": "model",
                        "parts": [{"text": "Сейчас около плюс пяти, без осадков."}],
                    }
                }
            ]
        }

    return {
        "candidates": [
            {
                "content": {
                    "role": "model",
                    "parts": [{"text": "Привет! Я Лиа, на связи. Чем помочь?"}],
                }
            }
        ]
    }


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
        raw = self.rfile.read(length) if length else b""
        body: dict = {}
        if raw:
            try:
                body = json.loads(raw.decode("utf-8", errors="replace"))
            except Exception:
                body = {}

        m = re.match(r"^/_control/scenario/([\w\-]+)$", path)
        if m:
            set_scenario(m.group(1))
            self._json(200, {"scenario": STATE["scenario"], "hits": STATE["hits"]})
            return

        if path in ("/_control/stt_text", "/_control/stt"):
            text = ""
            if isinstance(body, dict):
                text = str(body.get("text") or body.get("stt_text") or "").strip()
            if not text and raw:
                try:
                    text = raw.decode("utf-8", errors="replace").strip()
                except Exception:
                    text = ""
            with LOCK:
                STATE["stt_text"] = text or "какая сейчас погода"
            self._json(200, {"stt_text": STATE["stt_text"]})
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
        self._json(200, smart_reply(body))


def main() -> None:
    httpd = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"mock-gemini listening on 127.0.0.1:{PORT}", flush=True)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
