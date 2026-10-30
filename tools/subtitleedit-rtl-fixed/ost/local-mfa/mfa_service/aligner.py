"""Runs the official MFA CLI on one job and converts its real output into the
documented result schema. Nothing here invents confidence or timings."""
from __future__ import annotations

import csv
import json
import math
import os
import signal
import subprocess
import re
import time
from pathlib import Path

from . import textmap

RESULT_SCHEMA = "subs-il.mfa-result/1"

# Progress is the pipeline step that is really running, never a made-up percentage.
STAGE_ORDER = ["preparing", "aligning_pass1", "segmenting", "aligning_pass2", "finishing"]
# Documented MFA retry option ("mfa align ... --beam 100 --retry_beam 400"); long real speech
# failed with the default beam 10/40 in our tests.
BEAM_CONFIG = "beam: 100\nretry_beam: 400\n"
SEGMENT_MIN_SECONDS = 30.0


class AlignError(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


class Cancelled(Exception):
    pass


def mfa_command(model: dict, corpus: Path, out: Path, tmp: Path, jobs: int, config: Path) -> list[str]:
    # Argument list only: no shell, no user-controlled strings (paths are server-generated).
    cmd = _bin(model) + ["align", "--config_path", str(config), "--clean", "--final_clean",
           "--single_speaker", "--no_tokenization", "--output_format", "json", "--num_jobs", str(jobs),
           "--temporary_directory", str(tmp / "mfa_tmp")]
    if model.get("g2p"):
        cmd += ["--g2p_model_path", model["g2p"]]
    return cmd + [str(corpus), model["dictionary"], model["acoustic"], str(out)]


def vad_command(model: dict, corpus: Path, out: Path, tmp: Path, jobs: int) -> list[str]:
    # MFA's own energy-based VAD (no SpeechBrain / network models).
    return _bin(model) + ["segment_vad", "--clean", "--final_clean", "--single_speaker", "--no_speechbrain",
            "--output_format", "long_textgrid", "--num_jobs", str(jobs), "--temporary_directory", str(tmp / "mfa_tmp_vad"),
            str(corpus), str(out)]


def _bin(model: dict) -> list[str]:
    value = model.get("mfa_bin", "mfa")
    return list(value) if isinstance(value, list) else [value]


def run_mfa(cmd: list[str], workdir: Path, timeout_s: float, cancelled) -> None:
    # MFA calls Kaldi/OpenFst binaries from its own environment, so that bin folder comes first.
    path = os.pathsep.join(filter(None, [os.path.dirname(cmd[0]), os.environ.get("PATH", "/usr/bin:/bin")]))
    env = {"PATH": path, "HOME": str(workdir), "MFA_ROOT_DIR": str(workdir / "mfa_root"),
           "LANG": "C.UTF-8", "LC_ALL": "C.UTF-8", "OMP_NUM_THREADS": "1", "TMPDIR": str(workdir)}
    log_path = workdir / "engine.log"  # deleted with the job; never forwarded to service logs
    with open(log_path, "ab") as log:
        proc = subprocess.Popen(cmd, cwd=workdir, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                                start_new_session=True)
        deadline = time.monotonic() + max(1.0, timeout_s)
        try:
            while proc.poll() is None:
                if cancelled():
                    _kill(proc)
                    raise Cancelled()
                if time.monotonic() > deadline:
                    _kill(proc)
                    raise AlignError("timeout", "Alignment exceeded the configured runtime limit.")
                time.sleep(0.3)
        finally:
            if proc.poll() is None:
                _kill(proc)
    if proc.returncode != 0:
        tail = log_path.read_bytes()[-4000:].decode("utf-8", "replace")
        if "NoAlignmentsError" in tail:
            raise AlignError("no_alignment", "MFA could not align this text to this audio (NoAlignmentsError). "
                                             "Check that the script and audio match and are in the selected language.")
        raise AlignError("engine_failed", f"MFA exited with code {proc.returncode}.")


def _kill(proc: subprocess.Popen) -> None:
    try:
        if hasattr(os, "killpg"):
            os.killpg(proc.pid, signal.SIGKILL)  # the whole MFA process group (Kaldi children)
        else:
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)], capture_output=True)
    except ProcessLookupError:
        pass
    proc.wait(timeout=30)


def prepare_corpus(workdir: Path, wav: Path, cues: list[textmap.Cue]) -> Path:
    corpus = workdir / "corpus"
    corpus.mkdir()
    os.replace(wav, corpus / "utt.wav")
    (corpus / "utt.lab").write_text(textmap.transcript(cues) + "\n", encoding="utf-8")
    return corpus


_TG_INTERVAL = re.compile(r'xmin = ([-\d.eE]+)\s*\n\s*xmax = ([-\d.eE]+)\s*\n\s*text = "((?:[^"]|"")*)"')


def read_vad_segments(path: Path) -> list[tuple[float, float]]:
    text = path.read_text(encoding="utf-8", errors="replace")
    return [(float(a), float(b)) for a, b, label in _TG_INTERVAL.findall(text) if label.strip() and float(b) > float(a)]


def plan_blocks(cues: list[textmap.Cue], pass1_spans: list, segments: list[tuple[float, float]], duration: float):
    """Group consecutive cues into utterances whose audio range is a run of VAD speech
    segments that their pass-1 engine times overlap. Script order is never changed."""
    blocks = []
    for cue, span in zip(cues, pass1_spans):
        segs = []
        if span:
            overlap = {i: min(b, span[1]) - max(a, span[0]) for i, (a, b) in enumerate(segments)}
            overlap = {i: o for i, o in overlap.items() if o > 0}
            total = sum(overlap.values())
            # Planning rule only (final times always come from pass 2): ignore a segment that holds
            # a sliver of the cue, which is how pass-1 drift into a previous utterance looks.
            segs = sorted(i for i, o in overlap.items() if o >= 0.5 and o >= 0.15 * total) or \
                ([max(overlap, key=overlap.get)] if overlap else [])
            if segs:
                segs = list(range(segs[0], segs[-1] + 1))
            if not segs:
                mid = (span[0] + span[1]) / 2
                segs = [min(range(len(segments)), key=lambda i: min(abs(segments[i][0] - mid), abs(segments[i][1] - mid)))]
        if blocks and (not segs or segs[0] <= blocks[-1]["hi"]):
            blocks[-1]["cues"].append(cue)
            if segs:
                blocks[-1]["hi"] = max(blocks[-1]["hi"], segs[-1])
        elif segs:
            blocks.append({"lo": segs[0], "hi": segs[-1], "cues": [cue]})
        else:  # leading cues with no engine time: attach to the first block that follows
            blocks.append({"lo": None, "hi": -1, "cues": [cue]})
    merged = []
    for b in blocks:
        if merged and merged[-1]["lo"] is None:
            b["cues"] = merged.pop()["cues"] + b["cues"]
        merged.append(b)
    if merged and merged[-1]["lo"] is None:
        return None
    out = []
    for i, b in enumerate(merged):
        start = max(0.0, segments[b["lo"]][0] - 0.1)
        end = min(duration, segments[b["hi"]][1] + 0.1)
        if out and start < out[-1][1]:
            start = out[-1][1]
        if end - start < 0.05:
            return None
        out.append((start, end, b["cues"]))
    return out


def write_textgrid(path: Path, duration: float, blocks) -> None:
    items = []
    t = 0.0
    for start, end, cues in blocks:
        if start > t:
            items.append((t, start, ""))
        items.append((start, end, textmap.transcript(cues)))
        t = end
    if t < duration:
        items.append((t, duration, ""))
    lines = ['File type = "ooTextFile"', 'Object class = "TextGrid"', "", "xmin = 0", f"xmax = {duration}", "tiers? <exists>",
             "size = 1", "item []:", "    item [1]:", '        class = "IntervalTier"', '        name = "speaker"',
             "        xmin = 0", f"        xmax = {duration}", f"        intervals: size = {len(items)}"]
    for i, (a, b, text) in enumerate(items, 1):
        lines += [f"        intervals [{i}]:", f"            xmin = {a}", f"            xmax = {b}",
                  '            text = "' + text.replace('"', '""') + '"']
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def align(work: Path, wav: Path, cues: list[textmap.Cue], model: dict, duration: float, threads: int,
          timeout_s: int, cancelled, on_stage):
    """Pass 1 aligns the whole script to the whole file. For long audio, pass 2 re-aligns
    each group of cues only inside the speech segments that pass 1 placed it in."""
    deadline = time.monotonic() + timeout_s
    config = work / "align.yaml"
    config.write_text(BEAM_CONFIG, encoding="utf-8")
    on_stage("preparing")
    corpus = prepare_corpus(work, wav, cues)
    out1 = work / "out1"
    on_stage("aligning_pass1")
    run_mfa(mfa_command(model, corpus, out1, work, threads, config), work, deadline - time.monotonic(), cancelled)
    engine1 = load_engine_json(out1)
    method = {"passes": 1, "segmentation": None, "beam": 100, "retry_beam": 400}
    # MFA's VAD runs for every job: it is the factual basis for "no speech" and pause review flags.
    on_stage("segmenting")
    vad_out = work / "vad"
    run_mfa(vad_command(model, corpus, vad_out, work, threads), work, deadline - time.monotonic(), cancelled)
    tg = vad_out / "utt.TextGrid"
    segments = read_vad_segments(tg) if tg.exists() else []
    if not segments:
        raise AlignError("no_speech", "MFA voice activity detection found no speech in this audio. Nothing was aligned.")
    method["vad_segments"] = [[round(a, 3), round(b, 3)] for a, b in segments]
    if duration < SEGMENT_MIN_SECONDS:
        return engine1, read_analysis(out1), method
    spans = _cue_spans(cues, engine1)
    blocks = plan_blocks(cues, spans, segments, duration) if segments else None
    if not blocks or len(blocks) < 2:
        method["segmentation"] = "MFA segment_vad found no usable split; single pass kept"
        return engine1, read_analysis(out1), method
    corpus2 = work / "corpus2"
    corpus2.mkdir()
    os.replace(corpus / "utt.wav", corpus2 / "utt.wav")
    write_textgrid(corpus2 / "utt.TextGrid", duration, blocks)
    out2 = work / "out2"
    on_stage("aligning_pass2")
    run_mfa(mfa_command(model, corpus2, out2, work, threads, config), work, deadline - time.monotonic(), cancelled)
    method.update(passes=2, segmentation="MFA segment_vad (energy, no SpeechBrain)", utterances=len(blocks))
    return load_engine_json(out2), read_analysis(out2), method


def _cue_spans(cues: list[textmap.Cue], engine: dict) -> list:
    fresh = [textmap.Cue(c.id, c.text, [textmap.Token(t.cue_id, t.index, t.text, t.char_start, t.char_end, t.subwords,
                                                        [None] * len(t.subwords), [False] * len(t.subwords)) for t in c.tokens])
             for c in cues]
    textmap.apply_engine_words(fresh, engine.get("tiers", {}).get("words", {}).get("entries", []), [])
    spans = []
    for c in fresh:
        times = [x for t in c.tokens for x in t.sub_times if x]
        spans.append((min(a for a, _ in times), max(b for _, b in times)) if times else None)
    return spans


def _num(value: str):
    try:
        f = float(value)
        return f if math.isfinite(f) else None
    except (TypeError, ValueError):
        return None


def read_analysis(out: Path) -> list[dict]:
    path = out / "alignment_analysis.csv"
    if not path.exists():
        return []
    with open(path, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    keep = ["begin", "end", "overall_log_likelihood", "speech_log_likelihood", "phone_duration_deviation",
            "max_running_short_interval", "snr", "intensity_deviation"]
    return [{k: _num(r.get(k, "")) for k in keep if k in r} for r in rows]


def build_result(job: dict, model: dict, cues: list[textmap.Cue], engine_json: dict, duration: float,
                 analysis: list[dict], method: dict | None = None) -> dict:
    words = engine_json.get("tiers", {}).get("words", {}).get("entries", [])
    phones = engine_json.get("tiers", {}).get("phones", {}).get("entries", [])
    spn = [(float(s), float(e)) for s, e, p in phones if p == "spn"]
    last_end = -1.0
    for s, e, lbl in words:
        s, e = float(s), float(e)
        if not (math.isfinite(s) and math.isfinite(e)) or s < 0 or e <= s or e > duration + 0.05 or s < last_end - 1e-6:
            raise AlignError("invalid_engine_timing", "The engine returned timings that are not valid for this audio.")
        last_end = e
    textmap.apply_engine_words(cues, words, spn)
    in_dict = model.get("dictionary_words")
    summary = dict(cues_total=len(cues), cues_aligned=0, cues_partial=0, cues_unaligned=0, cues_review=0,
                   words_total=0, words_aligned=0, words_missing=0, words_oov=0, words_g2p=0, tokens_not_speech=0)
    out_cues = []
    for cue in cues:
        cue_words, times, problem = [], [], False
        for tok in cue.tokens:
            flags = []
            if not tok.subwords:
                flags.append("not_speech")
                summary["tokens_not_speech"] += 1
                cue_words.append(_word(tok, None, flags))
                continue
            summary["words_total"] += 1
            got = [t for t in tok.sub_times if t is not None]
            if len(got) < len(tok.subwords):
                flags.append("missing" if not got else "partially_missing")
            if any(tok.sub_oov):
                flags.append("oov_unknown_pronunciation")
            if in_dict is not None and model.get("g2p") and any(w not in in_dict for w in tok.subwords):
                flags.append("g2p_pronunciation")
                summary["words_g2p"] += 1
            span = (min(t[0] for t in got), max(t[1] for t in got)) if got else None
            if span:
                times.append(span)
            if "missing" in flags or "partially_missing" in flags:
                summary["words_missing"] += 1
                problem = True
            elif "oov_unknown_pronunciation" in flags:
                summary["words_oov"] += 1
                problem = True
            else:
                summary["words_aligned"] += 1
            cue_words.append(_word(tok, span, flags))
        review = _review_flags(times, cue_words, (method or {}).get("vad_segments"))
        if not times:
            status = "unaligned"
        elif problem:
            status = "partial"
        else:
            status = "aligned"
        summary["cues_" + status] += 1
        summary["cues_review"] += 1 if review and times else 0
        out_cues.append({"id": cue.id, "status": status, "review_flags": review if times else [],
                         "start": round(min(t[0] for t in times), 3) if times else None,
                         "end": round(max(t[1] for t in times), 3) if times else None,
                         "words": cue_words})
    complete = summary["cues_aligned"] == len(cues)
    return {
        "schema": RESULT_SCHEMA,
        "job_id": job["job_id"],
        "status": ("complete" if not summary["cues_review"] else "needs_review") if complete else "partial",
        "engine": {"name": "Montreal Forced Aligner", "version": model["engine_version"],
                   "acoustic_model": model["acoustic_label"], "dictionary": model["dictionary_label"],
                   "g2p_model": model.get("g2p_label")},
        "language": model["id"],
        "input": {"audio_sha256": job["audio_sha256"], "audio_duration_s": round(duration, 3),
                  "cues_sha256": job["cues_sha256"], "cue_count": len(cues)},
        "summary": summary,
        "method": method or {"passes": 1},
        "engine_diagnostics": {
            "alignment_analysis": analysis,
            "note": "Values are copied from MFA alignment_analysis.csv. MFA does not report a per-word confidence; "
                    "none is provided here. A script that differs from the speech can still produce plausible but wrong times; "
                    "review_flags are factual prompts from engine output, and their absence does not prove correctness.",
        },
        "cues": out_cues,
    }


LONG_PAUSE_S = 1.5
LONG_WORD_S = 2.5


def _review_flags(times, words, vad_segments) -> list[dict]:
    """Facts that call for listening, derived from engine output only (MFA word times and
    MFA segment_vad). They are review prompts, not confidence scores."""
    flags = []
    if not times:
        return flags
    start, end = min(t[0] for t in times), max(t[1] for t in times)
    if vad_segments is not None:
        if not any(a < end and b > start for a, b in vad_segments):
            flags.append({"code": "no_speech_detected", "source": "MFA segment_vad"})
        for (a1, b1), (a2, b2) in zip(vad_segments, vad_segments[1:]):
            if a2 - b1 >= LONG_PAUSE_S and start < b1 and end > a2:
                flags.append({"code": "crosses_long_pause", "source": "MFA segment_vad",
                              "pause_start": b1, "pause_end": a2})
    for w in words:
        if w["start"] is not None and w["end"] - w["start"] > LONG_WORD_S:
            flags.append({"code": "long_word_duration", "source": "MFA word times", "word_index": w["index"],
                          "seconds": round(w["end"] - w["start"], 3)})
    return flags


def _word(tok: textmap.Token, span, flags) -> dict:
    return {"index": tok.index, "text": tok.text, "char_start": tok.char_start, "char_end": tok.char_end,
            "aligned_as": tok.subwords, "start": round(span[0], 3) if span else None,
            "end": round(span[1], 3) if span else None, "flags": flags}


def load_engine_json(out: Path) -> dict:
    path = out / "utt.json"
    if not path.exists():
        raise AlignError("no_alignment", "MFA produced no alignment for this audio and text.")
    with open(path, encoding="utf-8") as f:
        return json.load(f)


