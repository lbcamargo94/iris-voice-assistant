import json
from pathlib import Path

import pytest

from iris_workers.bench.llm_dataset import parse_directed_questions
from iris_workers.llm.ollama import OllamaClient, require_loopback
from iris_workers.llm.prompts import (
    build_classify_messages,
    build_refine_messages,
    parse_classification,
    parse_refine_output,
)

REPO_ROOT = Path(__file__).resolve().parents[2]


@pytest.mark.parametrize("url", ["http://127.0.0.1:11434", "http://localhost:11434", "http://[::1]:11434"])
def test_loopback_urls_are_accepted(url: str) -> None:
    assert require_loopback(url) == url


@pytest.mark.parametrize("url", ["http://0.0.0.0:11434", "http://192.168.0.10:11434", "https://ollama.com", "http://"])
def test_non_loopback_urls_are_rejected(url: str) -> None:
    with pytest.raises(ValueError):
        require_loopback(url)
    with pytest.raises(ValueError):
        OllamaClient(url)


def test_refine_prompt_lists_terms_once_and_carries_both_texts() -> None:
    messages = build_refine_messages("Open a PR.", "Abra um PR.", ["PR", "PR"])

    assert messages[0]["role"] == "system" and "Brasil" in messages[0]["content"]
    user = messages[1]["content"]
    assert "Original (inglês): Open a PR." in user and "Tradução automática: Abra um PR." in user
    assert "Termos que ficam em inglês: PR\n" in user


def test_refine_prompt_without_terms() -> None:
    assert "Termos que ficam em inglês: (nenhum)" in build_refine_messages("Hi.", "Oi.", [])[1]["content"]


@pytest.mark.parametrize(
    ("raw", "text", "clean"),
    [
        ("O build quebrou.", "O build quebrou.", True),
        ("  O build quebrou.\n", "O build quebrou.", True),
        ("Tradução revisada: O build quebrou.", "O build quebrou.", False),
        ('"O build quebrou."', "O build quebrou.", False),
        ("O build quebrou.\nObservação: mantive build.", "O build quebrou.", False),
        ("", "", False),
    ],
)
def test_parse_refine_output(raw: str, text: str, clean: bool) -> None:
    parsed = parse_refine_output(raw)
    assert (parsed.text, parsed.clean) == (text, clean)


def test_classify_messages_map_speaker_labels_and_name() -> None:
    messages = build_classify_messages("Você: I fixed it. / Participante: Thanks.", "Can you deploy?", "Ana")

    assert "The user is named Ana" in messages[0]["content"]
    assert messages[1]["content"] == (
        "Conversation so far:\nYou: I fixed it.\nParticipant: Thanks.\n\n"
        "Last participant utterance:\nParticipant: Can you deploy?"
    )


@pytest.mark.parametrize(
    ("raw", "label", "valid"),
    [
        (json.dumps({"reason": "name", "answerer": "user"}), "directed", True),
        (json.dumps({"reason": "Mark", "answerer": "someone_else"}), "not_directed", True),
        (json.dumps({"reason": "team", "answerer": "group"}), "ambiguous", True),
        (json.dumps({"reason": "?", "answerer": "maybe"}), None, False),
        (json.dumps({"label": "directed"}), None, False),
        ("not json", None, False),
        ("[1, 2]", None, False),
    ],
)
def test_parse_classification(raw: str, label: str | None, valid: bool) -> None:
    parsed = parse_classification(raw)
    assert (parsed.label, parsed.valid) == (label, valid)


def test_directed_question_dataset_is_well_formed() -> None:
    items = parse_directed_questions((REPO_ROOT / "benchmarks" / "llm" / "directed-questions.md").read_text("utf-8"))

    assert len(items) == 32
    assert len({i.id for i in items}) == 32
    assert {i.label for i in items} == {"directed", "not_directed", "ambiguous"}


def test_dataset_parser_rejects_unknown_labels() -> None:
    with pytest.raises(ValueError):
        parse_directed_questions("| 001 | ctx | Hi? | maybe | rascunho | |")
