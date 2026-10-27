import hashlib
import io
import json
import logging
import os
import struct
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
import uuid
import wave
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))

from client import Client  # noqa: E402
from mfa_service import aligner, api, textmap  # noqa: E402
from mfa_service.config import Config  # noqa: E402
from mfa_service.jobs import JobService  # noqa: E402
from mfa_service.storage import FileStorage  # noqa: E402

TOKEN_A = "a" * 40
TOKEN_B = "b" * 40


def make_wav(seconds=2.0, rate=16000, channels=1, width=2, silent=False) -> bytes:
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(width)
        w.setframerate(rate)
        w.writeframes((b"\x00\x00" if silent else b"\x10\x00") * int(seconds * rate) * channels)
    return buf.getvalue()


class Server:
    def __init__(self, tmp: Path, models=None, **overrides):
        tokens = tmp / "tokens.json"
        tokens.write_text(json.dumps({hashlib.sha256(TOKEN_A.encode()).hexdigest(): "alice",
                                      hashlib.sha256(TOKEN_B.encode()).hexdigest(): "bob"}))
        fake = str(HERE / "fake_mfa.py")
        dict_path = tmp / "words.dict"
        dict_path.write_text("hello\th a\nworld\tw a\n")
        cfg = Config(storage="file", storage_root=str(tmp / "data"), tokens_file=str(tokens), work_root=str(tmp / "work"))
        cfg.models = models or [
            {"id": "fake", "label": "fake", "available": True, "engine_version": "test", "mfa_bin": fake,
             "acoustic": str(dict_path), "dictionary": str(dict_path), "acoustic_label": "fake 0", "dictionary_label": "fake 0",
             "dictionary_words": frozenset({"hello", "world"})},
            {"id": "hebrew", "label": "עברית", "available": False, "reason": "No official MFA Hebrew acoustic model or dictionary."}]
        for k, v in overrides.items():
            setattr(cfg, k, v)
        os.environ["PORT"] = "0"
        os.environ["MFA_HOST"] = "127.0.0.1"
        self.app, self.httpd = api.build(cfg, engine_version="test")
        self.app.service.start()
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()
        self.base = f"http://127.0.0.1:{self.httpd.server_address[1]}"

    def client(self, token=TOKEN_A, chunk=8 * 1024 * 1024):
        return Client(self.base, token, chunk)

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()


class TextMapTests(unittest.TestCase):
    def test_original_text_and_spans_are_preserved(self):
        cue = {"id": 7, "text": "‏-שלום,  עולם! It's well-known…"}
        [c] = textmap.build([cue])
        self.assertEqual([t.text for t in c.tokens], ["‏-שלום,", "עולם!", "It's", "well-known…"])
        for t in c.tokens:
            self.assertEqual(cue["text"][t.char_start:t.char_end], t.text)
        self.assertEqual(textmap.transcript([c]), "שלום עולם it's well known")

    def test_formatting_tags_stay_in_text_but_not_in_transcript(self):
        text = '<i>We\'ll</i> {\\an8}<font color="red">see</font> {\\i1}now'
        [c] = textmap.build([{"id": 1, "text": text}])
        self.assertEqual(textmap.transcript([c]), "we'll see now")
        self.assertEqual(text.split(), [t.text for t in c.tokens])

    def test_punctuation_only_tokens_are_not_sent(self):
        [c] = textmap.build([{"id": 1, "text": "- ... ♪ Hello"}])
        self.assertEqual([t.subwords for t in c.tokens], [[], [], [], ["hello"]])

    def test_skipped_engine_word_does_not_shift_times(self):
        cues = textmap.build([{"id": 1, "text": "one two three"}])
        textmap.apply_engine_words(cues, [[0, 1, "one"], [2, 3, "three"]], [])
        self.assertEqual([t.sub_times for t in cues[0].tokens], [[(0, 1)], [None], [(2, 3)]])


class ResultTests(unittest.TestCase):
    model = {"id": "x", "engine_version": "3.4.3", "acoustic_label": "a", "dictionary_label": "d", "dictionary_words": None}
    job = {"job_id": "j" * 32, "audio_sha256": "0" * 64, "cues_sha256": "1" * 64}

    def engine(self, words, spn=()):
        return {"tiers": {"words": {"entries": words}, "phones": {"entries": [[s, e, "spn"] for s, e in spn]}}}

    def test_partial_and_unaligned_are_never_complete(self):
        cues = textmap.build([{"id": 1, "text": "Hello world."}, {"id": 2, "text": "Missing words"}, {"id": 3, "text": "odd zorb"}])
        res = aligner.build_result(self.job, self.model, cues,
                                   self.engine([[0.1, 0.5, "hello"], [0.5, 0.9, "world"], [2, 2.5, "odd"], [2.5, 3, "zorb"]], [(2.5, 3)]),
                                   5.0, [])
        self.assertEqual("partial", res["status"])
        self.assertEqual(["aligned", "unaligned", "partial"], [c["status"] for c in res["cues"]])
        self.assertIsNone(res["cues"][1]["start"])
        self.assertIn("oov_unknown_pronunciation", res["cues"][2]["words"][1]["flags"])
        self.assertEqual("Hello world.", "".join(w["text"] + " " for w in res["cues"][0]["words"]).strip())
        self.assertFalse(any("confidence" in w for c in res["cues"] for w in c["words"][0]))

    def test_cue_crossing_long_vad_pause_needs_review(self):
        cues = textmap.build([{"id": 1, "text": "a b"}])
        method = {"passes": 2, "vad_segments": [[0.0, 2.0], [4.0, 6.0]]}
        silent = aligner.build_result(self.job, self.model, textmap.build([{"id": 1, "text": "a"}]),
                                      self.engine([[2.2, 3.5, "a"]]), 7.0, [], method)
        self.assertEqual("no_speech_detected", silent["cues"][0]["review_flags"][0]["code"])
        res = aligner.build_result(self.job, self.model, cues, self.engine([[1.5, 1.8, "a"], [4.2, 4.5, "b"]]), 7.0, [], method)
        self.assertEqual("needs_review", res["status"])
        self.assertEqual("crosses_long_pause", res["cues"][0]["review_flags"][0]["code"])
        res = aligner.build_result(self.job, self.model, textmap.build([{"id": 1, "text": "a"}]),
                                   self.engine([[4.2, 7.0, "a"]]), 7.0, [], method)
        self.assertEqual("long_word_duration", res["cues"][0]["review_flags"][0]["code"])

    def test_invalid_engine_timings_fail_instead_of_being_fixed(self):
        cues = textmap.build([{"id": 1, "text": "a b"}])
        for bad in ([[1, 0.5, "a"]], [[0, 9, "a"]], [[1, 2, "a"], [0.5, 0.8, "b"]], [[float("nan"), 1, "a"]]):
            with self.assertRaises(aligner.AlignError):
                aligner.build_result(self.job, self.model, cues, self.engine(bad), 5.0, [])


class TwoPassPlanTests(unittest.TestCase):
    def test_cues_follow_pass1_into_vad_segments_in_script_order(self):
        cues = textmap.build([{"id": i, "text": t} for i, t in enumerate(["a b", "c", "d e", "f"], 1)])
        segments = [(1.0, 5.0), (7.0, 9.0), (12.0, 20.0)]
        # cue 2 was placed by pass 1 in the silence before segment 2; cue 4 got no time at all
        blocks = aligner.plan_blocks(cues, [(1.2, 4.0), (5.5, 6.9), (12.5, 19.0), None], segments, 21.0)
        self.assertEqual([[1], [2], [3, 4]], [[c.id for c in b[2]] for b in blocks])
        self.assertEqual([(0.9, 5.1), (6.9, 9.1), (11.9, 20.1)], [tuple(round(x, 2) for x in b[:2]) for b in blocks])

    def test_pass1_drift_sliver_does_not_merge_utterances(self):
        cues = textmap.build([{"id": 1, "text": "a"}, {"id": 2, "text": "b"}])
        blocks = aligner.plan_blocks(cues, [(8.9, 33.1), (33.1, 61.2)], [(7.8, 34.7), (36.5, 62.4)], 63)
        self.assertEqual([[1], [2]], [[c.id for c in b[2]] for b in blocks])

    def test_cue_spanning_two_segments_merges_them(self):
        cues = textmap.build([{"id": 1, "text": "a"}, {"id": 2, "text": "b"}])
        blocks = aligner.plan_blocks(cues, [(1, 8), (8.5, 9)], [(0.5, 4.0), (6.0, 8.2), (8.4, 9.5)], 10)
        self.assertEqual([[1], [2]], [[c.id for c in b[2]] for b in blocks])
        self.assertEqual((0.4, 8.3), tuple(round(x, 2) for x in blocks[0][:2]))  # one utterance over two segments

    def test_textgrid_input_escapes_quotes_and_covers_file(self):
        tmp = Path(tempfile.mkdtemp())
        cues = textmap.build([{"id": 1, "text": 'say "hi"'}])
        aligner.write_textgrid(tmp / "t.TextGrid", 10.0, [(1.0, 4.0, cues)])
        text = (tmp / "t.TextGrid").read_text()
        self.assertIn("intervals: size = 3", text)
        self.assertEqual([(1.0, 4.0)], aligner.read_vad_segments(tmp / "t.TextGrid"))


class ApiTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.srv = Server(self.tmp)
        self.c = self.srv.client()
        self.wav = make_wav(3)

    def tearDown(self):
        self.srv.close()

    def run_job(self, cues, wav=None, client=None):
        c = client or self.c
        st, job = c.create("fake", cues, wav or self.wav)
        self.assertEqual(201, st, job)
        st, body = c.upload(job["job_id"], wav or self.wav)
        self.assertEqual(200, st, body)
        return c.wait(job["job_id"])[1]

    def test_auth_required_and_health_has_no_secrets(self):
        st, body = self.srv.client(token="").req("GET", "/v1/languages")
        self.assertEqual(401, st)
        st, body = self.srv.client(token="x" * 40).req("GET", "/v1/languages")
        self.assertEqual(401, st)
        st, body = self.srv.client(token="").req("GET", "/healthz")
        self.assertEqual(200, st)
        self.assertNotIn("token", json.dumps(body).lower())

    def test_hebrew_is_reported_unavailable_with_reason(self):
        st, body = self.c.req("GET", "/v1/languages")
        heb = [l for l in body["languages"] if l["id"] == "hebrew"][0]
        self.assertFalse(heb["available"])
        self.assertIn("Hebrew", heb["reason"])
        st, body = self.c.create("hebrew", [{"id": 1, "text": "שלום"}], self.wav)
        self.assertEqual(422, st)

    def test_full_flow_chunked_result_and_cleanup(self):
        c = self.srv.client(chunk=20000)
        cues = [{"id": 1, "text": "Hello, world!"}, {"id": 2, "text": "skipme oovword hello"}]
        final = self.run_job(cues, client=c)
        self.assertEqual("succeeded", final["status"], final)
        st, res = c.req("GET", f"/v1/jobs/{final['job_id']}/result")
        self.assertEqual(200, st)
        self.assertEqual("partial", res["status"])
        self.assertEqual(hashlib.sha256(self.wav).hexdigest(), res["input"]["audio_sha256"])
        self.assertEqual("Hello, world!", " ".join(w["text"] for w in res["cues"][0]["words"]))
        left = [p.name for p in (self.tmp / "data").rglob("*") if p.is_file()]
        self.assertNotIn("cues.json", left)
        self.assertFalse(any(p.parent.name == "audio" for p in (self.tmp / "data").rglob("*") if p.is_file()))
        self.assertEqual([], list((self.tmp / "work").iterdir()))
        st, _ = c.req("DELETE", f"/v1/jobs/{final['job_id']}")
        self.assertEqual(200, st)
        self.assertEqual(404, c.req("GET", f"/v1/jobs/{final['job_id']}")[0])

    def test_success_waits_for_local_work_cleanup(self):
        from mfa_service import jobs
        entered, release = threading.Event(), threading.Event()
        original = jobs.shutil.rmtree

        def cleanup(path, *args, **kwargs):
            if Path(path).name.startswith("job-") and not entered.is_set():
                entered.set()
                release.wait(5)
            return original(path, *args, **kwargs)

        with patch.object(jobs.shutil, "rmtree", side_effect=cleanup):
            st, job = self.c.create("fake", [{"id": 1, "text": "hello world"}], self.wav)
            self.assertEqual(201, st)
            try:
                self.assertEqual(200, self.c.upload(job["job_id"], self.wav)[0])
                self.assertTrue(entered.wait(5))
                self.assertEqual(409, self.c.req("GET", f"/v1/jobs/{job['job_id']}/result")[0])
                self.assertEqual("running", self.c.req("GET", f"/v1/jobs/{job['job_id']}")[1]["status"])
            finally:
                release.set()
            self.assertEqual("succeeded", self.c.wait(job["job_id"])[1]["status"])
            self.assertEqual([], list((self.tmp / "work").iterdir()))

    def test_reconnect_reuses_job_and_conflicting_reuse_rejected(self):
        rid = str(uuid.uuid4())
        cues = [{"id": 1, "text": "hello world"}]
        st1, j1 = self.c.create("fake", cues, self.wav, rid)
        st2, j2 = self.c.create("fake", cues, self.wav, rid)
        self.assertEqual((201, 200), (st1, st2))
        self.assertEqual(j1["job_id"], j2["job_id"])
        st, _ = self.c.create("fake", [{"id": 1, "text": "changed"}], self.wav, rid)
        self.assertEqual(409, st)
        # resumable upload: first attempt drops chunk 1, retry sends all (duplicates accepted)
        c = self.srv.client(chunk=30000)
        st, body = c.upload(j1["job_id"], self.wav, skip={1})
        self.assertEqual(409, st)
        st, body = c.upload(j1["job_id"], self.wav)
        self.assertEqual(200, st, body)
        self.assertEqual(200, c.upload(j1["job_id"], self.wav)[0])  # repeat complete is idempotent
        self.assertEqual("succeeded", c.wait(j1["job_id"])[1]["status"])

    def test_users_are_isolated(self):
        st, job = self.c.create("fake", [{"id": 1, "text": "hello"}], self.wav)
        other = self.srv.client(TOKEN_B)
        for method, path in [("GET", ""), ("POST", "/cancel"), ("GET", "/result"), ("DELETE", "")]:
            self.assertEqual(404, other.req(method, f"/v1/jobs/{job['job_id']}{path}")[0])

    def test_rejects_bad_audio_hash_size_and_format(self):
        st, _ = self.c.create("fake", [{"id": 1, "text": "hello"}], b"x" * 10)
        self.assertEqual(400, st)
        for wav in (make_wav(1, rate=44100), make_wav(1, channels=2), b"RIFF" + b"\0" * 100):
            st, job = self.c.create("fake", [{"id": 1, "text": "hello"}], wav)
            self.assertEqual(201, st)
            st, body = self.c.upload(job["job_id"], wav)
            self.assertEqual(422, st, body)
        st, job = self.c.create("fake", [{"id": 1, "text": "hello"}], self.wav)
        part = self.wav[:1000]
        st, _ = self.c.req("PUT", f"/v1/jobs/{job['job_id']}/audio/chunks/0", part, {"X-Chunk-SHA256": "0" * 64})
        self.assertEqual(400, st)
        st, _ = self.c.req("PUT", f"/v1/jobs/{job['job_id']}/audio/chunks/0", b"x" * (8 * 1024 * 1024 + 1),
                           {"X-Chunk-SHA256": "0" * 64})
        self.assertEqual(413, st)

    def test_cancel_running_job_kills_engine_and_leaves_no_result(self):
        st, job = self.c.create("fake", [{"id": 1, "text": "slowdown hello"}], self.wav)
        self.c.upload(job["job_id"], self.wav)
        for _ in range(50):
            if self.c.req("GET", f"/v1/jobs/{job['job_id']}")[1]["status"] == "running":
                break
            time.sleep(0.1)
        self.assertEqual(200, self.c.req("POST", f"/v1/jobs/{job['job_id']}/cancel")[0])
        final = self.c.wait(job["job_id"], 20)[1]
        self.assertEqual("cancelled", final["status"])
        self.assertEqual(409, self.c.req("GET", f"/v1/jobs/{job['job_id']}/result")[0])

    def test_silent_audio_fails_as_no_speech(self):
        final = self.run_job([{"id": 1, "text": "hello world"}], wav=make_wav(3, silent=True))
        self.assertEqual(("failed", "no_speech"), (final["status"], final["error"]["code"]))

    def test_engine_failure_and_limits(self):
        final = self.run_job([{"id": 1, "text": "failengine"}])
        self.assertEqual(("failed", "engine_failed"), (final["status"], final["error"]["code"]))
        self.c.create("fake", [{"id": 1, "text": "hello"}], self.wav)
        self.c.create("fake", [{"id": 1, "text": "hello"}], self.wav)
        st, body = self.c.create("fake", [{"id": 1, "text": "hello"}], self.wav)
        self.assertEqual(429, st)
        self.assertEqual(400, self.srv.client(TOKEN_B).create("fake", [{"id": 1, "text": "x" * 5000}], self.wav)[0])

    def test_logs_never_contain_script_text(self):
        records = []
        handler = logging.Handler()
        handler.emit = lambda r: records.append(r.getMessage())
        logging.getLogger("subs_il_mfa").addHandler(handler)
        logging.getLogger("subs_il_mfa").setLevel(logging.INFO)
        self.run_job([{"id": 1, "text": "SecretScriptWord hello"}])
        joined = "\n".join(records)
        self.assertIn("job_succeeded", joined)
        self.assertNotIn("SecretScriptWord".lower(), joined.lower())
        self.assertNotIn(TOKEN_A, joined)


class RetentionTests(unittest.TestCase):
    def test_sweep_deletes_expired_jobs_and_fails_stale_uploads(self):
        tmp = Path(tempfile.mkdtemp())
        now = [1000.0]
        cfg = Config(storage_root=str(tmp / "d"), work_root=str(tmp / "w"))
        cfg.models = [{"id": "fake", "available": True}]
        svc = JobService(cfg, FileStorage(cfg.storage_root), clock=lambda: now[0])
        wav = make_wav(1)
        job, _ = svc.create("alice", {"client_request_id": str(uuid.uuid4()), "language": "fake", "cues": [{"id": 1, "text": "hi"}],
                                      "audio_sha256": hashlib.sha256(wav).hexdigest(), "audio_bytes": len(wav)})
        now[0] += cfg.upload_window_s + 1
        svc.sweep()
        self.assertEqual("failed", svc.status("alice", job["job_id"])["status"])
        now[0] += cfg.retention_hours * 3600
        svc.sweep()
        self.assertEqual([], FileStorage(cfg.storage_root).list("users/"))

    def test_restart_fails_interrupted_and_requeues_queued(self):
        tmp = Path(tempfile.mkdtemp())
        cfg = Config(storage_root=str(tmp / "d"), work_root=str(tmp / "w"))
        cfg.models = [{"id": "fake", "available": True}]
        store = FileStorage(cfg.storage_root)
        svc = JobService(cfg, store)
        wav = make_wav(1)
        ids = []
        for _ in range(2):
            job, _ = svc.create("alice", {"client_request_id": str(uuid.uuid4()), "language": "fake", "cues": [{"id": 1, "text": "hi"}],
                                          "audio_sha256": hashlib.sha256(wav).hexdigest(), "audio_bytes": len(wav)})
            svc.upload_chunk("alice", job["job_id"], 0, wav, hashlib.sha256(wav).hexdigest())
            svc.complete_upload("alice", job["job_id"], 1)
            ids.append(job["job_id"])
        raw = store.get_json(f"users/alice/jobs/{ids[0]}/job.json")
        raw["status"] = "running"
        store.put_json(f"users/alice/jobs/{ids[0]}/job.json", raw)
        svc2 = JobService(cfg, store)
        svc2.recover()
        self.assertEqual("failed", svc2.status("alice", ids[0])["status"])
        self.assertEqual("interrupted", svc2.status("alice", ids[0])["error"]["code"])
        self.assertEqual(1, svc2.queue.qsize())


@unittest.skipUnless(os.environ.get("MFA_REAL_MODELS"), "set MFA_REAL_MODELS=/path/models.json to run the real engine")
class RealEngineTests(unittest.TestCase):
    """Runs the official MFA CLI. Audio: MFA_REAL_WAV (16 kHz mono) + MFA_REAL_CUES (JSON list)."""

    def test_real_alignment_end_to_end(self):
        tmp = Path(tempfile.mkdtemp())
        from mfa_service.config import load_models
        srv = Server(tmp, models=load_models(os.environ["MFA_REAL_MODELS"]), job_timeout_s=900)
        try:
            c = srv.client(chunk=1024 * 1024)
            wav = Path(os.environ["MFA_REAL_WAV"]).read_bytes()
            cues = json.loads(Path(os.environ["MFA_REAL_CUES"]).read_text(encoding="utf-8"))
            st, job = c.create(os.environ.get("MFA_REAL_LANGUAGE", "english_mfa"), cues, wav)
            self.assertEqual(201, st, job)
            stages = set()
            self.assertEqual(200, c.upload(job["job_id"], wav)[0])
            while True:
                st, s = c.req("GET", f"/v1/jobs/{job['job_id']}")
                stages.add(s["stage"])
                if s["status"] in ("succeeded", "failed", "cancelled"):
                    break
                time.sleep(0.5)
            self.assertEqual("succeeded", s["status"], s)
            st, res = c.req("GET", f"/v1/jobs/{job['job_id']}/result")
            out = os.environ.get("MFA_REAL_OUT")
            if out:
                Path(out).write_text(json.dumps(res, ensure_ascii=False, indent=1), encoding="utf-8")
            self.assertTrue({"aligning_pass1"} & stages, stages)
            self.assertEqual(len(cues), len(res["cues"]))
            for cue, orig in zip(res["cues"], cues):
                self.assertEqual(orig["text"].split(), [w["text"] for w in cue["words"]])
        finally:
            srv.close()


if __name__ == "__main__":
    unittest.main()


