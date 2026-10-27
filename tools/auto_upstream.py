"""Resolve official releases and prepare version metadata; never alter feature/font code."""
import argparse
import json
import os
from pathlib import Path
import re
import urllib.request

TAG = re.compile(r"v(\d+)\.(\d+)\.(\d+)(?:-(beta|rc)(\d+))?", re.I)

def api(path):
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "subtitleedit-elior-updater"}
    if os.environ.get("GH_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["GH_TOKEN"]
    with urllib.request.urlopen(urllib.request.Request("https://api.github.com/" + path, headers=headers), timeout=45) as response:
        return json.load(response)

def select_release(releases):
    eligible = [r for r in releases if not r.get("draft") and r.get("published_at") and TAG.fullmatch(r["tag_name"])]
    if not eligible:
        raise ValueError("No official versioned releases found")
    return max(eligible, key=lambda r: r["published_at"])

def resolve():
    releases = []
    for page in range(1, 11):
        batch = api(f"repos/SubtitleEdit/subtitleedit/releases?per_page=100&page={page}")
        releases.extend(batch)
        if len(batch) < 100:
            break
    else:
        raise ValueError("Release pagination limit reached; refusing incomplete selection")
    release = select_release(releases)
    tag = release["tag_name"]
    commit = api("repos/SubtitleEdit/subtitleedit/commits/" + tag)["sha"]
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Invalid upstream commit")
    matches = TAG.fullmatch(tag)
    numeric = ".".join([*matches.groups()[:3], matches.group(5) or "0"])
    repository = os.environ["GITHUB_REPOSITORY"]
    current = os.environ["GITHUB_SHA"]
    delivered = api(f"repos/{repository}/releases?per_page=100")
    already_built = any(not r["draft"] and r["tag_name"].startswith("custom-build-") and r["target_commitish"] == current and f"based on {tag}." in (r.get("body") or "") for r in delivered)
    values = {"tag": tag, "version": tag[1:], "numeric": numeric, "upstream_sha": commit, "build": str(not already_built).lower()}
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        for key, value in values.items():
            output.write(f"{key}={value}\n")
    print(json.dumps(values))

def prepare(root, tag):
    match = TAG.fullmatch(tag)
    if not match:
        raise ValueError("Invalid version tag")
    numeric = ".".join([*match.groups()[:3], match.group(5) or "0"])
    base = Path(root) / "tools/subtitleedit-rtl-fixed"
    notes_path = base / "release-notes.json"
    notes = json.loads(notes_path.read_text(encoding="utf-8-sig"))
    old = notes["upstream"]
    if not TAG.fullmatch(old):
        raise ValueError("Invalid baseline version")
    old_match = TAG.fullmatch(old)
    old_numeric = ".".join([*old_match.groups()[:3], old_match.group(5) or "0"])
    for relative in ["installer/SubtitleEditElior.iss", "installer/test-installer.ps1"]:
        path = base / relative
        text = path.read_text(encoding="utf-8-sig")
        if old[1:] not in text:
            raise ValueError("Installer baseline does not match release notes")
        path.write_text(text.replace(old[1:], tag[1:]).replace(old_numeric, numeric), encoding="utf-8-sig")
    notes["upstream"] = tag
    notes["changes"].insert(0, f"בסיס רשמי: {tag}. התוספות המותאמות נבדקות מחדש לפני פרסום; פרטי השינויים הרשמיים ב־https://github.com/SubtitleEdit/subtitleedit/releases/tag/{tag}")
    notes_path.write_text(json.dumps(notes, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["resolve", "prepare"])
    parser.add_argument("--root", default=".")
    parser.add_argument("--tag")
    args = parser.parse_args()
    if args.command == "resolve":
        resolve()
    else:
        prepare(args.root, args.tag)
