"""Text normalization for WER and simple latency statistics.

Normalization is deliberately simple and symmetric (applied to reference and hypothesis): lowercase,
drop punctuation, split hyphens, remove filled pauses that AMI annotates but Whisper usually omits.
Numbers are NOT normalized ("fifteen" vs "15"), which inflates absolute WER equally for all models;
use the numbers to compare configurations, not as an absolute quality claim.
"""

from __future__ import annotations

import math
import re
from collections.abc import Iterable, Sequence

FILLERS = frozenset(
    {"um", "umm", "uh", "uhm", "er", "erm", "hm", "hmm", "mm", "mmm", "mm-hmm", "uh-huh", "mm-mm", "uh-uh"}
)
_PUNCT = re.compile(r"[^\w'\- ]+")


def normalize_words(words: Iterable[str]) -> list[str]:
    out: list[str] = []
    for raw in words:
        token = _PUNCT.sub(" ", raw.lower())
        for piece in token.split():
            piece = piece.strip("'-")
            if not piece or piece in FILLERS:
                continue
            out.extend(p for p in piece.split("-") if p)
    return out


def word_error_rate(reference: Sequence[str], hypothesis: Sequence[str]) -> float:
    """Levenshtein WER over already-normalized token lists."""
    if not reference:
        return 0.0 if not hypothesis else 1.0
    previous = list(range(len(hypothesis) + 1))
    for i, ref_word in enumerate(reference, start=1):
        current = [i] + [0] * len(hypothesis)
        for j, hyp_word in enumerate(hypothesis, start=1):
            current[j] = min(
                previous[j] + 1,
                current[j - 1] + 1,
                previous[j - 1] + (ref_word != hyp_word),
            )
        previous = current
    return previous[-1] / len(reference)


def percentile(values: Sequence[float], q: float) -> float | None:
    """Nearest-rank percentile (q in [0, 100]); None for empty input."""
    if not values:
        return None
    ordered = sorted(values)
    rank = max(1, math.ceil(q / 100 * len(ordered)))
    return ordered[rank - 1]
