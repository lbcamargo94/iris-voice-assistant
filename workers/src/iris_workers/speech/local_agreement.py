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
        agreement_interval_seconds: float = 0.0,
        partial_transcribe: Transcriber | None = None,
        pause_commit_seconds: float = 0.0,
    ) -> None:
        """
        ``agreement_interval_seconds`` > 0 enables the hybrid mode: ``process()`` may be called more
        often (e.g. every 0.5 s) to refresh the tentative text, but words are only committed by
        comparing hypotheses separated by at least this much new audio (e.g. 1 s), which keeps the
        accuracy of the slower cadence. ``partial_transcribe`` (e.g. greedy decoding) is used for the
        display-only passes; defaults to ``transcribe``.

        ``pause_commit_seconds`` > 0 (policy v3, POC-001D): on an agreement pass, a tentative tail that ends
        a sentence and is followed by at least this much audio without words is committed right away,
        instead of waiting one more agreement interval for a second, matching hypothesis.
        """
        if pause_commit_seconds < 0:
            raise ValueError("pause_commit_seconds must be >= 0")
        self._pause_commit_seconds = pause_commit_seconds
        self.pause_commits = 0
        """Number of times a sentence tail was committed on a pause (policy v3 metric)."""
        if not 0 < trim_seconds <= max_buffer_seconds:
            raise ValueError("Require 0 < trim_seconds <= max_buffer_seconds")
        if silence_keep_seconds <= 0:
            raise ValueError("silence_keep_seconds must be positive")
        if agreement_interval_seconds < 0:
            raise ValueError("agreement_interval_seconds must be >= 0")
        self._agreement_interval = agreement_interval_seconds
        self._partial_transcribe = partial_transcribe or transcribe
        self._audio_seconds = 0.0
        self._last_agreement_audio = 0.0
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
    def audio_seconds(self) -> float:
        """All audio inserted so far, in seconds since the streamer started."""
        return self._audio_seconds

    @property
    def buffer_seconds(self) -> float:
        return len(self._buffer) / self._sample_rate

    def insert_audio(self, samples: np.ndarray) -> None:
        if samples.dtype != np.float32 or samples.ndim != 1:
            raise ValueError("Expected 1-D float32 samples")
        self._buffer = np.concatenate([self._buffer, samples])
        self._audio_seconds += len(samples) / self._sample_rate

    @property
    def next_pass_is_agreement(self) -> bool:
        """False when the next ``process()`` would be a display-only pass (hybrid mode).

        Schedulers may skip display-only passes when the worker is behind real time: committed
        output is unaffected, only the refresh of tentative text is delayed.
        """
        # Small tolerance so a 1.0 s interval is reached after two 0.5 s chunks despite float error.
        return self._audio_seconds - self._last_agreement_audio >= self._agreement_interval - 1e-6

    def process(self) -> StreamingUpdate:
        """Re-transcribes the buffer and returns newly committed and current tentative words."""
        if len(self._buffer) == 0:
            return StreamingUpdate()
        agreement_pass = self.next_pass_is_agreement
        transcribe = self._transcribe if agreement_pass else self._partial_transcribe
        hypothesis = [w.shifted(self._buffer_offset) for w in transcribe(self._buffer, self._prompt())]
        hypothesis = self._drop_already_committed(hypothesis)
        if not agreement_pass:
            # Display-only pass: refresh the tentative text; agreement state is untouched.
            return StreamingUpdate(tentative=hypothesis)
        self._last_agreement_audio = self._audio_seconds
        heard_nothing_new = not hypothesis

        newly_committed: list[TimedWord] = []
        while hypothesis and self._previous and hypothesis[0].key == self._previous[0].key:
            newly_committed.append(hypothesis.pop(0))
            self._previous.pop(0)
        self._committed.extend(newly_committed)
        self._previous = hypothesis

        if self._pause_commit_seconds and hypothesis and _SENTENCE_END.search(hypothesis[-1].text.strip()):
            buffer_end = self._buffer_offset + self.buffer_seconds
            if buffer_end - hypothesis[-1].end >= self._pause_commit_seconds:
                newly_committed.extend(hypothesis)
                self._committed.extend(hypothesis)
                self._previous = []
                self.pause_commits += 1

        if heard_nothing_new:
            self._trim_silence()
        newly_committed += self._trim()
        return StreamingUpdate(committed=newly_committed, tentative=list(self._previous))

    def finish(self, transcribe_tail: bool = False) -> StreamingUpdate:
        """End of stream: commits the remaining tentative words.

        With ``transcribe_tail`` the buffer is transcribed once more (full beam) and that whole hypothesis is
        committed, so audio heard only by display passes, or not processed yet, still produces words. The
        live worker uses it when a segment closes (pause, device loss, end of session); the benchmark keeps
        the default to stay comparable with POC-001A.
        """
        if transcribe_tail and len(self._buffer):
            hypothesis = [w.shifted(self._buffer_offset) for w in self._transcribe(self._buffer, self._prompt())]
            self._previous = self._drop_already_committed(hypothesis)
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
