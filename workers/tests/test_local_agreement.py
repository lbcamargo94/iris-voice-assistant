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


def test_forced_trim_commits_dropped_tentative_words_instead_of_losing_them() -> None:
    # Unstable output: each pass hears a different word at 0.0-0.5 s, so nothing ever agrees.
    transcriber = ScriptedTranscriber([[(0.0, 0.5, f" w{i}")] for i in range(5)])
    streamer = make(transcriber, trim_seconds=2.0, max_buffer_seconds=3.0)
    forced: list[str] = []
    for _ in range(4):
        streamer.insert_audio(second())
        forced += texts(streamer.process().committed)

    assert streamer.forced_trims == 1
    assert streamer.forced_trim_ranges == [(0.0, pytest.approx(2.0))]
    assert forced == [" w3"]  # latest tentative word inside the dropped audio is kept
    assert streamer.buffer_seconds <= 3.0


def test_silence_is_trimmed_so_it_does_not_accumulate() -> None:
    transcriber = ScriptedTranscriber([[]] * 6 + [[(0.5, 1.0, " hi")]])
    streamer = make(transcriber, silence_keep_seconds=2.0)
    for _ in range(7):
        streamer.insert_audio(second())
        update = streamer.process()

    assert max(transcriber.buffer_seconds) <= 3.0
    assert streamer.silence_trimmed_seconds == pytest.approx(4.0)
    assert streamer.forced_trims == 0
    assert update.tentative[0].start == pytest.approx(4.5)  # absolute time preserved


def test_full_agreement_is_not_mistaken_for_silence() -> None:
    hypothesis = [(0.0, 0.4, " a"), (0.5, 0.9, " b")]
    transcriber = ScriptedTranscriber([hypothesis, hypothesis, hypothesis + [(3.0, 3.4, " c")]])
    streamer = make(transcriber, silence_keep_seconds=0.5)
    for _ in range(3):
        streamer.insert_audio(second())
        streamer.process()  # pass 2 commits everything, leaving no tentative words

    assert streamer.silence_trimmed_seconds == 0.0
    assert transcriber.buffer_seconds == [1.0, 2.0, 3.0]


def test_silence_trim_waits_for_a_pause_after_the_last_committed_word() -> None:
    word = [(0.0, 1.5, " long")]
    transcriber = ScriptedTranscriber([word, word, [], [], []])  # "long" commits at pass 2
    streamer = make(transcriber, silence_keep_seconds=2.0)
    trimmed: list[float] = []
    for _ in range(5):
        streamer.insert_audio(second())
        streamer.process()
        trimmed.append(streamer.silence_trimmed_seconds)

    # Pause since the word end (1.5 s): 1.5 s at pass 3 (kept), 2.5 s at pass 4 (cut to 2.0 s).
    assert trimmed == [0.0, 0.0, 0.0, pytest.approx(2.0), pytest.approx(3.0)]
    assert streamer.buffer_seconds == pytest.approx(2.0)


def half_second() -> np.ndarray:
    return np.zeros(RATE // 2, dtype=np.float32)


def test_hybrid_mode_commits_only_on_agreement_passes() -> None:
    full = ScriptedTranscriber(
        [
            [(0.0, 0.4, " a")],  # agreement pass at 1.0 s
            [(0.0, 0.4, " a"), (1.2, 1.6, " b")],  # agreement pass at 2.0 s
        ]
    )
    partial = ScriptedTranscriber(
        [
            [(0.0, 0.3, " uh")],  # display pass at 0.5 s
            [(0.0, 0.4, " a"), (1.2, 1.5, " be")],  # display pass at 1.5 s
        ]
    )
    streamer = make(full, agreement_interval_seconds=1.0, partial_transcribe=partial)
    updates = []
    for _ in range(4):
        streamer.insert_audio(half_second())
        updates.append(streamer.process())

    # 0.5 s: display only; 1.0 s: first agreement hypothesis; 1.5 s: display only; 2.0 s: commits "a".
    assert [texts(u.committed) for u in updates] == [[], [], [], [" a"]]
    assert texts(updates[0].tentative) == [" uh"]
    assert texts(updates[2].tentative) == [" a", " be"]  # display pass never commits
    assert texts(updates[3].tentative) == [" b"]
    assert len(full.prompts) == 2 and len(partial.prompts) == 2


def test_next_pass_is_agreement_reports_hybrid_cadence() -> None:
    streamer = make(ScriptedTranscriber([[], []]), agreement_interval_seconds=1.0)
    cadence = []
    for _ in range(4):
        streamer.insert_audio(half_second())
        cadence.append(streamer.next_pass_is_agreement)
        if cadence[-1]:
            streamer.process()  # a scheduler skipped the display-only passes

    assert cadence == [False, True, False, True]


def test_display_pass_hides_already_committed_words() -> None:
    full = ScriptedTranscriber([[(0.0, 0.4, " a")], [(0.0, 0.4, " a")]])
    partial = ScriptedTranscriber([[(0.0, 0.4, " a"), (1.1, 1.4, " next")]])
    streamer = make(full, partial_transcribe=partial, agreement_interval_seconds=1.0)
    for _ in range(2):
        streamer.insert_audio(second())
        streamer.process()  # commits "a" on the second agreement pass
    streamer.insert_audio(half_second())

    update = streamer.process()

    assert update.committed == []
    assert texts(update.tentative) == [" next"]


def test_rejects_non_float32_audio() -> None:
    streamer = make(ScriptedTranscriber([]))

    with pytest.raises(ValueError):
        streamer.insert_audio(np.zeros(10, dtype=np.int16))
