"""Minimal reference client (tests and operator smoke checks). Mirrors the desktop flow."""
import hashlib
import http.client
import json
import time
import urllib.parse
import uuid


class Client:
    def __init__(self, base: str, token: str, chunk: int = 8 * 1024 * 1024):
        u = urllib.parse.urlsplit(base)
        self.https, self.host, self.port = u.scheme == "https", u.hostname, u.port
        self.token, self.chunk = token, chunk

    def req(self, method, path, body=None, headers=None, raw=False):
        conn = (http.client.HTTPSConnection if self.https else http.client.HTTPConnection)(self.host, self.port, timeout=60)
        h = {"Authorization": "Bearer " + self.token} if self.token else {}
        if isinstance(body, (dict, list)):
            body = json.dumps(body).encode()
            h["Content-Type"] = "application/json"
        h.update(headers or {})
        try:
            conn.request(method, path, body=body, headers=h)
        except (BrokenPipeError, ConnectionResetError):
            pass  # server rejected an oversized body early; its response is still readable
        r = conn.getresponse()
        data = r.read()
        conn.close()
        return r.status, (data if raw else json.loads(data or b"{}"))

    def create(self, language, cues, wav: bytes, request_id=None):
        return self.req("POST", "/v1/jobs", {"client_request_id": request_id or str(uuid.uuid4()), "language": language,
                                              "cues": cues, "audio_sha256": hashlib.sha256(wav).hexdigest(),
                                              "audio_bytes": len(wav)})

    def upload(self, job_id, wav: bytes, skip=()):
        n = 0
        for n, i in enumerate(range(0, len(wav), self.chunk)):
            if n in skip:
                continue
            part = wav[i:i + self.chunk]
            st, body = self.req("PUT", f"/v1/jobs/{job_id}/audio/chunks/{n}", part,
                                {"X-Chunk-SHA256": hashlib.sha256(part).hexdigest(), "Content-Type": "application/octet-stream"})
            if st != 200:
                return st, body
        return self.req("POST", f"/v1/jobs/{job_id}/audio/complete", {"chunk_count": (len(wav) + self.chunk - 1) // self.chunk})

    def wait(self, job_id, timeout=600):
        end = time.time() + timeout
        while time.time() < end:
            st, body = self.req("GET", f"/v1/jobs/{job_id}")
            if st != 200 or body["status"] in ("succeeded", "failed", "cancelled"):
                return st, body
            time.sleep(0.3)
        raise TimeoutError(job_id)

