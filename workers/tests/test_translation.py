import pytest

from iris_workers.bench.translation_quality import chrf, find_pt_pt_markers, parse_reference_table
from iris_workers.translation.ctranslate2_marian import (
    build_source_tokens,
    detokenize,
    normalize_source,
    split_sentences,
)


def test_detokenize_joins_pieces_and_restores_spaces() -> None:
    assert detokenize(["▁Ter", "minei", "▁os", "▁testes", "."]) == "Terminei os testes."


def test_normalize_source_drops_control_and_zero_width_characters() -> None:
    assert normalize_source("  Hello​\tworld\x07 \n again ") == "Hello world again"


def test_split_sentences_keeps_decimals_and_lowercase_continuations() -> None:
    assert split_sentences("Sorry, can you repeat? You broke up a little.") == [
        "Sorry, can you repeat?",
        "You broke up a little.",
    ]
    assert split_sentences("Version 2.4 fixes it. e.g. this stays") == ["Version 2.4 fixes it. e.g. this stays"]
    assert split_sentences("so um basically we need uh a way") == ["so um basically we need uh a way"]


def test_build_source_tokens_prepends_target_language_token() -> None:
    assert build_source_tokens(["▁Hi"], ">>pob<<") == [">>pob<<", "▁Hi"]
    assert build_source_tokens(["▁Hi"], None) == ["▁Hi"]


def test_parse_reference_table_reads_rows_and_skips_removed() -> None:
    table = (
        "| ID | Categoria | EN | PT-BR | Estado | Notas |\n"
        "| --- | --- | --- | --- | --- | --- |\n"
        "| 001 | status | Hello. | Olá. | rascunho | |\n"
        "| 002 | status | Bye. | Tchau. | remover | |\n"
        "| 003 | tecnico | Deploy it. | Faz o deploy. | revisado | alt: implanta |\n"
    )

    rows = parse_reference_table(table)

    assert [r.id for r in rows] == ["001", "003"]
    assert rows[1].reference == "Faz o deploy." and rows[1].notes == "alt: implanta"


def test_chrf_is_100_for_identical_text_and_lower_for_changes() -> None:
    assert chrf(["o time de plataforma"], ["o time de plataforma"]) == pytest.approx(100.0)
    partial = chrf(["a equipe da plataforma"], ["o time de plataforma"])
    assert 0 < partial < 100
    assert chrf(["xyz"], ["abc"]) == 0.0


def test_chrf_requires_aligned_inputs() -> None:
    with pytest.raises(ValueError):
        chrf(["a"], [])


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("A equipa está a trabalhar no ficheiro.", ["ficheiro", "equipa", "estar a + infinitivo"]),
        ("Pode partilhar o ecrã com o utilizador?", ["ecrã", "utilizador", "partilhar"]),
        ("A equipe está trabalhando no arquivo.", []),
        ("Ele foi a pé e voltou a correr.", []),  # "a + infinitive" outside estar/ficar/... is fine
        ("O contato do projeto atual.", []),
    ],
)
def test_pt_pt_markers(text: str, expected: list[str]) -> None:
    assert sorted(find_pt_pt_markers(text)) == sorted(expected)
