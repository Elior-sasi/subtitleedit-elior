"""Applies player-diagnostics.patch.json to an upstream checkout exactly like
apply-player-diagnostics.ps1 (same exactly-once anchor rule), into an output folder.
Usage: python3 check-player-diagnostics.py <subtitleedit-checkout> <out-dir>
"""
import json
import os
import sys

src, out = sys.argv[1], sys.argv[2]
here = os.path.dirname(os.path.abspath(__file__))
patch = json.load(open(os.path.join(here, "player-diagnostics.patch.json"), encoding="utf-8"))
files = {}
for edit in patch["edits"]:
    f = edit["file"]
    if f not in files:
        files[f] = open(os.path.join(src, f), encoding="utf-8-sig").read().replace("\r\n", "\n")
    text, anchor = files[f], edit["anchor"]
    count = text.count(anchor)
    if count != 1:
        sys.exit(f"anchor found {count} times in {f}: {anchor.splitlines()[0].strip()}")
    rep = {"after": anchor + edit["text"], "before": edit["text"] + anchor, "replace": edit["text"]}[edit["mode"]]
    files[f] = text.replace(anchor, rep, 1)
for f, text in files.items():
    path = os.path.join(out, f)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, "w", encoding="utf-8").write(text)
print(f"ok: {len(patch['edits'])} edits, {len(files)} files")

