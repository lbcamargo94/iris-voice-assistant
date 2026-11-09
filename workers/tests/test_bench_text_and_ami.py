from pathlib import Path

import pytest

from iris_workers.bench.ami import load_reference, parse_words_xml
from iris_workers.bench.text import normalize_words, percentile, word_error_rate

WORDS_XML = """<?xml version="1.0" encoding="ISO-8859-1" standalone="yes"?>
<nite:root nite:id="X.A.words" xmlns:nite="http://nite.sourceforge.net/">
   <w nite:id="X.A.words0" starttime="1.0" endtime="1.5">Okay</w>
   <w nite:id="X.A.words1" starttime="1.5" endtime="1.5" punc="true">.</w>
   <vocalsound nite:id="X.A.words2" starttime="1.6" endtime="1.8" type="laugh"/>
   <w nite:id="X.A.words3">untimed</w>
   <w nite:id="X.A.words4" starttime="3.0" endtime="3.4">ready</w>
</nite:root>
"""


def test_normalize_strips_punctuation_fillers_and_splits_hyphens() -> None:
    assert normalize_words(["Um,", " Okay.", "Mm-hmm", "user-friendly", "it's", "?"]) == [
        "okay",
        "user",
        "friendly",
        "it's",
    ]


def test_wer_counts_substitution_insertion_deletion() -> None:
    assert word_error_rate(["a", "b", "c"], ["a", "x", "c", "d"]) == pytest.approx(2 / 3)
    assert word_error_rate(["a", "b"], []) == 1.0
    assert word_error_rate([], []) == 0.0


def test_percentile_nearest_rank() -> None:
    assert percentile([5, 1, 3, 2, 4], 50) == 3
    assert percentile([1, 2, 3, 4], 95) == 4
    assert percentile([], 50) is None


def test_parses_words_skipping_punctuation_and_untimed(tmp_path: Path) -> None:
    file = tmp_path / "X.A.words.xml"
    file.write_text(WORDS_XML, encoding="iso-8859-1")

    words, skipped = parse_words_xml(file)

    assert [w.text for w in words] == ["Okay", "ready"]
    assert skipped == 1


def test_reference_merges_speakers_by_start_time(tmp_path: Path) -> None:
    folder = tmp_path / "annotations" / "words"
    folder.mkdir(parents=True)
    (folder / "X.A.words.xml").write_text(WORDS_XML, encoding="iso-8859-1")
    (folder / "X.B.words.xml").write_text(
        WORDS_XML.replace("X.A", "X.B").replace('starttime="1.0"', 'starttime="2.0"').replace("Okay", "Hi"),
        encoding="iso-8859-1",
    )

    reference = load_reference(tmp_path, "X")

    assert [w.text for w in reference.words] == ["Okay", "Hi", "ready", "ready"]
    assert reference.skipped_untimed == 2
