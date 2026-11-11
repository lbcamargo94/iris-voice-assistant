"""Parser for the directed-question set (benchmarks/llm/directed-questions.md); no bench dependencies."""

from __future__ import annotations

from dataclasses import dataclass

from iris_workers.llm.prompts import LABELS


@dataclass(frozen=True)
class LabeledUtterance:
    id: str
    context: str
    utterance: str
    label: str
    state: str


def parse_directed_questions(markdown: str) -> list[LabeledUtterance]:
    """Rows of `| ID | Contexto | Fala | Rótulo | Estado | Notas |`; rows marked `remover` are skipped."""
    items = []
    for line in markdown.splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) != 6 or not cells[0].isdigit() or cells[4].lower() == "remover":
            continue
        if cells[3] not in LABELS:
            raise ValueError(f"row {cells[0]}: unknown label {cells[3]!r}")
        items.append(LabeledUtterance(*cells[:5]))
    return items
