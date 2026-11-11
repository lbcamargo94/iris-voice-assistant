"""Deterministic fact guard for LLM refinements (ADR-010, decision 2026-11-11).

A refinement is only offered as a suggestion when it keeps the facts of the fast translation:
numbers, weekdays, months, relative dates, negations, mid-sentence names, glossary terms and the
number of questions. Anything else (word choice, fluency) is the LLM's job. The guard does not
check grammar, so a suggestion that passes can still be wrong: the fast version stays the default.
"""

from __future__ import annotations

import re
from collections import Counter
from collections.abc import Iterable, Sequence
from dataclasses import dataclass

from iris_workers.bench.translation_quality import find_pt_pt_markers

_WORD = re.compile(r"[\wÀ-ÿ]+(?:-[\wÀ-ÿ]+)*", re.UNICODE)
_DIGITS = re.compile(r"\d+(?:[.,:]\d+)*")

NUMBER_WORDS = frozenset({
    "zero", "dois", "duas", "três", "quatro", "cinco", "seis", "sete", "oito", "nove", "dez",
    "onze", "doze", "treze", "catorze", "quatorze", "quinze", "dezesseis", "dezessete", "dezoito",
    "dezenove", "vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta", "oitenta",
    "noventa", "cem", "cento", "duzentos", "duzentas", "trezentos", "trezentas", "quatrocentos",
    "quinhentos", "seiscentos", "setecentos", "oitocentos", "novecentos", "mil", "milhão", "milhões",
    "bilhão", "bilhões", "meia", "metade", "dobro", "dezena", "dúzia",
})
""""um"/"uma" are left out: they are mostly articles in PT-BR."""

WEEKDAYS = {
    "segunda": "seg", "segunda-feira": "seg", "terça": "ter", "terça-feira": "ter",
    "quarta": "qua", "quarta-feira": "qua", "quinta": "qui", "quinta-feira": "qui",
    "sexta": "sex", "sexta-feira": "sex", "sábado": "sab", "domingo": "dom",
}
MONTHS = frozenset({
    "janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro",
    "outubro", "novembro", "dezembro",
})
RELATIVE_TIME = frozenset({
    "ontem", "anteontem", "hoje", "amanhã", "próxima", "próximo", "próximas", "próximos",
    "passada", "passado", "passadas", "passados", "atrás",
})
NEGATIONS = frozenset({"não", "nunca", "jamais", "nem", "nenhum", "nenhuma", "ninguém", "nada"})

_SENTENCE_START = re.compile(r"(?:^|[.!?]\s+)[\"'(\[]?([\wÀ-ÿ]+)")


@dataclass(frozen=True)
class GuardResult:
    accepted: bool
    reasons: tuple[str, ...]


def _words(text: str) -> list[str]:
    return [w.casefold() for w in _WORD.findall(text)]


def _numbers(text: str) -> Counter[str]:
    found = Counter(d.replace(",", ".") for d in _DIGITS.findall(text))
    found.update(w for w in _words(text) if w in NUMBER_WORDS)
    return found


def _weekdays(text: str) -> Counter[str]:
    return Counter(WEEKDAYS[w] for w in _words(text) if w in WEEKDAYS)


def _in_set(text: str, vocabulary: frozenset[str]) -> Counter[str]:
    return Counter(w for w in _words(text) if w in vocabulary)


def _names(text: str) -> set[str]:
    """Capitalized words that do not start a sentence: likely names or products."""
    starts = {m.start(1) for m in _SENTENCE_START.finditer(text)}
    return {m.group(0) for m in _WORD.finditer(text) if m.group(0)[0].isupper() and m.start() not in starts}


def _missing_terms(text: str, terms: Iterable[str]) -> list[str]:
    lowered = text.casefold()
    return [t for t in dict.fromkeys(terms) if t.casefold() not in lowered]


def check_refinement(
    fast: str,
    refined: str,
    keep_terms: Sequence[str] = (),
    *,
    clean: bool = True,
) -> GuardResult:
    """Accept ``refined`` as a suggestion only if it keeps the facts of ``fast``."""
    reasons: list[str] = []
    if not refined.strip():
        return GuardResult(False, ("empty",))
    if not clean:
        reasons.append("unclean_output")
    ratio = len(refined) / max(len(fast), 1)
    if not 0.5 <= ratio <= 2.0:
        reasons.append("length")
    checks = (
        ("numbers", _numbers),
        ("weekdays", _weekdays),
        ("months", lambda t: _in_set(t, MONTHS)),
        ("relative_time", lambda t: _in_set(t, RELATIVE_TIME)),
        ("negation", lambda t: _in_set(t, NEGATIONS)),
    )
    for name, extract in checks:
        if extract(fast) != extract(refined):
            reasons.append(name)
    lost = {n for n in _names(fast) if n not in refined}
    added = {n for n in _names(refined) if n not in fast}
    if lost or added:
        reasons.append("names")
    if _missing_terms(refined, [t for t in keep_terms if t.casefold() in fast.casefold()]):
        reasons.append("glossary")
    if fast.count("?") != refined.count("?"):
        reasons.append("questions")
    if find_pt_pt_markers(refined) and not find_pt_pt_markers(fast):
        reasons.append("pt_pt")
    unique = tuple(dict.fromkeys(reasons))
    return GuardResult(not unique, unique)
