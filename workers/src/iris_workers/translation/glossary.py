"""Keep technical terms in English through machine translation (ADR-010, refinement 2026-11-10).

Each glossary term in the source is replaced by an opaque placeholder (``ZQX0``, ``ZQX1``...) that
Opus-MT copies verbatim (7/7 in the POC-001A probe; the raw term, quotes, ``__0__`` and ``[0]`` were
translated or mangled). After translation the placeholders are replaced by the original English
surface form. Opus-MT treats placeholders as masculine nouns, so the determiner right before a
feminine term ("uma feature flag", "da sprint") is corrected.

If the model drops or duplicates a placeholder, :meth:`TermProtector.restore` reports it and the
caller must fall back to an unprotected translation: a term is never silently lost.
"""

from __future__ import annotations

import re
from collections.abc import Iterable, Sequence
from dataclasses import dataclass
from typing import Literal

PLACEHOLDER_PREFIX = "ZQX"

Gender = Literal["m", "f"]


@dataclass(frozen=True)
class GlossaryEntry:
    term: str
    gender: Gender = "m"
    """Grammatical gender used in PT-BR ("a sprint", "o build")."""
    case_sensitive: bool = False
    """Acronyms such as "PR" only match in upper case."""
    noun_only: bool = False
    """Also an English verb ("to build", "we deploy"): protect only after a determiner ("the build",
    "a nightly build"), because a protected verb would leave English in the middle of the sentence."""


DEFAULT_TECH_TERMS: tuple[GlossaryEntry, ...] = (
    GlossaryEntry("pull request"),
    GlossaryEntry("PR", case_sensitive=True),
    GlossaryEntry("feature flag", "f"),
    GlossaryEntry("dead letter queue", "f"),
    GlossaryEntry("build", noun_only=True),
    GlossaryEntry("deploy", noun_only=True),
    GlossaryEntry("merge", noun_only=True),
    GlossaryEntry("sprint", "f"),
    GlossaryEntry("staging"),
    GlossaryEntry("endpoint"),
    GlossaryEntry("ticket"),
    GlossaryEntry("timeout"),
    GlossaryEntry("rollback"),
    GlossaryEntry("hotfix"),
    GlossaryEntry("bug"),
    GlossaryEntry("cache", noun_only=True),
    GlossaryEntry("dashboard"),
    GlossaryEntry("backlog"),
    GlossaryEntry("branch", "f", noun_only=True),
    GlossaryEntry("pipeline"),
    GlossaryEntry("frontend"),
    GlossaryEntry("backend"),
    GlossaryEntry("roadmap"),
)

_DETERMINERS = (
    "the", "a", "an", "this", "that", "these", "those", "our", "my", "your", "his", "her", "their", "its",
    "each", "every", "any", "no", "another", "next", "last",
)
_DETERMINER_PREFIX = rf"(?i:(?:{'|'.join(_DETERMINERS)})\s+(?:[\w-]+\s+)?)"
"""A determiner, optionally followed by one modifier ("the nightly build")."""
"""Jargon usually spoken in English in Brazilian tech meetings. Ambiguous words (release, commit,
job, query) are left out of the default list; users can add them to their own glossary."""

_MASCULINE_TO_FEMININE = {
    "o": "a", "os": "as", "um": "uma", "uns": "umas", "do": "da", "dos": "das", "no": "na", "nos": "nas",
    "ao": "à", "aos": "às", "pelo": "pela", "pelos": "pelas", "num": "numa", "dum": "duma",
    "esse": "essa", "este": "esta", "aquele": "aquela", "desse": "dessa", "deste": "desta",
    "nesse": "nessa", "neste": "nesta", "seu": "sua", "meu": "minha", "nosso": "nossa",
    "novo": "nova", "outro": "outra", "mesmo": "mesma", "primeiro": "primeira", "último": "última",
}
_FEMININE_TO_MASCULINE = {v: k for k, v in _MASCULINE_TO_FEMININE.items()}


@dataclass(frozen=True)
class ProtectedText:
    text: str
    slots: tuple[tuple[str, str, Gender], ...]
    """(placeholder, original surface form, gender) in order of appearance."""


@dataclass(frozen=True)
class RestoredText:
    text: str
    missing: tuple[str, ...]
    """Placeholders not found exactly once in the translation (caller must fall back)."""


class TermProtector:
    def __init__(self, entries: Iterable[GlossaryEntry] = DEFAULT_TECH_TERMS) -> None:
        self._entries = sorted(entries, key=lambda e: len(e.term), reverse=True)  # longest match first
        alternatives = []
        for index, entry in enumerate(self._entries):
            words = r"\s+".join(re.escape(w) for w in entry.term.split()) + "s?"
            body = words if entry.case_sensitive else f"(?i:{words})"
            prefix = f"(?P<p{index}>{_DETERMINER_PREFIX})" if entry.noun_only else ""
            alternatives.append(f"{prefix}(?P<t{index}>{body})")
        self._pattern = re.compile(rf"(?<![\w-])(?:{'|'.join(alternatives)})(?![\w-])") if alternatives else None

    def protect(self, text: str) -> ProtectedText:
        if self._pattern is None:
            return ProtectedText(text, ())
        slots: list[tuple[str, str, Gender]] = []

        def replace(match: re.Match[str]) -> str:
            name = match.lastgroup  # the term group closes last
            entry = self._entries[int(name[1:])]
            placeholder = f"{PLACEHOLDER_PREFIX}{len(slots)}"
            slots.append((placeholder, match.group(name), entry.gender))
            prefix = match.group(f"p{name[1:]}") if entry.noun_only else ""
            return f"{prefix}{placeholder}"

        return ProtectedText(self._pattern.sub(replace, text), tuple(slots))

    @staticmethod
    def restore(translation: str, slots: Sequence[tuple[str, str, Gender]]) -> RestoredText:
        missing = []
        for placeholder, surface, gender in slots:
            occurrences = re.findall(rf"(?<!\w){placeholder}(?!\w)", translation)
            if len(occurrences) != 1:
                missing.append(placeholder)
                continue
            translation = _fix_determiner(translation, placeholder, gender)
            translation = re.sub(rf"(?<!\w){placeholder}(?!\w)", lambda _: surface, translation)
        return RestoredText(translation, tuple(missing))


def _fix_determiner(text: str, placeholder: str, gender: Gender) -> str:
    table = _MASCULINE_TO_FEMININE if gender == "f" else _FEMININE_TO_MASCULINE

    def swap(match: re.Match[str]) -> str:
        word = match.group(1)
        replacement = table.get(word.lower())
        if replacement is None:
            return match.group()
        if word[0].isupper():
            replacement = replacement[0].upper() + replacement[1:]
        return f"{replacement}{match.group(2)}"

    return re.sub(rf"(?<!\w)(\w+)(\s+)(?={placeholder}(?!\w))", swap, text)
