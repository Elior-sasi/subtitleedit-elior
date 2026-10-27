# Subtitle Edit — Elior

Custom Hebrew tooling for [Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit): fixed physical RTL layout, OST table, Hebrew line breaking, project guides, previous-episode context, reviewed translation and timing tools.

This repository starts with a clean history and contains only application/build sources. It contains no user settings, subtitles, media, API credentials or account exports.

## Automatic builds

The build workflow checks official published releases every six hours, including beta and RC releases. It selects the newest publication, resolves its immutable upstream commit, and skips versions already delivered from the same custom-source commit. It applies the custom patches and runs Windows regression tests, packaging exclusions and installer installation/reinstallation checks before publishing a release. A failed patch or test stops publication; the previous release remains available. Future incompatible upstream changes can require manual repair.

The updater uses this public repository, verifies successful build identity, download SHA-256 and package manifest, and installs after the application exits. A first installation from this repository is needed to switch from the former private update channel. Existing user settings and work files are excluded from release packages.

Scheduled checks are best effort: GitHub may delay schedules or disable them after prolonged inactivity. The Actions page exposes each result; a schedule being configured does not prove a build passed.

## Development

`python -m unittest discover -s tools -p 'test_auto_upstream.py'`

Build scripts download the official upstream source and apply the files under `tools/subtitleedit-rtl-fixed`. Existing font sizes are preserved. Cloud MFA deployment is optional and is never performed by the release workflow.

Upstream license: MIT, copyright Nikolaj Olsson. See LICENSE. This is an unofficial custom build.
