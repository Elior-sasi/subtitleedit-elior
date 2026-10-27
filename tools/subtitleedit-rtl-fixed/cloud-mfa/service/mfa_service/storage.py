"""Private object storage. FileStorage for local runs and tests, GcsStorage for
Cloud Run. Keys are server-generated (user id + random job id); never user paths."""
from __future__ import annotations

import io
import json
import os
import re
import shutil
import tempfile
import time
from pathlib import Path

_KEY = re.compile(r"^[A-Za-z0-9_\-][A-Za-z0-9_\-./]{0,300}$")


def _retry(action):
    # Windows refuses to replace or open a file another thread holds open; that is brief here.
    for attempt in range(50):
        try:
            return action()
        except PermissionError:
            if attempt == 49:
                raise
            time.sleep(0.02)


def _check(key: str) -> str:
    if not _KEY.match(key) or ".." in key or "//" in key:
        raise ValueError("invalid storage key")
    return key


class FileStorage:
    def __init__(self, root: str):
        self.root = Path(root).resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        os.chmod(self.root, 0o700)

    def _path(self, key: str) -> Path:
        path = (self.root / _check(key)).resolve()
        if self.root not in path.parents:
            raise ValueError("invalid storage key")
        return path

    def put_bytes(self, key: str, data: bytes) -> None:
        path = self._path(key)
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        fd, tmp = tempfile.mkstemp(dir=path.parent)
        with os.fdopen(fd, "wb") as f:
            f.write(data)
        _retry(lambda: os.replace(tmp, path))

    def get_bytes(self, key: str) -> bytes | None:
        try:
            return _retry(self._path(key).read_bytes)
        except FileNotFoundError:
            return None

    def download(self, key: str, dest: Path) -> bool:
        try:
            shutil.copyfile(self._path(key), dest)
            return True
        except FileNotFoundError:
            return False

    def exists(self, key: str) -> bool:
        return self._path(key).exists()

    def list(self, prefix: str) -> list[str]:
        base = self._path(prefix.rstrip("/")) if prefix else self.root
        if not base.exists():
            return []
        return sorted(str(p.relative_to(self.root)) for p in base.rglob("*") if p.is_file())

    def delete_prefix(self, prefix: str) -> None:
        path = self._path(prefix.rstrip("/"))
        if path.is_dir():
            shutil.rmtree(path, ignore_errors=True)
        elif path.exists():
            path.unlink()

    def put_json(self, key: str, obj) -> None:
        self.put_bytes(key, json.dumps(obj, ensure_ascii=False, separators=(",", ":")).encode())

    def get_json(self, key: str):
        data = self.get_bytes(key)
        return None if data is None else json.loads(data)


class GcsStorage(FileStorage):
    """Same interface on a private bucket (uniform access, public access prevention,
    lifecycle delete as a backstop). Credentials come from the Cloud Run identity."""

    def __init__(self, bucket: str, client=None):
        if client is None:
            from google.cloud import storage  # imported only in the cloud image
            client = storage.Client()
        self.bucket = client.bucket(bucket)

    def put_bytes(self, key: str, data: bytes) -> None:
        self.bucket.blob(_check(key)).upload_from_file(io.BytesIO(data), size=len(data), content_type="application/octet-stream")

    def get_bytes(self, key: str) -> bytes | None:
        blob = self.bucket.blob(_check(key))
        try:
            return blob.download_as_bytes()
        except Exception as e:  # google.api_core.exceptions.NotFound
            if type(e).__name__ == "NotFound":
                return None
            raise

    def download(self, key: str, dest: Path) -> bool:
        data_blob = self.bucket.blob(_check(key))
        try:
            data_blob.download_to_filename(str(dest))
            return True
        except Exception as e:
            if type(e).__name__ == "NotFound":
                return False
            raise

    def exists(self, key: str) -> bool:
        return self.bucket.blob(_check(key)).exists()

    def list(self, prefix: str) -> list[str]:
        return sorted(b.name for b in self.bucket.client.list_blobs(self.bucket, prefix=_check(prefix) if prefix else None))

    def delete_prefix(self, prefix: str) -> None:
        for name in self.list(prefix):
            self.bucket.blob(name).delete()

