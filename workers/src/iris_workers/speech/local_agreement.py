"""LocalAgreement-2 streaming policy over a non-streaming STT model (ADR-008).

The recent audio buffer is re-transcribed every time new audio arrives. A word is *committed*
(final, never re-emitted) once two consecutive hypotheses agree on it as part of their common
prefix; the rest of the latest hypothesis is *tentative* (partial, may still change). The buffer is
trimmed after committed sentence ends so each call stays bounded.

Based on the policy described by Macháček, Dabre and Bojar, "Turning Whisper into Real-Time
Transcription System" (2023), reimplemented here without model coupling: the transcriber is any
callable, so the policy is testable and independent of faster-whisper (AGENTS.md §3).
"""

from __future__ import annotations

import re
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field

import numpy as np

_SENTENCE_END = re.compile(r"[.?!][\"')\]]*$")
_NON_WORD = re.compile(r"[^\w']+")


@dataclass(frozen=True, slots=True)
class TimedWord:
    """A word with start/end in seconds. Absolute stream time unless stated otherwise."""

    start: float
    end: float
    text: str

    def shifted(self, offset: float) -> TimedWord:
        return TimedWord(self.start + offset, self.end + offset, self.text)

    @property
    def key(self) -> str:
        """Comparison key: case- and punctuation-insensitive."""
        return _NON_WORD.sub("", self.text.lower())


Transcriber = Callable[[np.ndarray, str], Sequence[TimedWord]]
"""(float32 mono samples, prompt) -> words with timestamps relative to the start of the samples."""


@dataclass(frozen=True, slots=True)
class StreamingUpdate:
    committed: list[TimedWord] = field(default_factory=list)
    """Words confirmed by this step, in order. Never repeated by later updates."""
    tentative: list[TimedWord] = field(default_factory=list)
    """Current unconfirmed tail; replaces the tentative tail of the previous update."""


class LocalAgreementStreamer:
    """Turns repeated full-buffer transcriptions into committed/tentative word streams."""

    def __init__(
        self,
        transcribe: Transcriber,
        *,
        sample_rate: int = 16_000,
        trim_seconds: float = 15.0,
        max_buffer_seconds: float = 30.0,
        prompt_chars: int = 200,
        silence_keep_seconds: float = 3.0,
    ) -> None:
        if not 0 < trim_seconds <= max_buffer_seconds:
            raise ValueError("Require 0 < trim_seconds <= max_buffer_seconds")
        if silence_keep_seconds <= 0:
            raise ValueError("silence_keep_seconds must be positive")
        self._silence_keep_seconds = silence_keep_seconds
        self.silence_trimmed_seconds = 0.0
        """Audio dropped because the model found no words in it (not counted as loss)."""
        self._transcribe = transcribe
        self._sample_rate = sample_rate
        self._trim_seconds = trim_seconds
        self._max_buffer_seconds = max_buffer_seconds
        self._prompt_chars = prompt_chars
        self._buffer = np.zeros(0, dtype=np.float32)
        self._buffer_offset = 0.0
        self._committed: list[TimedWord] = []
        self._previous: list[TimedWord] = []
        self.forced_trim_ranges: list[tuple[float, float]] = []
        """(start, end) of audio dropped without a committed boundary (ADR-009 visible-loss metric)."""

    @property
    def forced_trims(self) -> int:
        return len(self.forced_trim_ranges)

    @property
    def committed(self) -> Sequence[TimedWord]:
        return self._committed

    @property
    def buffer_seconds(self) -> float:
        return len(self._buffer) / self._sample_rate

    def insert_audio(self, samples: np.ndarray) -> None:
        if samples.dtype != np.float32 or samples.ndim != 1:
            raise ValueError("Expected 1-D float32 samples")
        self._buffer = np.concatenate([self._buffer, samples])

    def process(self) -> StreamingUpdate:
        """Re-transcribes the buffer and returns newly committed and current tentative words."""
        if len(self._buffer) == 0:
            return StreamingUpdate()
        hypothesis = [w.shifted(self._buffer_offset) for w in self._transcribe(self._buffer, self._prompt())]
        hypothesis = self._drop_already_committed(hypothesis)
        heard_nothing_new = not hypothesis

        newly_committed: list[TimedWord] = []
        while hypothesis and self._previous and hypothesis[0].key == self._previous[0].key:
            newly_committed.append(hypothesis.pop(0))
            self._previous.pop(0)
        self._committed.extend(newly_committed)
        self._previous = hypothesis

        if heard_nothing_new:
            self._trim_silence()
        newly_committed += self._trim()
        return StreamingUpdate(committed=newly_committed, tentative=list(self._previous))

    def finish(self) -> StreamingUpdate:
        """End of stream: commits the remaining tentative words."""
        remaining = self._previous
        self._committed.extend(remaining)
        self._previous = []
        self._buffer = np.zeros(0, dtype=np.float32)
        return StreamingUpdate(committed=list(remaining))

    def _prompt(self) -> str:
        """Committed text that is no longer inside the buffer, as decoding context."""
        outside = [w.text.strip() for w in self._committed if w.end <= self._buffer_offset]
        return " ".join(outside)[-self._prompt_chars :]

    def _drop_already_committed(self, hypothesis: list[TimedWord]) -> list[TimedWord]:
        if not self._committed:
            return hypothesis
        last_end = self._committed[-1].end
        hypothesis = [w for w in hypothesis if w.start > last_end - 0.1]
        # The model may repeat the last committed words right at the boundary: drop the longest
        # leading n-gram (n <= 5) that equals the committed tail.
        if hypothesis and abs(hypothesis[0].start - last_end) < 1.0:
            for n in range(min(5, len(self._committed), len(hypothesis)), 0, -1):
                tail = [w.key for w in self._committed[-n:]]
                head = [w.key for w in hypothesis[:n]]
                if tail == head:
                    return hypothesis[n:]
        return hypothesis

    def _trim_silence(self) -> None:
        """The pass heard no new words: drop old audio, keeping the last ``silence_keep_seconds``.

        Whisper tends to classify a window dominated by silence as "no speech" and then misses
        short utterances inside it; keeping silence out of the buffer avoids that (POC-001A finding).
        Only acts after a pause of at least ``silence_keep_seconds`` since the last committed word,
        so ordinary pauses keep their acoustic context.
        """
        buffer_end = self._buffer_offset + self.buffer_seconds
        cut = buffer_end - self._silence_keep_seconds
        if self._committed and self._committed[-1].end > cut:
            return
        if cut > self._buffer_offset:
            self.silence_trimmed_seconds += cut - self._buffer_offset
            self._cut_at(cut)

    def _trim(self) -> list[TimedWord]:
        """Bounds the buffer. Returns tentative words force-committed by a cut without a boundary."""
        if self.buffer_seconds <= self._trim_seconds:
            return []
        inside = [w for w in self._committed if w.end > self._buffer_offset]
        sentence_ends = [w.end for w in inside if _SENTENCE_END.search(w.text.strip())]
        if sentence_ends:
            self._cut_at(sentence_ends[-1])
        elif self.buffer_seconds > self._max_buffer_seconds:
            if inside:
                self._cut_at(inside[-1].end)
            else:
                # Nothing committed for a whole window (unstable output): keep the most recent trim
                # window. Tentative words in the dropped audio are committed rather than silently
                # lost (ADR-009), and the cut is recorded so it is visible in reports.
                cut = self._buffer_offset + self.buffer_seconds - self._trim_seconds
                self.forced_trim_ranges.append((self._buffer_offset, cut))
                forced = [w for w in self._previous if w.end <= cut]
                self._previous = [w for w in self._previous if w.end > cut]
                self._committed.extend(forced)
                self._cut_at(cut)
                return forced
        return []

    def _cut_at(self, time: float) -> None:
        samples = int(round((time - self._buffer_offset) * self._sample_rate))
        samples = max(0, min(samples, len(self._buffer)))
        self._buffer = self._buffer[samples:]
        self._buffer_offset += samples / self._sample_rate
