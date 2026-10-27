"""Service limits and model registry. All values come from the environment with safe defaults."""
from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from pathlib import Path


def _int(name: str, default: int, lo: int, hi: int) -> int:
    value = int(os.environ.get(name, default))
    if not lo <= value <= hi:
        raise ValueError(f"{name} must be between {lo} and {hi}")
    return value


@dataclass
class Config:
    storage: str = "file"                 # file | gcs
    storage_root: str = "/tmp/subs-il-mfa-data"
    bucket: str = ""
    tokens_file: str = ""                 # JSON {sha256(token): user_id}, mounted from Secret Manager
    models_file: str = ""
    work_root: str = "/tmp/subs-il-mfa-work"
    max_audio_seconds: int = 3600
    max_chunk_bytes: int = 8 * 1024 * 1024
    max_cues: int = 5000
    max_cue_chars: int = 1000
    max_text_chars: int = 400_000
    max_concurrent_jobs: int = 1
    max_queued_jobs: int = 10
    max_active_per_user: int = 2
    job_timeout_s: int = 3600
    upload_window_s: int = 3600
    retention_hours: int = 6
    mfa_threads: int = 2
    service_name: str = "subs-il MFA (Google Cloud Run)"
    models: list = field(default_factory=list)

    @staticmethod
    def from_env() -> "Config":
        c = Config(
            storage=os.environ.get("MFA_STORAGE", "file"),
            storage_root=os.environ.get("MFA_STORAGE_ROOT", "/tmp/subs-il-mfa-data"),
            bucket=os.environ.get("MFA_BUCKET", ""),
            tokens_file=os.environ.get("MFA_TOKENS_FILE", ""),
            models_file=os.environ.get("MFA_MODELS_FILE", ""),
            work_root=os.environ.get("MFA_WORK_ROOT", "/tmp/subs-il-mfa-work"),
            max_audio_seconds=_int("MFA_MAX_AUDIO_SECONDS", 3600, 1, 4 * 3600),
            max_concurrent_jobs=_int("MFA_MAX_CONCURRENT_JOBS", 1, 1, 8),
            max_queued_jobs=_int("MFA_MAX_QUEUED_JOBS", 10, 1, 200),
            max_active_per_user=_int("MFA_MAX_ACTIVE_PER_USER", 2, 1, 20),
            job_timeout_s=_int("MFA_JOB_TIMEOUT_SECONDS", 3600, 30, 6 * 3600),
            retention_hours=_int("MFA_RETENTION_HOURS", 6, 1, 72),
            mfa_threads=_int("MFA_THREADS", 2, 1, 8),
        )
        if c.storage not in ("file", "gcs") or (c.storage == "gcs" and not c.bucket):
            raise ValueError("MFA_STORAGE must be file or gcs (with MFA_BUCKET)")
        c.models = load_models(c.models_file)
        return c

    @property
    def max_audio_bytes(self) -> int:
        return 44 + self.max_audio_seconds * 32000 + 4096


def load_models(path: str) -> list[dict]:
    """models.json entries: id, label, engine_version, acoustic, dictionary, optional g2p,
    *_label fields, available, verified, reason. Unavailable entries document what is missing."""
    if not path:
        return []
    models = json.loads(Path(path).read_text(encoding="utf-8"))
    for m in models:
        if m.get("available"):
            for key in ("acoustic", "dictionary") + (("g2p",) if m.get("g2p") else ()):
                if not Path(m[key]).is_file():
                    m["available"] = False
                    m["reason"] = f"model file missing on server: {key}"
            if m.get("available"):
                m["dictionary_words"] = _dictionary_words(m["dictionary"])
    return models


def _dictionary_words(path: str) -> frozenset:
    words = set()
    with open(path, encoding="utf-8") as f:
        for line in f:
            head = line.split("\t", 1)[0].split(" ", 1)[0].strip()
            if head:
                words.add(head.casefold())
    return frozenset(words)

