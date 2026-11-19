"""Vocabulary candidates in a final sentence (ADR-015).

A word is compared with the model's own vocabulary (Whisper's ~50k BPE, learned from English text): common words
are a single token, so a word that needs two or more tokens is rare. Proper nouns (capitalized after the first
word) and acronyms are kept too. Pure function with an injected ``token_count`` so it is testable without a model.
"""

from __future__ import annotations

import re
from collections.abc import Callable
from dataclasses import dataclass

_WORD = re.compile(r"[A-Za-z][A-Za-z0-9'\-]*")
_ACRONYM = re.compile(r"^[A-Z][A-Z0-9]{1,5}$")
COMMON_CAPITALIZED = frozenset({
    "I", "I'm", "I'll", "I've", "I'd", "OK", "Okay", "Mr", "Mrs", "Ms", "Dr", "Oh", "Yeah", "Yes", "No",
    "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
    "January", "February", "March", "April", "May", "June", "July", "August", "September", "October",
    "November", "December", "English", "God",
})


@dataclass(frozen=True)
class Term:
    text: str
    kind: str  # "rare", "name" or "acronym"


def extract_terms(sentence: str, token_count: Callable[[str], int], *, min_rare_length: int = 4) -> list[Term]:
    terms: dict[str, Term] = {}
    for index, match in enumerate(_WORD.finditer(sentence)):
        word = match.group(0).strip("'-")
        if word.endswith("'s"):
            word = word[:-2]
        if len(word) < 2:
            continue
        if _ACRONYM.match(word) and word not in COMMON_CAPITALIZED:
            term = Term(word, "acronym")
        elif index > 0 and word[0].isupper() and word not in COMMON_CAPITALIZED:
            term = Term(word, "name")
        elif len(word) >= min_rare_length and word.isalpha() and token_count(word.lower()) >= 2:
            term = Term(word.lower(), "rare")
        else:
            continue
        terms.setdefault(term.text.lower(), term)
    return list(terms.values())
