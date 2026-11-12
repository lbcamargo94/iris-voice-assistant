"""SpeechEngine scheduling with a deterministic fake recognizer (no model, runs in CI)."""

from collections.abc import Sequence

import numpy as np

from iris_workers.speech.local_agreement import TimedWord
from iris_workers.workers.speech_engine import MICROPHONE, SAMPLE_RATE, SYSTEM_AUDIO, SpeechConfig, SpeechEngine

FRAME = 320


class ScriptRecognizer:
    """Each sample holds its absolute stream index, so the fake knows which part of the session it hears.

    Returns every scripted word that is fully inside the buffer (stable, like a confident recognizer),
    with timestamps relative to the buffer start.
    """

    def __init__(self, words: Sequence[tuple[float, float, str]]) -> None:
        self.words = list(words)
        self.calls = 0

    def __call__(self, audio: np.ndarray, prompt: str) -> list[TimedWord]:
        self.calls += 1
        if not len(audio):
            return []
        start = float(audio[0]) / SAMPLE_RATE
        end = (float(audio[-1]) + 1) / SAMPLE_RATE
        return [TimedWord(s - start, e - start, t) for s, e, t in self.words if s >= start - 1e-6 and e <= end - 0.05]


def frames(start: float, seconds: float):
    first = int(start * SAMPLE_RATE)
    for pos in range(first, first + int(seconds * SAMPLE_RATE), FRAME):
        yield pos, np.arange(pos, pos + FRAME, dtype=np.float32)


def run(engine: SpeechEngine) -> None:
    while engine.step():
        pass


def make(words, **config) -> tuple[SpeechEngine, list[dict]]:
    events: list[dict] = []
    recognizer = ScriptRecognizer(words)
    return SpeechEngine(recognizer, recognizer, events.append, SpeechConfig(**config)), events


SCRIPT = [(0.2, 0.5, " Hello"), (0.5, 0.9, " there."), (1.6, 1.9, " How"), (1.9, 2.1, " are"), (2.1, 2.5, " you?")]


def finals(events, stream=None):
    return [e for e in events if e["type"] == "stt.final" and (stream is None or e["stream"] == stream)]


def test_sentences_become_finals_with_session_positions():
    engine, events = make(SCRIPT)
    for pos, samples in frames(0, 4.0):
        engine.add_frame(SYSTEM_AUDIO, pos, samples)
        run(engine)

    got = finals(events)
    assert [f["text"] for f in got] == ["Hello there.", "How are you?"]
    assert (got[0]["start"], got[0]["end"]) == (int(0.2 * SAMPLE_RATE), int(0.9 * SAMPLE_RATE))
    assert got[1]["start"] == int(1.6 * SAMPLE_RATE)
    assert not any(f["forced"] for f in got)
    partials = [e for e in events if e["type"] == "stt.partial"]
    assert any("Hello" in p["text"] for p in partials)
    assert [p["revision"] for p in partials] == sorted(p["revision"] for p in partials)


def test_pause_commit_finalizes_a_sentence_sooner_than_two_agreements():
    short = [(0.0, 0.15, " Ship"), (0.15, 0.3, " it."), (2.6, 2.9, " Done.")]  # sentence, then silence
    with_pause, events_pause = make(short, pause_commit_seconds=0.6)
    without, events_plain = make(short, pause_commit_seconds=0.0)

    def first_final_time(engine, events):
        for pos, samples in frames(0, 4.0):
            engine.add_frame(SYSTEM_AUDIO, pos, samples)
            run(engine)
            if finals(events):
                return pos / SAMPLE_RATE
        return None

    assert first_final_time(with_pause, events_pause) < first_final_time(without, events_plain)


def test_ack_moves_to_the_end_of_finalized_speech():
    engine, events = make(SCRIPT)
    for pos, samples in frames(0, 4.0):
        engine.add_frame(SYSTEM_AUDIO, pos, samples)
        run(engine)
    acks = [e["replay_from"] for e in events if e["type"] == "stt.ack"]
    assert acks == sorted(acks)
    assert acks[-1] >= int(2.5 * SAMPLE_RATE)  # everything up to the last final can be dropped by the host


def test_system_audio_is_processed_before_the_microphone():
    calls = []
    engine, _ = make([])
    engine._transcribe = engine._partial = lambda audio, prompt: calls.append(int(audio[0])) or []
    for (pos, s) in frames(0, 0.5):
        engine.add_frame(MICROPHONE, pos, s + 1_000_000)
    for (pos, s) in frames(0, 0.5):
        engine.add_frame(SYSTEM_AUDIO, pos, s)
    engine.step()
    assert calls == [0]  # the system chunk went first although the microphone queued earlier


def test_microphone_only_gets_agreement_passes():
    engine, _ = make(SCRIPT)
    for pos, samples in frames(0, 3.0):
        engine.add_frame(MICROPHONE, pos, samples)
        run(engine)
    stats = engine.stats()[str(MICROPHONE)]
    assert stats["skipped_display_passes"] >= 2


def test_display_passes_are_skipped_when_behind():
    engine, _ = make(SCRIPT)
    for pos, samples in frames(0, 3.0):  # 3 s queued at once: the worker is behind
        engine.add_frame(SYSTEM_AUDIO, pos, samples)
    run(engine)
    assert engine.stats()[str(SYSTEM_AUDIO)]["skipped_display_passes"] >= 1


def test_a_position_jump_finalizes_the_previous_segment_without_losing_words():
    words = [(0.2, 0.6, " unfinished"), (0.6, 1.0, " thought"), (10.2, 10.5, " Back"), (10.5, 10.9, " again.")]
    engine, events = make(words)
    for pos, samples in frames(0, 1.5):
        engine.add_frame(SYSTEM_AUDIO, pos, samples)
    for pos, samples in frames(10.0, 1.5):  # after a pause: positions jump 8.5 s
        engine.add_frame(SYSTEM_AUDIO, pos, samples)
    run(engine)
    engine.flush()

    texts = [f["text"] for f in finals(events)]
    assert texts == ["unfinished thought", "Back again."]
    assert finals(events)[0]["forced"]          # closed by the jump, not by punctuation
    assert finals(events)[1]["start"] == int(10.2 * SAMPLE_RATE)


def test_flush_transcribes_queued_audio_and_closes_open_sentences():
    engine, events = make([(0.1, 0.4, " last"), (0.4, 0.7, " words")])
    for pos, samples in frames(0, 0.8):
        engine.add_frame(SYSTEM_AUDIO, pos, samples)
    assert engine.flush() == 1
    assert [f["text"] for f in finals(events)] == ["last words"]
    assert engine.stats()[str(SYSTEM_AUDIO)]["segments"] == 0


def test_backlog_beyond_the_limit_is_skipped_and_reported():
    engine, _ = make([], max_backlog_seconds=1.0)
    for pos, samples in frames(0, 3.0):
        engine.add_frame(SYSTEM_AUDIO, pos, samples)
    stats = engine.stats()[str(SYSTEM_AUDIO)]
    assert stats["queued_seconds"] <= 1.0
    assert stats["backlog_skipped_seconds"] >= 2.0 - 0.02
