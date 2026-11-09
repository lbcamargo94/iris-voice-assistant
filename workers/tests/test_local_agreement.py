from collections.abc import Sequence

import numpy as np
import pytest

from iris_workers.speech.local_agreement import LocalAgreementStreamer, TimedWord

RATE = 100  # small sample rate keeps test buffers tiny


class ScriptedTranscriber:
    """Returns pre-scripted hypotheses (relative timestamps) and records prompts and buffer sizes."""

    def __init__(self, hypotheses: list[list[tuple[float, float, str]]]) -> None:
        self._hypotheses = list(hypotheses)
        self.prompts: list[str] = []
        self.buffer_seconds: list[float] = []

    def __call__(self, audio: np.ndarray, prompt: str) -> Sequence[TimedWord]:
        self.prompts.append(prompt)
        self.buffer_seconds.append(len(audio) / RATE)
        return [TimedWord(s, e, t) for s, e, t in self._hypotheses.pop(0)]


def second() -> np.ndarray:
    return np.zeros(RATE, dtype=np.float32)


def make(transcriber: ScriptedTranscriber, **kwargs: float) -> LocalAgreementStreamer:
    return LocalAgreementStreamer(transcriber, sample_rate=RATE, **kwargs)


def texts(words: Sequence[TimedWord]) -> list[str]:
    return [w.text for w in words]


def test_first_hypothesis_is_only_tentative() -> None:
    streamer = make(ScriptedTranscriber([[(0.0, 0.4, " Hello"), (0.5, 0.9, " world")]]))
    streamer.insert_audio(second())

    update = streamer.process()

    assert update.committed == []
    assert texts(update.tentative) == [" Hello", " world"]


def test_commits_common_prefix_of_two_consecutive_hypotheses() -> None:
    streamer = make(
        ScriptedTranscriber(
            [
                [(0.0, 0.4, " Hello"), (0.5, 0.9, " word")],
                [(0.0, 0.4, " hello,"), (0.5, 0.9, " world"), (1.0, 1.5, " today")],
            ]
        )
    )
    streamer.insert_audio(second())
    streamer.process()
    streamer.insert_audio(second())

    update = streamer.process()

    assert texts(update.committed) == [" hello,"]  # case/punctuation-insensitive agreement
    assert texts(update.tentative) == [" world", " today"]


def test_committed_words_are_never_reemitted() -> None:
    streamer = make(
        ScriptedTranscriber(
            [
                [(0.0, 0.4, " a"), (0.5, 0.9, " b")],
                [(0.0, 0.4, " a"), (0.5, 0.9, " b"), (1.0, 1.4, " c")],
                [(0.0, 0.4, " a"), (0.5, 0.9, " b"), (1.0, 1.4, " c"), (1.5, 1.9, " d")],
            ]
        )
    )
    committed: list[str] = []
    for _ in range(3):
        streamer.insert_audio(second())
        committed += texts(streamer.process().committed)
    committed += texts(streamer.finish().committed)

    assert committed == [" a", " b", " c", " d"]


def test_drops_boundary_ngram_repeating_committed_tail() -> None:
    streamer = make(
        ScriptedTranscriber(
            [
                [(0.0, 0.4, " one"), (0.5, 0.9, " two")],
                [(0.0, 0.4, " one"), (0.5, 0.9, " two"), (1.0, 1.4, " three")],
                # Model shifts timestamps and repeats "two" right after the committed boundary.
                [(0.95, 1.0, " two"), (1.0, 1.4, " three"), (1.5, 1.9, " four")],
            ]
        )
    )
    for _ in range(3):
        streamer.insert_audio(second())
        update = streamer.process()

    assert texts(streamer.committed) == [" one", " two", " three"]
    assert texts(update.tentative) == [" four"]


def test_trims_buffer_after_committed_sentence_and_prompts_with_trimmed_text() -> None:
    transcriber = ScriptedTranscriber(
        [
            [(0.0, 0.5, " Done."), (1.0, 1.5, " Next")],
            [(0.0, 0.5, " Done."), (1.0, 1.5, " Next"), (2.0, 2.5, " part")],
            # After the trim at 0.5 s, timestamps are relative to the new buffer start.
            [(0.5, 1.0, " Next"), (1.5, 2.0, " part")],
        ]
    )
    streamer = make(transcriber, trim_seconds=1.5, max_buffer_seconds=3.0)
    streamer.insert_audio(second())
    streamer.process()
    streamer.insert_audio(second())
    second_update = streamer.process()  # commits "Done. Next"; trims at the sentence end (0.5 s)
    streamer.insert_audio(second())

    update = streamer.process()

    assert texts(second_update.committed) == [" Done.", " Next"]
    assert transcriber.buffer_seconds == [1.0, 2.0, 2.5]
    assert transcriber.prompts[-1] == "Done."
    assert texts(update.committed) == [" part"]  # re-heard "Next" is not re-emitted
    assert update.committed[0].start == pytest.approx(2.0)  # absolute time after the trim offset


def test_forced_trim_is_counted_when_nothing_commits() -> None:
    transcriber = ScriptedTranscriber([[(0.0, 0.5, f" w{i}")] for i in range(5)])
    streamer = make(transcriber, trim_seconds=2.0, max_buffer_seconds=3.0)
    for _ in range(5):
        streamer.insert_audio(second())
        streamer.process()

    assert streamer.forced_trims >= 1
    assert streamer.buffer_seconds <= 3.0


def test_rejects_non_float32_audio() -> None:
    streamer = make(ScriptedTranscriber([]))

    with pytest.raises(ValueError):
        streamer.insert_audio(np.zeros(10, dtype=np.int16))
