"""Reference parsing, chrF and European-Portuguese (PT-PT) marker detection for the translation benchmark."""

from __future__ import annotations

import re
from collections import Counter
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class ReferenceSentence:
    id: str
    category: str
    source: str
    reference: str
    state: str
    notes: str


def parse_reference_table(markdown: str) -> list[ReferenceSentence]:
    """Rows of the `| ID | Categoria | EN | PT-BR | Estado | Notas |` table; rows marked `remover` are skipped."""
    sentences: list[ReferenceSentence] = []
    for line in markdown.splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) != 6 or not cells[0].isdigit():
            continue
        sentence = ReferenceSentence(*cells)
        if sentence.state.lower() != "remover":
            sentences.append(sentence)
    return sentences


def load_references(path: Path) -> list[ReferenceSentence]:
    return parse_reference_table(path.read_text(encoding="utf-8"))


def _char_ngrams(text: str, n: int) -> Counter[str]:
    chars = text.replace(" ", "")
    return Counter(chars[i : i + n] for i in range(len(chars) - n + 1))


def chrf(hypotheses: Sequence[str], references: Sequence[str], max_order: int = 6, beta: float = 2.0) -> float:
    """Corpus chrF (character n-grams 1..6, beta 2, whitespace ignored, case-sensitive), 0-100.

    Follows the sacrebleu chrF definition (statistics summed over sentences, precision and recall
    averaged over the orders present); no word n-grams (i.e. chrF, not chrF++).
    """
    if len(hypotheses) != len(references):
        raise ValueError("hypotheses and references must have the same length")
    matches = [0] * max_order
    hyp_total = [0] * max_order
    ref_total = [0] * max_order
    for hyp, ref in zip(hypotheses, references):
        for n in range(1, max_order + 1):
            h, r = _char_ngrams(hyp, n), _char_ngrams(ref, n)
            matches[n - 1] += sum((h & r).values())
            hyp_total[n - 1] += sum(h.values())
            ref_total[n - 1] += sum(r.values())
    precisions = [m / h for m, h in zip(matches, hyp_total) if h]
    recalls = [m / r for m, r in zip(matches, ref_total) if r]
    if not precisions or not recalls:
        return 0.0
    p, r = sum(precisions) / len(precisions), sum(recalls) / len(recalls)
    if p + r == 0:
        return 0.0
    b2 = beta * beta
    return 100 * (1 + b2) * p * r / (b2 * p + r)


_ESTAR_A = (
    r"(?:estou|estás|está|estamos|estão|estava|estavas|estávamos|estavam|estive|esteve|estiveram|estar|"
    r"fico|fica|ficam|ficar|continua|continuam|continuar|anda|andam|andar)"
)
PT_PT_MARKERS: dict[str, str] = {
    "ecrã": r"ecrãs?",
    "utilizador": r"utilizador(?:a|es|as)?",
    "ficheiro": r"ficheiros?",
    "telemóvel": r"telemóve(?:l|is)",
    "equipa": r"equipas?",
    "registo": r"registos?",
    "partilhar": r"partilh\w*",
    "planeamento": r"planeamentos?",
    "monitorização": r"monitorizaç(?:ão|ões)",
    "aceder": r"aced(?:er|e|em|ido|ia)",
    "descarregar": r"descarreg\w*",
    "connosco": r"connosco",
    "contacto": r"contact(?:o|os|ar)",
    "facto": r"factos?",
    "projecto": r"projectos?",
    "objectivo": r"objectivos?",
    "actual": r"actua(?:l|is|lmente|lizar)",
    "secção": r"secç(?:ão|ões)",
    "vós": r"vós",
    "fixe": r"fixes?",
    "autocarro": r"autocarros?",
    "pequeno-almoço": r"pequeno-almoço",
    "estar a + infinitivo": _ESTAR_A + r"\s+a\s+\w+(?:ar|er|ir)",
}
"""Unambiguous PT-PT forms. `guardar` (= salvar) is left out: it is also valid PT-BR in other senses."""

_MARKER_PATTERNS = {name: re.compile(rf"(?<!\w){pattern}(?!\w)", re.IGNORECASE) for name, pattern in PT_PT_MARKERS.items()}


def find_pt_pt_markers(text: str) -> list[str]:
    return [name for name, pattern in _MARKER_PATTERNS.items() if pattern.search(text)]
