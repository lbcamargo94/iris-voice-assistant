"""Marian/OPUS-MT translation on CTranslate2 with SentencePiece (Opus-MT tc-big and Argos packages).

Both engines are Marian transformers: Opus-MT ships separate source/target SentencePiece models and
expects a ``>>id<<`` target-language token at the start of the source; Argos packages ship one shared
SentencePiece model and add their language token through ``add_source_bos`` in ``config.json``.
Only the source SentencePiece model is needed: output pieces are detokenized as Marian does.

``ctranslate2`` and ``sentencepiece`` are imported lazily so the pure helpers stay testable without them.
"""

from __future__ import annotations

import re
import time
import unicodedata
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

from iris_workers.translation.glossary import TermProtector

_SPACES = re.compile(r"\s+")
_ZERO_WIDTH = dict.fromkeys(map(ord, "⁠​﻿"))


def normalize_source(text: str) -> str:
    """Mirror of the Opus-MT preprocess.sh: drop control/zero-width characters, collapse spaces."""
    text = text.translate(_ZERO_WIDTH)
    text = "".join(" " if unicodedata.category(ch).startswith("C") else ch for ch in text)
    return _SPACES.sub(" ", text).strip()


_SENTENCE_END = re.compile(r"(?<=[.!?])\s+(?=[\"'(\[]?[A-Z0-9])")


def split_sentences(text: str) -> list[str]:
    """Split on . ! ? followed by a capitalized word. Marian models trained on single sentences
    silently drop everything after the first sentence (measured on the POC-001A reference set)."""
    return [part for part in _SENTENCE_END.split(text) if part]


def detokenize(pieces: Sequence[str]) -> str:
    """Marian postprocess.sh: join pieces and turn the SentencePiece word marker into spaces.

    ``SentencePieceProcessor.decode_pieces`` is not used because the CTranslate2 vocabulary can hold
    pieces unknown to the .spm model (Argos), which it would leave with a literal ``▁``.
    """
    return "".join(pieces).replace("▁", " ").strip()


def build_source_tokens(pieces: Sequence[str], target_token: str | None) -> list[str]:
    return ([target_token] if target_token else []) + list(pieces)


@dataclass(frozen=True)
class Translation:
    text: str
    seconds: float
    protected_terms: int = 0
    """Glossary terms kept in English in the output."""
    glossary_fallbacks: int = 0
    """Sentences retranslated without protection because the model lost a placeholder."""


class MarianTranslator:
    def __init__(
        self,
        model_dir: Path,
        source_spm: Path,
        *,
        protector: TermProtector | None = None,
        target_token: str | None = None,
        device: str = "cuda",
        compute_type: str = "default",
        beam_size: int = 4,
        intra_threads: int = 0,
        max_decoding_length: int = 256,
    ) -> None:
        import ctranslate2
        import sentencepiece

        self._translator = ctranslate2.Translator(
            str(model_dir), device=device, compute_type=compute_type, intra_threads=intra_threads
        )
        self._source = sentencepiece.SentencePieceProcessor(model_file=str(source_spm))
        self._protector = protector
        self._target_token = target_token
        self._beam_size = beam_size
        self._max_decoding_length = max_decoding_length

    def _translate_batch(self, sentences: Sequence[str]) -> list[str]:
        batch = [
            build_source_tokens(self._source.encode(sentence, out_type=str), self._target_token)
            for sentence in sentences
        ]
        results = self._translator.translate_batch(
            batch, beam_size=self._beam_size, max_decoding_length=self._max_decoding_length
        )
        return [detokenize(r.hypotheses[0]) for r in results]

    def translate(self, text: str) -> Translation:
        start = time.perf_counter()
        sentences = split_sentences(normalize_source(text))
        if not sentences:
            return Translation(text="", seconds=time.perf_counter() - start)
        if self._protector is None:
            output = " ".join(self._translate_batch(sentences))
            return Translation(text=output, seconds=time.perf_counter() - start)

        protected = [self._protector.protect(sentence) for sentence in sentences]
        outputs = self._translate_batch([p.text for p in protected])
        restored = [TermProtector.restore(out, p.slots) for out, p in zip(outputs, protected)]
        lost = [i for i, r in enumerate(restored) if r.missing]
        final = [r.text for r in restored]
        for i, retranslated in zip(lost, self._translate_batch([sentences[i] for i in lost]) if lost else []):
            final[i] = retranslated
        kept = sum(len(p.slots) for i, p in enumerate(protected) if i not in lost)
        return Translation(
            text=" ".join(final), seconds=time.perf_counter() - start,
            protected_terms=kept, glossary_fallbacks=len(lost),
        )
