#!/usr/bin/env python3
"""TEST DOUBLE ONLY - never deployed. Mimics the MFA CLI file contract for protocol
tests where the real engine is not installed (e.g. the Windows CI client test).
Times are synthetic and evenly spaced; it is not an aligner.
Magic words: slowdown (sleep 4 s), failengine (exit 3), skipme (omitted), oovword (spn)."""
import json
import sys
import time
import wave
from pathlib import Path

args = sys.argv[1:]
if args[0] == "segment_vad":
    corpus, out = Path(args[-2]), Path(args[-1])
    out.mkdir(parents=True, exist_ok=True)
    with wave.open(str(corpus / "utt.wav")) as w:
        dur = w.getnframes() / w.getframerate()
    import array
    with wave.open(str(corpus / "utt.wav")) as w:
        loud = any(array.array("h", w.readframes(w.getnframes())))
    if loud or "silence_is_speech" in (corpus / "utt.lab").read_text():
        (out / "utt.TextGrid").write_text('xmin = 0\nxmax = %s\ntext = "speech"\n' % dur)
    sys.exit(0)
assert args[0] == "align"
corpus, out = Path(args[-4]), Path(args[-1])
print("INFO     Setting up corpus information...", flush=True)
words = (corpus / "utt.lab").read_text(encoding="utf-8").split()
time.sleep(4 if "slowdown" in words else 0.2)
print("INFO     Performing first-pass alignment...", flush=True)
if "failengine" in words:
    sys.exit(3)
with wave.open(str(corpus / "utt.wav")) as w:
    dur = w.getnframes() / w.getframerate()
step = dur / max(1, len(words) + 1)
entries, phones = [], []
for i, word in enumerate(words):
    if word == "skipme":
        continue
    s, e = round(step * (i + 0.5), 3), round(step * (i + 1.3), 3)
    entries.append([s, e, word])
    phones.append([s, e, "spn" if word == "oovword" else "a"])
out.mkdir(parents=True, exist_ok=True)
(out / "utt.json").write_text(json.dumps({"start": 0, "end": dur, "tiers": {"words": {"type": "interval", "entries": entries}, "phones": {"type": "interval", "entries": phones}}}))
(out / "alignment_analysis.csv").write_text("file,begin,end,speaker,overall_log_likelihood,speech_log_likelihood,phone_duration_deviation,max_running_short_interval,snr,intensity_deviation\nutt,0.0,%s,corpus,-80.0,-81.0,1.5,0,7.0,\n" % dur)
print("INFO     Exporting alignment TextGrids...", flush=True)

