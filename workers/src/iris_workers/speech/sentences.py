"""Group committed STT words into sentences for translation (ADR-010: translate confirmed clauses only).

Only committed (final) words enter the assembler, so a sentence is never revised after it is emitted.
A sentence ends at terminal punctuation. Speech without punctuation is flushed after ``max_words``
so translation never waits indefinitely; no committed word is ever dropped (ADR-009).
"""

from __future__ import annotations

import re
from collections.abc import Iterable
from dataclasses import dataclass

from iris_workers.speech.local_agreement import TimedWord

_TERMINAL = re.compile(r"[.?!…][\"')\]]*$")


@dataclass(frozen=True, slots=True)
class Sentence:
    text: str
    start: float
    end: float
    """Stream time (seconds) when the last word was spoken."""
    word_count: int
    forced: bool
    """True when flushed by ``max_words`` or :meth:`SentenceAssembler.flush`, not by punctuation."""


class SentenceAssembler:
    def __init__(self, max_words: int = 30) -> None:
        if max_words < 1:
            raise ValueError("max_words must be >= 1")
        self.max_words = max_words
        self._words: list[TimedWord] = []

    @property
    def pending_words(self) -> int:
        return len(self._words)

    @property
    def pending(self) -> tuple[TimedWord, ...]:
        """Committed words of the sentence still open (no terminal punctuation yet)."""
        return tuple(self._words)

    def add(self, words: Iterable[TimedWord]) -> list[Sentence]:
        sentences = []
        for word in words:
            if not word.text.strip():
                continue
            self._words.append(word)
            if _TERMINAL.search(word.text.strip()):
                sentences.append(self._emit(forced=False))
            elif len(self._words) >= self.max_words:
                sentences.append(self._emit(forced=True))
        return sentences

    def flush(self) -> Sentence | None:
        return self._emit(forced=True) if self._words else None

    def _emit(self, *, forced: bool) -> Sentence:
        words, self._words = self._words, []
        text = " ".join(w.text.strip() for w in words)
        return Sentence(text, words[0].start, words[-1].end, len(words), forced)
