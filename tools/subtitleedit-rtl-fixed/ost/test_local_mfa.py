"""Offline boundary and real subprocess tests; no inference-quality claim."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
import wave

ROOT = Path(__file__).parent / "local-mfa"
spec = importlib.util.spec_from_file_location("local_runner", ROOT / "run_local_mfa.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class LocalMfaTests(unittest.TestCase):
    def test_request_keeps_markup_and_source_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "request.json"
            p.write_text(json.dumps({"cues": [{"id": 1, "text": '<font title="a b">Hello, WORLD!</font>'}]}))
            before = p.read_bytes()
            cues = runner.read_request(p)
            built = runner.textmap.build(cues)
            self.assertEqual("hello world", runner.textmap.transcript(built))
            self.assertEqual(cues[0]["text"], built[0].text)
            self.assertEqual(before, p.read_bytes())

    def test_reject_hebrew_empty_and_wrong_ids(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "request.json"
            for cues in ([], [{"id": 1, "text": "שלום"}], [{"id": 2, "text": "Hello"}], [{"id": 1, "text": "a" * 1001}]):
                p.write_text(json.dumps({"cues": cues}))
                with self.assertRaises(ValueError):
                    runner.read_request(p)

    def test_wav_requires_mono_16khz_and_bounded_duration(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "audio.wav"
            with wave.open(str(p), "wb") as w:
                w.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
                w.writeframes(b"\0\0" * 16000)
            self.assertEqual(1, runner.duration(p))
            with wave.open(str(p), "wb") as w:
                w.setparams((2, 2, 16000, 0, "NONE", "not compressed"))
                w.writeframes(b"\0" * 64000)
            with self.assertRaises(ValueError):
                runner.duration(p)

    def test_subprocess_drains_large_output_and_fails_closed(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            runner.run_cli([sys.executable, "-c", "print('x'*200000)"], root, 10, lambda: False)
            with self.assertRaises(RuntimeError):
                runner.run_cli([sys.executable, "-c", "raise SystemExit(2)"], root, 10, lambda: False)

    def test_cancel_kills_owned_process(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(TimeoutError):
                runner.run_cli([sys.executable, "-c", "import time; time.sleep(30)"], Path(tmp), 10, lambda: True)

    def test_engine_missing_word_stays_partial_without_inventing_time(self):
        cues = runner.textmap.build([{"id": 1, "text": "Hello, missing world!"}])
        r = runner.aligner.build_result({"job_id": "local", "audio_sha256": "a", "cues_sha256": "c"},
             {"id": "en", "engine_version": "3.4.3", "acoustic_label": "test", "dictionary_label": "test"}, cues,
             {"tiers": {"words": {"entries": [[.1, .3, "hello"], [.8, 1, "world"]]}}}, 2, [])
        self.assertEqual("partial", r["status"])
        self.assertEqual("missing", r["cues"][0]["words"][1]["flags"][0])
        self.assertIsNone(r["cues"][0]["words"][1]["start"])
        self.assertEqual("Hello,", r["cues"][0]["words"][0]["text"])


if __name__ == "__main__":
    unittest.main()
