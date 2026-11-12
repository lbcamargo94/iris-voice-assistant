"""Streaming STT scheduling for two streams (ADR-008), independent of the model and of the protocol.

``SpeechEngine`` takes canonical 20 ms frames (16 kHz mono float32), runs LocalAgreement per stream and emits
events through ``emit``:

- ``stt.partial`` ``{stream, text, revision}``: the open sentence (committed words not yet ending a sentence plus
  the tentative tail). Replaces the previous partial of that stream.
- ``stt.final`` ``{stream, text, start, end, words, forced}``: an immutable sentence; ``start``/``end`` are
  stream positions in samples (session time, ADR-007).
- ``stt.ack`` ``{stream, replay_from}``: after a restart, re-sending audio from this position reproduces every
  word not yet final (ADR-014 decision 5). Everything before it is final or carried no words.

A jump in frame positions (pause, device loss, replay after a restart) closes the current contiguous segment:
its queued audio is still transcribed and its last words finalized before the next segment starts, so nothing
is dropped. Audio arrives on the protocol thread (``add_frame`` only queues); ``step`` runs on one worker thread.

Scheduling (policy v3, POC-001D): a pass every ``chunk_seconds`` of new audio; agreement every
``agreement_interval`` with the full beam; display-only passes use the fast transcriber and are skipped when the
stream is behind (backlog > ``late_seconds``). The system stream goes first (ADR-008 decision 6, ADR-009); the
microphone only gets agreement passes, so its text refreshes once per second. A sentence tail followed by
``pause_commit_seconds`` without words is committed at once.
"""

from __future__ import annotations

import threading
from collections import deque
from collections.abc import Callable
from dataclasses import dataclass, field
from typing import Any

import numpy as np

from iris_workers.speech.local_agreement import LocalAgreementStreamer, TimedWord, Transcriber
from iris_workers.speech.sentences import SentenceAssembler

SAMPLE_RATE = 16_000
SYSTEM_AUDIO = 1
MICROPHONE = 2


@dataclass(frozen=True)
class SpeechConfig:
    chunk_seconds: float = 0.5
    agreement_interval: float = 1.0
    late_seconds: float = 0.25
    pause_commit_seconds: float = 0.6
    trim_seconds: float = 15.0
    max_buffer_seconds: float = 30.0
    max_sentence_words: int = 30
    mic_display_passes: bool = False
    max_backlog_seconds: float = 30.0
    """Audio queued but not yet transcribed, per stream; beyond it the oldest queued audio is skipped (reported)."""


@dataclass
class _Segment:
    """Contiguous audio starting at ``base`` (stream position in samples)."""

    base: int
    expected: int
    chunks: deque[np.ndarray] = field(default_factory=deque)
    queued: int = 0
    closed: bool = False

    def take(self, samples: int) -> np.ndarray:
        parts, taken = [], 0
        while self.chunks and taken < samples:
            chunk = self.chunks.popleft()
            need = samples - taken
            if len(chunk) > need:
                self.chunks.appendleft(chunk[need:])
                chunk = chunk[:need]
            parts.append(chunk)
            taken += len(chunk)
        self.queued -= taken
        return np.concatenate(parts) if parts else np.zeros(0, dtype=np.float32)


@dataclass
class _Stream:
    kind: int
    segments: deque[_Segment] = field(default_factory=deque)
    streamer: LocalAgreementStreamer | None = None
    assembler: SentenceAssembler | None = None
    base: int = 0
    revision: int = 0
    last_partial: str = ""
    last_ack: int = -1
    last_final_end: int = 0
    skipped_display_passes: int = 0
    backlog_skipped_samples: int = 0
    pause_commits: int = 0
    forced_trims: int = 0

    @property
    def queued(self) -> int:
        return sum(s.queued for s in self.segments)


class SpeechEngine:
    def __init__(self, transcribe: Transcriber, partial_transcribe: Transcriber, emit: Callable[[dict[str, Any]], None],
                 config: SpeechConfig | None = None) -> None:
        self._transcribe = transcribe
        self._partial = partial_transcribe
        self._emit = emit
        self.config = config or SpeechConfig()
        self._streams: dict[int, _Stream] = {}
        self._lock = threading.Lock()
        self._step_lock = threading.Lock()  # one transcription pass at a time, whatever thread calls
        self.work_available = threading.Event()
        self.finals = 0
        """Sentences emitted so far."""

    # ---- input (protocol reader thread: queue only, never transcribe) -----------------------------------

    def add_frame(self, stream: int, position: int, samples: np.ndarray) -> None:
        with self._lock:
            state = self._streams.setdefault(stream, _Stream(stream))
            last = state.segments[-1] if state.segments else None
            if last is None or last.closed or position != last.expected:
                if last is not None:
                    last.closed = True
                last = _Segment(position, position)
                state.segments.append(last)
            last.chunks.append(samples)
            last.queued += len(samples)
            last.expected = position + len(samples)
            limit = int(self.config.max_backlog_seconds * SAMPLE_RATE)
            while state.queued > limit:
                head = state.segments[0]
                dropped = head.take(min(head.queued, state.queued - limit))
                state.backlog_skipped_samples += len(dropped)
                if not len(dropped):
                    break
        self.work_available.set()

    def end_segment(self, stream: int) -> None:
        """Marks the current segment complete (e.g. capture paused) so its last words are finalized."""
        with self._lock:
            state = self._streams.get(stream)
            if state and state.segments:
                state.segments[-1].closed = True
        self.work_available.set()

    # ---- processing (one worker thread) ----------------------------------------------------------------

    def step(self) -> bool:
        """Runs at most one transcription pass; system audio first. False when there was nothing to do."""
        with self._step_lock:
            return self._step()

    def close_all(self) -> None:
        """End of session: every segment is closed, so the processing loop finalizes all queued audio."""
        with self._lock:
            for state in self._streams.values():
                for segment in state.segments:
                    segment.closed = True
        self.work_available.set()

    def is_idle(self) -> bool:
        with self._lock:
            return not any(state.segments for state in self._streams.values())

    def _step(self) -> bool:
        chunk = int(self.config.chunk_seconds * SAMPLE_RATE)
        with self._lock:
            candidates = []
            for state in sorted(self._streams.values(), key=lambda s: s.kind):
                if not state.segments:
                    continue
                head = state.segments[0]
                if head.queued >= chunk or head.closed:
                    candidates.append(state)
            if not candidates:
                self.work_available.clear()
                return False
            state = candidates[0]
            head = state.segments[0]
            finishing = head.closed and head.queued < chunk
            audio = head.take(head.queued if finishing else chunk)
            backlog = state.queued / SAMPLE_RATE
            if state.streamer is None:
                self._open(state, head.base)

        streamer = state.streamer
        assert streamer is not None
        if len(audio):
            streamer.insert_audio(audio)
        if finishing:
            self._close(state, head.expected)  # transcribes the tail once more: no word is left behind
            with self._lock:
                state.segments.popleft()
            return True

        agreement = streamer.next_pass_is_agreement
        if not agreement and (backlog > self.config.late_seconds
                              or (state.kind == MICROPHONE and not self.config.mic_display_passes)):
            state.skipped_display_passes += 1
            return True
        update = streamer.process()
        self._publish(state, update.committed, update.tentative)
        return True

    def flush(self) -> int:
        """Closes every segment and transcribes all queued audio on the calling thread. Returns finals emitted."""
        self.close_all()
        before = self.finals
        while self.step():
            pass
        return self.finals - before

    def stats(self) -> dict[str, Any]:
        with self._lock:
            return {str(s.kind): {"queued_seconds": round(s.queued / SAMPLE_RATE, 3),
                                  "segments": len(s.segments),
                                  "skipped_display_passes": s.skipped_display_passes,
                                  "pause_commits": s.pause_commits + (s.streamer.pause_commits if s.streamer else 0),
                                  "forced_trims": s.forced_trims + (s.streamer.forced_trims if s.streamer else 0),
                                  "backlog_skipped_seconds": round(s.backlog_skipped_samples / SAMPLE_RATE, 3)}
                    for s in self._streams.values()}

    # ---- internals ---------------------------------------------------------------------------------------

    def _open(self, state: _Stream, base: int) -> None:
        cfg = self.config
        state.streamer = LocalAgreementStreamer(
            self._transcribe, partial_transcribe=self._partial, trim_seconds=cfg.trim_seconds,
            max_buffer_seconds=cfg.max_buffer_seconds, agreement_interval_seconds=cfg.agreement_interval,
            pause_commit_seconds=cfg.pause_commit_seconds)
        state.assembler = SentenceAssembler(cfg.max_sentence_words)
        state.base = base
        state.last_final_end = max(state.last_final_end, base)

    def _close(self, state: _Stream, end_position: int) -> None:
        assert state.streamer is not None and state.assembler is not None
        update = state.streamer.finish(transcribe_tail=True)
        self._publish(state, update.committed, [])
        rest = state.assembler.flush()
        if rest is not None:
            self._final(state, rest.text, rest.start, rest.end, rest.word_count, forced=True)
        self._partial_update(state, "")
        self._ack(state, end_position)
        state.pause_commits += state.streamer.pause_commits
        state.forced_trims += state.streamer.forced_trims
        state.streamer, state.assembler = None, None

    def _publish(self, state: _Stream, committed: list[TimedWord], tentative: list[TimedWord]) -> None:
        assert state.assembler is not None and state.streamer is not None
        for sentence in state.assembler.add(committed):
            self._final(state, sentence.text, sentence.start, sentence.end, sentence.word_count, sentence.forced)
        open_words = [w.text.strip() for w in state.assembler.pending] + [w.text.strip() for w in tentative]
        self._partial_update(state, " ".join(w for w in open_words if w))
        if state.assembler.pending_words == 0 and not tentative:
            processed = state.base + int(round(state.streamer.audio_seconds * SAMPLE_RATE))
            self._ack(state, max(state.last_final_end, processed))
        else:
            self._ack(state, state.last_final_end)

    def _final(self, state: _Stream, text: str, start: float, end: float, words: int, forced: bool) -> None:
        start_pos = state.base + int(round(start * SAMPLE_RATE))
        end_pos = state.base + int(round(end * SAMPLE_RATE))
        state.last_final_end = max(state.last_final_end, end_pos)
        self.finals += 1
        self._emit({"type": "stt.final", "stream": state.kind, "text": text, "start": start_pos, "end": end_pos,
                    "words": words, "forced": forced})

    def _partial_update(self, state: _Stream, text: str) -> None:
        if text != state.last_partial:
            state.last_partial = text
            state.revision += 1
            self._emit({"type": "stt.partial", "stream": state.kind, "text": text, "revision": state.revision})

    def _ack(self, state: _Stream, position: int) -> None:
        if position > state.last_ack:
            state.last_ack = position
            self._emit({"type": "stt.ack", "stream": state.kind, "replay_from": position})
