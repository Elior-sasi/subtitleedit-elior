"""Local MFA bridge. No web service, tokens, uploads, or shell commands.

Run with the Python executable inside an installed MFA 3.4.3 conda environment.
Only the explicit --download-models action permits MFA model downloads.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import wave

sys.path.insert(0, str(Path(__file__).resolve().parent))
from mfa_service import aligner, textmap

MODEL = "english_us_arpa"
VERSION = "3.4.3"


def read_request(path: Path):
    if path.stat().st_size > 2_000_000:
        raise ValueError("request_too_large")
    req = json.loads(path.read_text(encoding="utf-8-sig"))
    cues = req.get("cues", [])
    if not 1 <= len(cues) <= 5000:
        raise ValueError("invalid_cue_count")
    total = 0
    for i, cue in enumerate(cues, 1):
        if cue.get("id") != i or not isinstance(cue.get("text"), str) or not cue["text"].strip() or len(cue["text"]) > 1000:
            raise ValueError("invalid_cue")
        total += len(cue["text"])
    if total > 400_000:
        raise ValueError("text_too_large")
    if any("\u0590" <= c <= "\u05ff" for cue in cues for c in cue["text"]):
        raise ValueError("english_models_only")
    return cues


def duration(path: Path):
    with wave.open(str(path), "rb") as w:
        if (w.getnchannels(), w.getframerate(), w.getsampwidth(), w.getcomptype()) != (1, 16000, 2, "NONE"):
            raise ValueError("invalid_wav_format")
        seconds = w.getnframes() / 16000
    if not 0 < seconds <= 7200:
        raise ValueError("invalid_audio_duration")
    return seconds


def run_cli(cmd, workdir, timeout_s, cancelled):
    """Preserve Windows conda DLL/SystemRoot environment; bound runtime and logs."""
    env = os.environ.copy()
    proc = subprocess.Popen(cmd, cwd=workdir, env=env, stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            start_new_session=os.name != "nt",
                            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    # Drain continuously without storing transcript/model/path output in the app log.
    import threading
    def drain():
        while proc.stdout.read(8192):
            pass
    reader = threading.Thread(target=drain, daemon=True)
    reader.start()
    deadline = time.monotonic() + max(1, timeout_s)
    try:
        while proc.poll() is None:
            if cancelled() or time.monotonic() > deadline:
                aligner._kill(proc)
                raise TimeoutError("engine_cancelled_or_timeout")
            time.sleep(.2)
        reader.join(timeout=5)
        if proc.returncode:
            raise RuntimeError("mfa_command_failed")
    finally:
        if proc.poll() is None:
            aligner._kill(proc)
        proc.stdout.close()


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("--cache", required=True, type=Path)
    p.add_argument("--check", action="store_true")
    p.add_argument("--download-models", action="store_true")
    p.add_argument("--request", type=Path)
    p.add_argument("--audio", type=Path)
    p.add_argument("--work", type=Path)
    args = p.parse_args(argv)
    version = importlib.metadata.version("montreal-forced-aligner")
    if version != VERSION:
        raise ValueError("mfa_version_must_be_3_4_3")
    root = args.cache.resolve()
    root.mkdir(parents=True, exist_ok=True)
    os.environ["MFA_ROOT_DIR"] = str(root)
    prefix = Path(sys.prefix)
    bins = [prefix / "Library" / "bin", prefix / "Scripts", prefix, prefix / "bin"]
    os.environ["PATH"] = os.pathsep.join(map(str, bins)) + os.pathsep + os.environ.get("PATH", "")
    cli = [sys.executable, "-m", "montreal_forced_aligner"]
    if args.check:
        run_cli(cli + ["--help"], root, 120, lambda: False)
        print("MFA 3.4.3 local CLI ready", flush=True)
        return
    if args.download_models:
        for kind in ("dictionary", "acoustic"):
            print("downloading_" + kind, flush=True)
            run_cli(cli + ["model", "download", kind, MODEL], root, 1800, lambda: False)
        print("English models downloaded", flush=True)
        return
    if not args.request or not args.audio or not args.work:
        raise ValueError("missing_alignment_input")
    # Named model lookup must already succeed locally; never implicitly download here.
    acoustic = root / "pretrained_models" / "acoustic" / (MODEL + ".zip")
    dictionary = root / "pretrained_models" / "dictionary" / (MODEL + ".dict")
    if not acoustic.is_file() or not dictionary.is_file():
        raise ValueError("download_english_models_first")
    cues = read_request(args.request)
    seconds = duration(args.audio)
    with args.audio.open("rb") as audio_stream:
        audio_hash = hashlib.file_digest(audio_stream, "sha256").hexdigest()
    cues_hash = hashlib.sha256(args.request.read_bytes()).hexdigest()
    work = args.work.resolve()
    work.mkdir(exist_ok=True)
    model = dict(id="en-US", acoustic=str(acoustic), dictionary=str(dictionary),
                 acoustic_label=MODEL, dictionary_label=MODEL, engine_version=version, mfa_bin=cli)
    aligner.run_mfa = run_cli
    built = textmap.build(cues)
    engine, analysis, method = aligner.align(work, args.audio, built, model, seconds,
                                           2, 7200, lambda: False,
                                           lambda stage: print(stage, flush=True))
    result = aligner.build_result(dict(job_id="local000000000000000000000000000", audio_sha256=audio_hash, cues_sha256=cues_hash),
                                  model, built, engine, seconds, analysis, method)
    output = work / "result.json"
    output.write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
    print("local_result_ready", flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        # No transcript or source file paths in diagnostics.
        print("Local MFA failed: " + type(e).__name__, file=sys.stderr)
        sys.exit(1)
