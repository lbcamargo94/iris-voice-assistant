"""Model manifests under ``benchmarks/models/`` (docs/MODEL-MANAGER.md): pinned revisions and checksums.

Downloads must be explicit and verifiable (AGENTS.md §2). A model listed in a manifest is only used
when its checksum or digest matches; an unlisted model can still be benchmarked from local files,
but reports mark it as unpinned and it is never downloaded.
"""

from __future__ import annotations

import json
from functools import cache
from pathlib import Path
from typing import Any

MANIFEST_DIR = Path(__file__).resolve().parents[4] / "benchmarks" / "models"


class ManifestMismatch(RuntimeError):
    pass


@cache
def load(name: str, directory: Path = MANIFEST_DIR) -> dict[str, Any]:
    return json.loads((directory / f"{name}.manifest.json").read_text(encoding="utf-8"))


def entry(manifest: str, model: str, directory: Path = MANIFEST_DIR) -> dict[str, Any] | None:
    return next((m for m in load(manifest, directory)["models"] if m["name"] == model), None)


def verify_ollama_model(installed: list[dict[str, Any]], name: str) -> bool:
    """Check an Ollama model (from ``/api/tags``) against ``llm.manifest.json``. True if pinned."""
    digest = next((m.get("digest", "") for m in installed if m.get("name") == name), None)
    if digest is None:
        raise ManifestMismatch(f"{name} is not installed; pull it with scripts/start-ollama.ps1 -Pull {name}")
    pinned = entry("llm", name)
    return verify(pinned and pinned["digest"], digest, f"Ollama model {name}")


def verify(expected: str | None, actual: str, what: str) -> bool:
    """True if pinned and matching, False if unpinned. Raises on a mismatch."""
    if expected is None:
        return False
    normalized = actual.lower().removeprefix("sha256:")
    if normalized != expected.lower():
        raise ManifestMismatch(f"{what}: expected {expected}, found {normalized}")
    return True
