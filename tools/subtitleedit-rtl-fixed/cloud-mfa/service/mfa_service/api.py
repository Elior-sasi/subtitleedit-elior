"""HTTP API (stdlib only). TLS is terminated by Cloud Run. Every /v1 route requires
a bearer token; jobs are scoped to the token's user. Request bodies are bounded
and never logged."""
from __future__ import annotations

import hashlib
import json
import logging
import os
import re
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from .config import Config
from .jobs import ApiError, JobService
from .storage import FileStorage, GcsStorage

log = logging.getLogger("subs_il_mfa")
_USER = re.compile(r"^[a-z0-9_-]{1,32}$")
_ROUTE = re.compile(r"^/v1/jobs/([A-Za-z0-9_-]{32})(?:/(audio/chunks/(\d{1,5})|audio/complete|cancel|result))?$")
MAX_JSON = 4 * 1024 * 1024


class Tokens:
    def __init__(self, path: str):
        self.path, self._mtime, self._map = path, None, {}

    def user(self, header: str | None) -> str:
        if not header or not header.startswith("Bearer "):
            raise ApiError(401, "unauthorized", "Missing bearer token.")
        token = header[7:].strip()
        if not 32 <= len(token) <= 200:
            raise ApiError(401, "unauthorized", "Invalid token.")
        self._reload()
        user = self._map.get(hashlib.sha256(token.encode()).hexdigest())
        if not user:
            raise ApiError(401, "unauthorized", "Invalid token.")
        return user

    def _reload(self):
        if not self.path:
            return
        mtime = os.stat(self.path).st_mtime
        if mtime != self._mtime:
            data = json.loads(Path(self.path).read_text(encoding="utf-8"))
            self._map = {h: u for h, u in data.items() if re.fullmatch(r"[0-9a-f]{64}", h) and _USER.match(u)}
            self._mtime = mtime


class App:
    def __init__(self, cfg: Config, service: JobService, tokens: Tokens, engine_version: str):
        self.cfg, self.service, self.tokens, self.engine_version = cfg, service, tokens, engine_version
        self.slots = threading.BoundedSemaphore(32)


def make_handler(app: App):
    class Handler(BaseHTTPRequestHandler):
        server_version = "subs-il-mfa"
        sys_version = ""
        protocol_version = "HTTP/1.1"
        timeout = 120

        def log_message(self, *args):  # default logger prints request lines; keep logs content-free
            pass

        def _send(self, status: int, obj) -> None:
            data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.end_headers()
            self.wfile.write(data)

        def _body(self, limit: int) -> bytes:
            if self.headers.get("Transfer-Encoding"):
                raise ApiError(411, "length_required", "Chunked transfer encoding is not accepted.")
            try:
                length = int(self.headers.get("Content-Length", "-1"))
            except ValueError:
                length = -1
            if length < 0:
                raise ApiError(411, "length_required", "Content-Length required.")
            if length > limit:
                raise ApiError(413, "too_large", "Request body too large.")
            data = self.rfile.read(length)
            if len(data) != length:
                raise ApiError(400, "incomplete_body", "Request body incomplete.")
            return data

        def _json(self) -> dict:
            try:
                obj = json.loads(self._body(MAX_JSON).decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                raise ApiError(400, "bad_json", "Body must be UTF-8 JSON.")
            if not isinstance(obj, dict):
                raise ApiError(400, "bad_json", "Body must be a JSON object.")
            return obj

        def _dispatch(self, method: str) -> None:
            if not app.slots.acquire(timeout=5):
                return self._send(503, {"error": {"code": "busy", "message": "Server busy."}})
            try:
                status, obj = self._route(method)
                self._send(status, obj)
            except ApiError as e:
                self.close_connection = e.status in (400, 411, 413)
                self._send(e.status, {"error": {"code": e.code, "message": e.message}})
            except Exception:
                log.exception("request failed")  # stack traces contain no request bodies
                self._send(500, {"error": {"code": "internal", "message": "Unexpected server error."}})
            finally:
                app.slots.release()

        def _route(self, method: str):
            path = self.path.split("?", 1)[0]
            if path == "/healthz" and method == "GET":
                models = [m["id"] for m in app.cfg.models if m.get("available")]
                return 200, {"ok": True, "engine": "Montreal Forced Aligner", "engine_version": app.engine_version,
                             "available_languages": models, "workers": len(app.service.workers),
                             "queue": app.service.queue.qsize()}
            user = app.tokens.user(self.headers.get("Authorization"))
            if path == "/v1/languages" and method == "GET":
                return 200, {"languages": app.service.languages(), "service": app.cfg.service_name,
                             "retention_hours": app.cfg.retention_hours, "max_audio_seconds": app.cfg.max_audio_seconds,
                             "max_chunk_bytes": app.cfg.max_chunk_bytes}
            if path == "/v1/jobs" and method == "POST":
                job, created = app.service.create(user, self._json())
                return (201 if created else 200), job
            m = _ROUTE.match(path)
            if not m:
                raise ApiError(404, "not_found", "Unknown route.")
            job_id, sub, chunk = m.group(1), m.group(2), m.group(3)
            if sub is None and method == "GET":
                return 200, app.service.status(user, job_id)
            if sub is None and method == "DELETE":
                app.service.delete(user, job_id)
                return 200, {"deleted": True}
            if chunk is not None and method == "PUT":
                data = self._body(app.cfg.max_chunk_bytes)
                return 200, app.service.upload_chunk(user, job_id, int(chunk), data, self.headers.get("X-Chunk-SHA256", "").lower())
            if sub == "audio/complete" and method == "POST":
                return 200, app.service.complete_upload(user, job_id, self._json().get("chunk_count"))
            if sub == "cancel" and method == "POST":
                return 200, app.service.cancel(user, job_id)
            if sub == "result" and method == "GET":
                return 200, app.service.result(user, job_id)
            raise ApiError(405, "method_not_allowed", "Method not allowed.")

        def do_GET(self):
            self._dispatch("GET")

        def do_POST(self):
            self._dispatch("POST")

        def do_PUT(self):
            self._dispatch("PUT")

        def do_DELETE(self):
            self._dispatch("DELETE")

    return Handler


def build(cfg: Config | None = None, engine_version: str | None = None) -> tuple[App, ThreadingHTTPServer]:
    cfg = cfg or Config.from_env()
    store = GcsStorage(cfg.bucket) if cfg.storage == "gcs" else FileStorage(cfg.storage_root)
    service = JobService(cfg, store)
    if engine_version is None:
        try:
            import importlib.metadata as md
            engine_version = md.version("montreal-forced-aligner")
        except Exception:
            engine_version = next((m["engine_version"] for m in cfg.models if m.get("available")), "unknown")
    app = App(cfg, service, Tokens(cfg.tokens_file), engine_version)
    host = os.environ.get("MFA_HOST", "0.0.0.0")
    port = int(os.environ.get("PORT", "8080"))
    server = ThreadingHTTPServer((host, port), make_handler(app))
    server.daemon_threads = True
    return app, server


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    app, server = build()
    app.service.start()
    log.info(json.dumps({"event": "started", "engine_version": app.engine_version,
                         "languages": [m["id"] for m in app.cfg.models if m.get("available")]}))
    server.serve_forever()


if __name__ == "__main__":
    main()

