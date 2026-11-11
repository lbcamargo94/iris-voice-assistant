"""Prompt builders and response parsers for the local LLM tasks (pure functions, model-agnostic).

- Translation refinement (ADR-010 tier 2): rewrite the fast Opus-MT output into natural PT-BR,
  keeping glossary terms in English and adding nothing.
- Directed-question classification (ADR-013 layer 6): label a participant utterance for the user.
"""

from __future__ import annotations

import json
import re
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Literal

PROMPT_VERSION = 2
"""v1 (2026-11-10): over-applied English to unlisted words and rewrote correct sentences; the
classifier read "directed" as "addressed to someone". v2 restricts English to the listed terms,
asks to keep correct translations, and classifies by who is expected to answer, reason first."""

REFINE_SYSTEM = (
    "Você revisa traduções automáticas de reuniões de trabalho, do inglês para o português do Brasil.\n"
    "Regras:\n"
    "- Corrija apenas erros de sentido e trechos pouco naturais, como um brasileiro falaria numa reunião.\n"
    "- Se a tradução automática já estiver correta e natural, repita-a sem mudanças.\n"
    "- Use português do Brasil, nunca de Portugal.\n"
    "- Mantenha em inglês SOMENTE os termos da lista. Todo o resto fica em português, "
    "inclusive números escritos por extenso.\n"
    "- Não acrescente nem remova informação. Preserve números, nomes e expressões de quantidade aproximada.\n"
    "- Responda somente com a tradução revisada, em uma linha, sem aspas e sem comentários."
)


def build_refine_messages(source: str, fast_translation: str, keep_terms: Sequence[str]) -> list[dict[str, str]]:
    terms = ", ".join(dict.fromkeys(keep_terms)) or "(nenhum)"
    user = (
        f"Original (inglês): {source}\n"
        f"Tradução automática: {fast_translation}\n"
        f"Termos que ficam em inglês: {terms}\n"
        "Tradução revisada:"
    )
    return [{"role": "system", "content": REFINE_SYSTEM}, {"role": "user", "content": user}]


_LABEL_PREFIX = re.compile(r"^\s*(?:tradução revisada|tradução|revisada)\s*:\s*", re.IGNORECASE)


@dataclass(frozen=True)
class RefineOutput:
    text: str
    clean: bool
    """False when the model added commentary, several lines or a label that had to be stripped."""


def parse_refine_output(raw: str) -> RefineOutput:
    lines = [line.strip() for line in raw.strip().splitlines() if line.strip()]
    if not lines:
        return RefineOutput("", clean=False)
    first = lines[0]
    stripped = _LABEL_PREFIX.sub("", first)
    unquoted = stripped.strip().strip('"“”')
    clean = len(lines) == 1 and stripped == first and unquoted == stripped.strip()
    return RefineOutput(unquoted, clean=clean)


Label = Literal["directed", "not_directed", "ambiguous"]
LABELS: tuple[Label, ...] = ("directed", "not_directed", "ambiguous")

ANSWERER_TO_LABEL: dict[str, Label] = {"user": "directed", "someone_else": "not_directed", "group": "ambiguous"}

CLASSIFY_SCHEMA = {
    "type": "object",
    "properties": {
        "reason": {"type": "string"},
        "answerer": {"type": "string", "enum": list(ANSWERER_TO_LABEL)},
    },
    "required": ["reason", "answerer"],
}


def classify_system(user_name: str) -> str:
    return (
        "You help a meeting assistant decide who is expected to answer or act on an utterance.\n"
        f"The user is named {user_name}. The user's own speech is labeled 'You'; everyone else is "
        "labeled 'Participant'.\n"
        "For the last participant utterance, decide who is expected to answer or act:\n"
        f"- user: {user_name} specifically. Evidence: {user_name}'s name, a reply to what 'You' just said, "
        f"or {user_name} explicitly included (\"{user_name}, Mark, which of you...\").\n"
        "- someone_else: a specific other person. Evidence: another person's name, or continuing an exchange "
        "with that person. Also use someone_else for rhetorical questions, reported speech and statements "
        "that ask nothing.\n"
        "- group: everyone, anyone, the team, a role, or 'you guys', without singling anyone out.\n"
        "A name other than the user's means someone_else, even right after the user spoke.\n"
        'Answer with JSON: {"reason": ..., "answerer": ...}. First write the reason: one short sentence '
        "citing the evidence."
    )


def build_classify_messages(context: str, utterance: str, user_name: str) -> list[dict[str, str]]:
    history = "\n".join(
        line.strip().replace("Você:", "You:").replace("Participante:", "Participant:")
        for line in context.split(" / ") if line.strip()
    )
    user = f"Conversation so far:\n{history}\n\nLast participant utterance:\nParticipant: {utterance}"
    return [{"role": "system", "content": classify_system(user_name)}, {"role": "user", "content": user}]


@dataclass(frozen=True)
class Classification:
    label: Label | None
    reason: str
    valid: bool


def parse_classification(raw: str) -> Classification:
    try:
        data = json.loads(raw)
    except json.JSONDecodeError:
        return Classification(None, "", valid=False)
    if not isinstance(data, dict):
        return Classification(None, "", valid=False)
    reason = str(data.get("reason", ""))
    label = ANSWERER_TO_LABEL.get(data.get("answerer"))
    if label is None:
        return Classification(None, reason, valid=False)
    return Classification(label, reason, valid=True)
