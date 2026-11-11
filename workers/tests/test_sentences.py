import pytest

from iris_workers.speech.local_agreement import TimedWord
from iris_workers.speech.sentences import SentenceAssembler


def words(text: str, start: float = 0.0) -> list[TimedWord]:
    return [TimedWord(start + i, start + i + 0.5, f" {w}") for i, w in enumerate(text.split())]


def test_emits_sentence_at_terminal_punctuation_with_timing():
    assembler = SentenceAssembler()
    out = assembler.add(words("We shipped it. Did it work?"))
    assert [s.text for s in out] == ["We shipped it.", "Did it work?"]
    assert (out[0].start, out[0].end, out[0].word_count, out[0].forced) == (0.0, 2.5, 3, False)
    assert out[1].end == 5.5
    assert assembler.pending_words == 0


def test_sentence_can_span_several_updates():
    assembler = SentenceAssembler()
    assert assembler.add(words("So the")) == []
    out = assembler.add(words("build failed.", start=2))
    assert [s.text for s in out] == ["So the build failed."]


def test_closing_quote_or_paren_after_punctuation_still_ends():
    assert SentenceAssembler().add(words('he said "done."'))[0].text == 'he said "done."'


def test_unpunctuated_speech_is_flushed_without_loss():
    assembler = SentenceAssembler(max_words=4)
    out = assembler.add(words("one two three four five six"))
    assert [(s.text, s.forced) for s in out] == [("one two three four", True)]
    rest = assembler.flush()
    assert rest is not None and rest.text == "five six" and rest.forced
    assert assembler.flush() is None


def test_blank_words_are_ignored_and_max_words_validated():
    assert SentenceAssembler().add([TimedWord(0, 1, " ")]) == []
    with pytest.raises(ValueError):
        SentenceAssembler(max_words=0)
