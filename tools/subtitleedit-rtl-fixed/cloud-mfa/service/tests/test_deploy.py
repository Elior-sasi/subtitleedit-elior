import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

HERE = pathlib.Path(__file__).resolve().parent
SCRIPT = HERE.parent / "deploy" / "deploy.sh"
sys.path.insert(0, str(HERE.parent))

from mfa_service.storage import GcsStorage  # noqa: E402


class DeployScriptTests(unittest.TestCase):
    def test_plan_calls_nothing_and_shows_cost(self):
        r = subprocess.run(["bash", str(SCRIPT), "example-project"], env={"PATH": "/usr/bin:/bin"}, capture_output=True, text=True)
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertIn("USD/hour", r.stdout)
        self.assertIn("NOT a spending cap", r.stdout)

    def test_rejects_injection_bad_mode_and_plain_tokens(self):
        for args in (["x; rm -rf /", "--plan"], ["example-project", "--go"]):
            self.assertEqual(2, subprocess.run(["bash", str(SCRIPT), *args], capture_output=True).returncode)
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as f:
            json.dump({"sim_plaintexttoken": "elior"}, f)
        r = subprocess.run(["bash", str(SCRIPT), "example-project", "--deploy", f.name], capture_output=True, text=True, input="example-project\n")
        self.assertEqual(2, r.returncode)

    def _tokens(self):
        f = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False)
        json.dump({"a" * 64: "elior"}, f)
        f.close()
        return f.name

    def test_cancel_before_any_cloud_call(self):
        with tempfile.TemporaryDirectory() as tmp:
            log = pathlib.Path(tmp) / "calls"
            fake = pathlib.Path(tmp) / "gcloud"
            fake.write_text('#!/bin/bash\necho "$*" >> "$CALL_LOG"\n')
            fake.chmod(0o700)
            env = dict(os.environ, PATH=tmp + ":" + os.environ["PATH"], CALL_LOG=str(log))
            r = subprocess.run(["bash", str(SCRIPT), "example-project", "--deploy", self._tokens()], input="no\n",
                               capture_output=True, text=True, env=env)
            self.assertEqual(3, r.returncode)
            self.assertFalse(log.exists())

    def test_deploy_is_private_bounded_and_digest_pinned(self):
        with tempfile.TemporaryDirectory() as tmp:
            log = pathlib.Path(tmp) / "calls"
            fake = pathlib.Path(tmp) / "gcloud"
            fake.write_text('#!/bin/bash\nprintf "%s\\n" "$*" >> "$CALL_LOG"\n'
                            'if [[ "$1 $2" == "billing projects" ]]; then echo True; fi\n'
                            'if [[ "$*" == *"describe"* && "$*" != *"docker images"* && "$*" != *"run services"* && "$*" != billing* ]]; then exit 1; fi\n'
                            'if [[ "$1 $2 $3 $4" == "artifacts docker images describe" ]]; then printf "sha256:%064d\\n" 0; fi\n'
                            'if [[ "$1 $2 $3" == "run services describe" ]]; then echo https://x.run.app; fi\n')
            fake.chmod(0o700)
            env = dict(os.environ, PATH=tmp + ":" + os.environ["PATH"], CALL_LOG=str(log))
            r = subprocess.run(["bash", str(SCRIPT), "example-project", "--deploy", self._tokens()], input="example-project\n",
                               capture_output=True, text=True, env=env)
            self.assertEqual(0, r.returncode, r.stderr + r.stdout)
            calls = log.read_text()
            self.assertIn("--uniform-bucket-level-access --public-access-prevention", calls)
            self.assertIn("--max-instances=1", calls)
            self.assertIn("@sha256:" + "0" * 64, calls)
            self.assertIn("--execution-environment=gen2", calls)
            for forbidden in ("keys create", "allUsers", "billing projects link", "projects add-iam-policy-binding", "roles/owner",
                              "roles/editor"):
                self.assertNotIn(forbidden, calls)


class FakeBlob:
    def __init__(self, store, name):
        self.store, self.name = store, name

    def upload_from_file(self, f, size, content_type):
        self.store[self.name] = f.read()

    def download_as_bytes(self):
        if self.name not in self.store:
            raise type("NotFound", (Exception,), {})()
        return self.store[self.name]

    def download_to_filename(self, path):
        pathlib.Path(path).write_bytes(self.download_as_bytes())

    def exists(self):
        return self.name in self.store

    def delete(self):
        del self.store[self.name]


class FakeBucket:
    def __init__(self):
        self.objects = {}
        self.client = self

    def blob(self, name):
        return FakeBlob(self.objects, name)

    def list_blobs(self, bucket, prefix=None):
        return [FakeBlob(self.objects, n) for n in sorted(self.objects) if not prefix or n.startswith(prefix)]


class FakeClient:
    def __init__(self):
        self.b = FakeBucket()

    def bucket(self, name):
        return self.b


class GcsStorageTests(unittest.TestCase):
    def test_contract_matches_file_storage(self):
        s = GcsStorage("b", client=FakeClient())
        s.put_json("users/a/jobs/x/job.json", {"k": "ש"})
        self.assertEqual({"k": "ש"}, s.get_json("users/a/jobs/x/job.json"))
        self.assertIsNone(s.get_bytes("users/a/missing"))
        self.assertEqual(["users/a/jobs/x/job.json"], s.list("users/a/"))
        s.delete_prefix("users/a/jobs/x/")
        self.assertEqual([], s.list("users/"))
        with self.assertRaises(ValueError):
            s.put_bytes("../etc/passwd", b"")


if __name__ == "__main__":
    unittest.main()

