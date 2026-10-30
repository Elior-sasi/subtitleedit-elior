"""Explicit mapping between the user's original cue text and the words sent to MFA.

The original text is never rewritten. Each whitespace-delimited token keeps its
exact character span in its cue. For alignment only, a token is normalized:
bidi control marks removed, leading/trailing punctuation stripped, casefolded,
and split on hyphens/dashes into sub-words. Every sub-word records the index of
the original token it came from, so engine timings map back without guessing.
"""
from __future__ import annotations

import difflib
import re
import unicodedata
from dataclasses import dataclass, field

_BIDI = dict.fromkeys(map(ord, "‎‏‪‫‬‭‮⁦⁧⁨⁩﻿"), None)
_TOKEN = re.compile(r"\S+")
# Formatting markup is kept in the original text but never sent to the engine.
_MARKUP = re.compile(r"<[^<>\n]{0,200}>|\{\\[^{}\n]{0,500}\}")
_HYPHENS = re.compile(r"[\-‐‑‒–—־/]+")


def _is_word_char(ch: str) -> bool:
    cat = unicodedata.category(ch)
    return cat[0] in ("L", "M", "N")


def normalize_token(token: str) -> list[str]:
    """Return alignment sub-words for one original token (may be empty)."""
    text = unicodedata.normalize("NFC", token.translate(_BIDI))
    parts = []
    for piece in _HYPHENS.split(text):
        start, end = 0, len(piece)
        while start < end and not _is_word_char(piece[start]):
            start += 1
        while end > start and not _is_word_char(piece[end - 1]):
            end -= 1
        core = piece[start:end]
        # Keep only letters, marks, digits and inner apostrophes (clitics such as it's).
        core = "".join(c for c in core if _is_word_char(c) or c in "'’")
        core = core.replace("’", "'").casefold()
        if any(_is_word_char(c) for c in core):
            parts.append(core)
    return parts


@dataclass
class Token:
    cue_id: int
    index: int
    text: str
    char_start: int
    char_end: int
    subwords: list[str]
    sub_times: list[tuple[float, float] | None] = field(default_factory=list)
    sub_oov: list[bool] = field(default_factory=list)


@dataclass
class Cue:
    id: int
    text: str
    tokens: list[Token]


def build(cues: list[dict]) -> list[Cue]:
    result = []
    for cue in cues:
        tokens = []
        masked = _MARKUP.sub(lambda m: " " * len(m.group()), cue["text"])
        for i, m in enumerate(_TOKEN.finditer(cue["text"])):
            subs = normalize_token(masked[m.start():m.end()])
            tokens.append(Token(cue["id"], i, m.group(), m.start(), m.end(), subs,
                                [None] * len(subs), [False] * len(subs)))
        result.append(Cue(cue["id"], cue["text"], tokens))
    return result


def transcript(cues: list[Cue]) -> str:
    return " ".join(s for c in cues for t in c.tokens for s in t.subwords)


def apply_engine_words(cues: list[Cue], entries: list, spn_spans: list[tuple[float, float]]) -> int:
    """Map MFA word entries (start, end, label) onto sub-words in order.

    Uses a sequence alignment on labels, so a skipped or extra engine word never
    shifts timings onto the wrong token. Returns the number of matched sub-words.
    """
    flat = [(t, k) for c in cues for t in c.tokens for k in range(len(t.subwords))]
    ours = [t.subwords[k] for t, k in flat]
    words = [(float(s), float(e), str(lbl)) for s, e, lbl in entries if str(lbl).strip()]
    theirs = [lbl.casefold() for _, _, lbl in words]
    matcher = difflib.SequenceMatcher(a=ours, b=theirs, autojunk=False)
    matched = 0
    for block in matcher.get_matching_blocks():
        for j in range(block.size):
            tok, k = flat[block.a + j]
            start, end, _ = words[block.b + j]
            tok.sub_times[k] = (start, end)
            tok.sub_oov[k] = any(s <= start + 1e-6 and end <= e + 1e-6 for s, e in spn_spans)
            matched += 1
    return matched


