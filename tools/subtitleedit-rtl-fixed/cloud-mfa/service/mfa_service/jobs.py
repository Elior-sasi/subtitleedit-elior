"""Job lifecycle: create (idempotent), chunked upload, queue with bounded
concurrency, cancellation, results and retention. Designed for a single service
instance (Cloud Run max-instances=1); state lives in private storage so a
reconnecting client resumes the same job instead of creating a new one."""
from __future__ import annotations

import hashlib
import json
import logging
import queue
import re
import secrets
import shutil
import tempfile
import threading
import time
import wave
from pathlib import Path

from . import aligner, textmap

log = logging.getLogger("subs_il_mfa")
_JOB_ID = re.compile(r"^[A-Za-z0-9_-]{32}$")
_REQ_ID = re.compile(r"^[0-9a-fA-F-]{36}$")
_SHA = re.compile(r"^[0-9a-f]{64}$")
ACTIVE = ("awaiting_upload", "queued", "running")
FINAL = ("succeeded", "failed", "cancelled")


class ApiError(Exception):
    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status, self.code, self.message = status, code, message


def cues_digest(cues: list[dict]) -> str:
    canon = json.dumps([[c["id"], c["text"]] for c in cues], ensure_ascii=False, separators=(",", ":"))
    return hashlib.sha256(canon.encode("utf-8")).hexdigest()


class JobService:
    def __init__(self, cfg, storage, clock=time.time):
        self.cfg, self.store, self.clock = cfg, storage, clock
        self.lock = threading.RLock()
        self.queue: queue.Queue[tuple[str, str]] = queue.Queue()
        self.cancel_flags: dict[str, threading.Event] = {}
        self.models = {m["id"]: m for m in cfg.models}
        self.workers: list[threading.Thread] = []
        Path(cfg.work_root).mkdir(parents=True, exist_ok=True, mode=0o700)

    # ---------- keys ----------
    @staticmethod
    def _base(user: str, job_id: str) -> str:
        return f"users/{user}/jobs/{job_id}/"

    def _job(self, user: str, job_id: str) -> dict:
        if not _JOB_ID.match(job_id or ""):
            raise ApiError(404, "not_found", "Job not found.")
        job = self.store.get_json(self._base(user, job_id) + "job.json")
        if job is None or job.get("user") != user:
            raise ApiError(404, "not_found", "Job not found or already deleted.")
        if job["expires_at"] <= self.clock():
            self._purge(user, job_id)
            raise ApiError(404, "not_found", "Job expired and was deleted.")
        return job

    def _save(self, job: dict) -> None:
        job["updated_at"] = self.clock()
        self.store.put_json(self._base(job["user"], job["job_id"]) + "job.json", job)

    # ---------- public API ----------
    def languages(self) -> list[dict]:
        return [{k: m.get(k) for k in ("id", "label", "available", "verified", "reason", "engine_version")}
                for m in self.cfg.models]

    def create(self, user: str, body: dict) -> tuple[dict, bool]:
        req = str(body.get("client_request_id", ""))
        if not _REQ_ID.match(req):
            raise ApiError(400, "bad_request", "client_request_id must be a UUID.")
        model = self.models.get(str(body.get("language", "")))
        if not model or not model.get("available"):
            raise ApiError(422, "language_unavailable", (model or {}).get("reason") or "Language is not available on this server.")
        cues = self._validate_cues(body.get("cues"))
        sha, size = str(body.get("audio_sha256", "")), body.get("audio_bytes")
        if not _SHA.match(sha) or not isinstance(size, int) or not 44 < size <= self.cfg.max_audio_bytes:
            raise ApiError(413 if isinstance(size, int) and size > self.cfg.max_audio_bytes else 400,
                           "bad_audio_declaration", f"Audio must be WAV 16 kHz mono PCM up to {self.cfg.max_audio_seconds} seconds.")
        digest = cues_digest(cues)
        fingerprint = hashlib.sha256(f"{model['id']}|{digest}|{sha}|{size}".encode()).hexdigest()
        with self.lock:
            index_key = f"users/{user}/requests/{req.lower()}.json"
            existing = self.store.get_json(index_key)
            if existing:
                try:
                    job = self._job(user, existing["job_id"])
                except ApiError:
                    job = None
                if job is not None:
                    if existing["fingerprint"] != fingerprint:
                        raise ApiError(409, "request_id_reused", "This request id was already used for different input.")
                    return self._public(job), False
            active = [j for j in self._user_jobs(user) if j["status"] in ACTIVE]
            if len(active) >= self.cfg.max_active_per_user:
                raise ApiError(429, "too_many_active_jobs", "Too many active jobs for this user. Wait or cancel one.")
            if self.queue.qsize() >= self.cfg.max_queued_jobs:
                raise ApiError(429, "queue_full", "The alignment queue is full. Try again later.")
            now = self.clock()
            job_id = secrets.token_urlsafe(24)  # 192 random bits, 32 chars
            job = {"job_id": job_id, "user": user, "status": "awaiting_upload", "stage": None,
                   "language": model["id"], "cues_sha256": digest, "audio_sha256": sha, "audio_bytes": size,
                   "cue_count": len(cues), "created_at": now, "upload_deadline": now + self.cfg.upload_window_s,
                   "expires_at": now + self.cfg.retention_hours * 3600, "error": None, "chunks": {}}
            base = self._base(user, job_id)
            self.store.put_json(base + "cues.json", cues)
            self._save(job)
            self.store.put_json(index_key, {"job_id": job_id, "fingerprint": fingerprint})
        log.info(json.dumps({"event": "job_created", "job": job_id, "cues": len(cues), "audio_bytes": size}))
        return self._public(job), True

    def upload_chunk(self, user: str, job_id: str, index: int, data: bytes, declared_sha: str) -> dict:
        if not 0 <= index < 10_000:
            raise ApiError(400, "bad_chunk", "Invalid chunk index.")
        if len(data) == 0 or len(data) > self.cfg.max_chunk_bytes:
            raise ApiError(413, "chunk_too_large", f"Chunks must be 1..{self.cfg.max_chunk_bytes} bytes.")
        sha = hashlib.sha256(data).hexdigest()
        if declared_sha != sha:
            raise ApiError(400, "chunk_hash_mismatch", "Chunk SHA-256 does not match the uploaded bytes.")
        with self.lock:
            job = self._job(user, job_id)
            if job["status"] != "awaiting_upload":
                if job["chunks"].get(str(index), {}).get("sha256") == sha:
                    return self._public(job)  # duplicate retry after reconnect
                raise ApiError(409, "not_accepting_upload", "This job is not accepting audio.")
            if self.clock() > job["upload_deadline"]:
                raise ApiError(410, "upload_window_closed", "Upload window closed. Create a new job.")
            prev = job["chunks"].get(str(index))
            if prev and prev["sha256"] == sha:
                return self._public(job)
            if sum(c["bytes"] for k, c in job["chunks"].items() if k != str(index)) + len(data) > job["audio_bytes"]:
                raise ApiError(413, "too_much_audio", "More bytes than declared.")
            self.store.put_bytes(self._base(user, job_id) + f"audio/{index:05d}", data)
            job["chunks"][str(index)] = {"bytes": len(data), "sha256": sha}
            self._save(job)
            return self._public(job)

    def complete_upload(self, user: str, job_id: str, chunk_count: int) -> dict:
        with self.lock:
            job = self._job(user, job_id)
            if job["status"] != "awaiting_upload":
                return self._public(job)  # idempotent
            if not isinstance(chunk_count, int) or sorted(job["chunks"]) != sorted(map(str, range(chunk_count))):
                raise ApiError(409, "missing_chunks", "Not all chunks were uploaded.")
            tmp = Path(tempfile.mkdtemp(dir=self.cfg.work_root))
            try:
                wav = self._assemble(job, tmp)
                self._check_wav(wav)
            except ApiError as e:
                # Invalid audio ends the job, so it never blocks the user's active-job quota.
                self._finish(job, "failed", {"code": e.code, "message": e.message})
                raise
            finally:
                shutil.rmtree(tmp, ignore_errors=True)
            job["status"] = "queued"
            job["stage"] = "queued"
            self._save(job)
            self.cancel_flags[job_id] = threading.Event()
            self.queue.put((user, job_id))
        log.info(json.dumps({"event": "job_queued", "job": job_id}))
        return self._public(job)

    def status(self, user: str, job_id: str) -> dict:
        return self._public(self._job(user, job_id))

    def cancel(self, user: str, job_id: str) -> dict:
        with self.lock:
            job = self._job(user, job_id)
            if job["status"] in FINAL:
                return self._public(job)
            flag = self.cancel_flags.setdefault(job_id, threading.Event())
            flag.set()
            if job["status"] in ("awaiting_upload", "queued"):
                self._finish(job, "cancelled", None)
            else:
                job["cancel_requested"] = True
                self._save(job)
            return self._public(job)

    def result(self, user: str, job_id: str) -> dict:
        job = self._job(user, job_id)
        if job["status"] != "succeeded":
            raise ApiError(409, "not_ready", "No result: job status is " + job["status"])
        res = self.store.get_json(self._base(user, job_id) + "result.json")
        if res is None:
            raise ApiError(404, "not_found", "Result was deleted.")
        return res

    def delete(self, user: str, job_id: str) -> None:
        with self.lock:
            job = self._job(user, job_id)
            if job["status"] == "running":
                self.cancel_flags.setdefault(job_id, threading.Event()).set()
            self._purge(user, job_id)
        log.info(json.dumps({"event": "job_deleted", "job": job_id}))

    # ---------- internals ----------
    def _validate_cues(self, cues) -> list[dict]:
        if not isinstance(cues, list) or not 0 < len(cues) <= self.cfg.max_cues:
            raise ApiError(400, "bad_cues", f"Send 1..{self.cfg.max_cues} cues.")
        total, seen, out = 0, set(), []
        for c in cues:
            if not isinstance(c, dict) or not isinstance(c.get("id"), int) or not isinstance(c.get("text"), str):
                raise ApiError(400, "bad_cues", "Each cue needs an integer id and text.")
            if c["id"] in seen or len(c["text"]) > self.cfg.max_cue_chars or "\x00" in c["text"]:
                raise ApiError(400, "bad_cues", "Cue ids must be unique and texts bounded.")
            seen.add(c["id"])
            total += len(c["text"])
            out.append({"id": c["id"], "text": c["text"]})
        if total > self.cfg.max_text_chars:
            raise ApiError(413, "text_too_large", "Script text is too long for one job.")
        if not textmap.transcript(textmap.build(out)).strip():
            raise ApiError(422, "no_words", "The script has no words to align.")
        return out

    def _assemble(self, job: dict, tmp: Path) -> Path:
        wav = tmp / "audio.wav"
        h = hashlib.sha256()
        with open(wav, "wb") as f:
            for i in range(len(job["chunks"])):
                data = self.store.get_bytes(self._base(job["user"], job["job_id"]) + f"audio/{i:05d}")
                if data is None:
                    raise ApiError(409, "missing_chunks", "Uploaded audio is incomplete.")
                h.update(data)
                f.write(data)
        if wav.stat().st_size != job["audio_bytes"] or h.hexdigest() != job["audio_sha256"]:
            raise ApiError(422, "audio_hash_mismatch", "Uploaded audio does not match the declared size/SHA-256.")
        return wav

    def _check_wav(self, wav: Path) -> float:
        try:
            with open(wav, "rb") as f:
                head = f.read(12)
            if head[:4] != b"RIFF" or head[8:12] != b"WAVE":
                raise wave.Error("not RIFF/WAVE")
            with wave.open(str(wav), "rb") as w:
                ok = (w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getcomptype()) == (1, 2, 16000, "NONE")
                duration = w.getnframes() / 16000.0
        except (wave.Error, EOFError) as e:
            raise ApiError(422, "bad_audio", "Audio must be a WAV PCM file.") from e
        if not ok:
            raise ApiError(422, "bad_audio", "Audio must be WAV 16 kHz, mono, 16-bit PCM.")
        if not 0.2 <= duration <= self.cfg.max_audio_seconds:
            raise ApiError(413, "audio_duration", f"Audio duration must be 0.2..{self.cfg.max_audio_seconds} seconds.")
        return duration

    def _user_jobs(self, user: str) -> list[dict]:
        jobs = []
        for key in self.store.list(f"users/{user}/jobs/"):
            if key.endswith("/job.json"):
                job = self.store.get_json(key)
                if job:
                    jobs.append(job)
        return jobs

    def _public(self, job: dict) -> dict:
        stage = job.get("stage")
        order = ["awaiting_upload", "queued"] + aligner.STAGE_ORDER + ["done"]
        return {"job_id": job["job_id"], "status": job["status"], "stage": stage,
                "stage_index": order.index(stage) + 1 if stage in order else None, "stage_count": len(order),
                "language": job["language"], "cue_count": job["cue_count"], "cues_sha256": job["cues_sha256"],
                "audio_sha256": job["audio_sha256"], "audio_bytes": job["audio_bytes"],
                "uploaded_chunks": sorted(int(k) for k in job["chunks"]),
                "cancel_requested": bool(job.get("cancel_requested")),
                "error": job.get("error"), "expires_at": job["expires_at"], "retention_hours": self.cfg.retention_hours}

    def _finish(self, job: dict, status: str, error: dict | None) -> None:
        job["status"] = status
        job["error"] = error
        job["stage"] = "done" if status == "succeeded" else job.get("stage")
        base = self._base(job["user"], job["job_id"])
        # Inputs are no longer needed: delete audio and script now, keep only the result until expiry.
        self.store.delete_prefix(base + "audio/")
        if self.store.exists(base + "cues.json"):
            self.store.delete_prefix(base + "cues.json")
        self._save(job)

    def _purge(self, user: str, job_id: str) -> None:
        self.store.delete_prefix(self._base(user, job_id))
        self.cancel_flags.pop(job_id, None)

    # ---------- workers ----------
    def start(self) -> None:
        self.recover()
        for i in range(self.cfg.max_concurrent_jobs):
            t = threading.Thread(target=self._worker, name=f"mfa-worker-{i}", daemon=True)
            t.start()
            self.workers.append(t)
        threading.Thread(target=self._sweeper, name="mfa-sweeper", daemon=True).start()

    def recover(self) -> None:
        """Called at startup: re-queue queued jobs, fail jobs interrupted mid-run."""
        users = {k.split("/")[1] for k in self.store.list("users/") if k.count("/") >= 3}
        for user in sorted(users):
            for job in sorted(self._user_jobs(user), key=lambda j: j["created_at"]):
                if job["status"] == "queued":
                    self.cancel_flags[job["job_id"]] = threading.Event()
                    self.queue.put((user, job["job_id"]))
                elif job["status"] == "running":
                    self._finish(job, "failed", {"code": "interrupted", "message": "The server restarted during alignment."})

    def sweep(self) -> int:
        removed = 0
        now = self.clock()
        users = {k.split("/")[1] for k in self.store.list("users/") if k.count("/") >= 3}
        for user in users:
            for job in self._user_jobs(user):
                expired = job["expires_at"] <= now
                stale_upload = job["status"] == "awaiting_upload" and job["upload_deadline"] <= now
                if expired:
                    with self.lock:
                        self._purge(user, job["job_id"])
                    removed += 1
                elif stale_upload:
                    with self.lock:
                        self._finish(job, "failed", {"code": "upload_window_closed", "message": "Audio was not uploaded in time."})
            for key in self.store.list(f"users/{user}/requests/"):
                ref = self.store.get_json(key) or {}
                if not self.store.exists(self._base(user, ref.get("job_id", "x" * 32)) + "job.json"):
                    self.store.delete_prefix(key)
        return removed

    def _sweeper(self) -> None:
        while True:
            try:
                self.sweep()
            except Exception:
                log.exception("sweep failed")
            time.sleep(300)

    def _worker(self) -> None:
        while True:
            user, job_id = self.queue.get()
            try:
                self.run_job(user, job_id)
            except Exception:
                log.error(json.dumps({"event": "worker_crash", "job": job_id}))
            finally:
                self.queue.task_done()

    def run_job(self, user: str, job_id: str) -> None:
        with self.lock:
            try:
                job = self._job(user, job_id)
            except ApiError:
                return
            if job["status"] != "queued":
                return
            job["status"], job["stage"] = "running", "preparing"
            job["started_at"] = self.clock()
            self._save(job)
        flag = self.cancel_flags.setdefault(job_id, threading.Event())
        model = self.models[job["language"]]
        work = Path(tempfile.mkdtemp(prefix="job-", dir=self.cfg.work_root))
        started = time.monotonic()
        try:
            base = self._base(user, job_id)
            cues_raw = self.store.get_json(base + "cues.json")
            wav = self._assemble(job, work)
            duration = self._check_wav(wav)
            cues = textmap.build(cues_raw)

            def on_stage(stage):
                with self.lock:
                    current = self.store.get_json(base + "job.json")
                    if current and current["status"] == "running" and current.get("stage") != stage:
                        current["stage"] = stage
                        self._save(current)

            engine, analysis, method = aligner.align(work, wav, cues, model, duration, self.cfg.mfa_threads,
                                                     self.cfg.job_timeout_s, flag.is_set, on_stage)
            if flag.is_set():
                raise aligner.Cancelled()
            on_stage("finishing")
            result = aligner.build_result(job, model, cues, engine, duration, analysis, method)
            # Do not expose a completed result while its local audio/script still exist.
            # Readers may poll immediately after _finish publishes the terminal status.
            shutil.rmtree(work)
            with self.lock:
                current = self._job(user, job_id)
                if flag.is_set() or current.get("cancel_requested"):
                    raise aligner.Cancelled()
                self.store.put_json(base + "result.json", result)
                self._finish(current, "succeeded", None)
            log.info(json.dumps({"event": "job_succeeded", "job": job_id, "seconds": round(time.monotonic() - started, 1),
                                 "result": result["status"]}))
        except aligner.Cancelled:
            self._end(user, job_id, "cancelled", None)
        except aligner.AlignError as e:
            self._end(user, job_id, "failed", {"code": e.code, "message": str(e)})
        except ApiError as e:
            self._end(user, job_id, "failed", {"code": e.code, "message": e.message})
        except Exception:
            self._end(user, job_id, "failed", {"code": "internal", "message": "Unexpected server error."})
        finally:
            shutil.rmtree(work, ignore_errors=True)
            self.cancel_flags.pop(job_id, None)

    def _end(self, user: str, job_id: str, status: str, error) -> None:
        with self.lock:
            try:
                job = self._job(user, job_id)
            except ApiError:
                return
            if job["status"] not in FINAL:
                self._finish(job, status, error)
        log.info(json.dumps({"event": "job_" + status, "job": job_id, "code": (error or {}).get("code")}))


