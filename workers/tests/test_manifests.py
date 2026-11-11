import json
import re
from pathlib import Path

import pytest

from iris_workers.bench import manifests

REPO = Path(__file__).resolve().parents[2]
ALL_MANIFESTS = sorted([*(REPO / "benchmarks" / "models").glob("*.manifest.json"),
                        *(REPO / "benchmarks" / "corpus").glob("*.manifest.json")])
HEX64 = re.compile(r"^[0-9a-f]{64}$")
HEX40 = re.compile(r"^[0-9a-f]{40}$")


def walk(node, path=""):
    if isinstance(node, dict):
        for key, value in node.items():
            yield from walk(value, f"{path}.{key}")
            yield f"{path}.{key}", key, value
    elif isinstance(node, list):
        for index, value in enumerate(node):
            yield from walk(value, f"{path}[{index}]")


def test_manifests_exist_for_every_model_family():
    names = {p.name for p in ALL_MANIFESTS}
    assert {"whisper.manifest.json", "translation.manifest.json", "llm.manifest.json",
            "kokoro.manifest.json", "tts.manifest.json", "ami.manifest.json"} <= names


@pytest.mark.parametrize("path", ALL_MANIFESTS, ids=lambda p: p.name)
def test_manifest_checksums_revisions_and_urls_are_well_formed(path):
    data = json.loads(path.read_text(encoding="utf-8"))
    checked = 0
    for where, key, value in walk(data):
        if key in {"sha256", "modelBinSha256", "digest"}:
            assert isinstance(value, str) and HEX64.match(value), f"{path.name}{where}: {value!r}"
            checked += 1
        elif key == "revision":
            assert isinstance(value, str) and HEX40.match(value), f"{path.name}{where}: {value!r}"
        elif key in {"url", "source", "artifactUri"} and isinstance(value, str):
            assert value.startswith("https://"), f"{path.name}{where}: downloads must use HTTPS"
    assert checked, f"{path.name} pins no checksum"


def test_selected_models_are_listed():
    for name in ("whisper", "llm"):
        data = manifests.load(name)
        assert manifests.entry(name, data["selected"]) is not None


def test_verify_accepts_match_flags_unpinned_and_rejects_mismatch():
    digest = "a" * 64
    assert manifests.verify(digest, "sha256:" + digest.upper(), "x") is True
    assert manifests.verify(None, digest, "x") is False
    with pytest.raises(manifests.ManifestMismatch, match="expected"):
        manifests.verify(digest, "b" * 64, "model.bin")


def test_verify_ollama_model_uses_the_installed_digest():
    pinned = manifests.entry("llm", "qwen3:4b-instruct")
    assert manifests.verify_ollama_model([{"name": "qwen3:4b-instruct", "digest": pinned["digest"]}],
                                         "qwen3:4b-instruct") is True
    assert manifests.verify_ollama_model([{"name": "other:1b", "digest": "c" * 64}], "other:1b") is False
    with pytest.raises(manifests.ManifestMismatch):
        manifests.verify_ollama_model([{"name": "qwen3:4b-instruct", "digest": "d" * 64}], "qwen3:4b-instruct")
    with pytest.raises(manifests.ManifestMismatch, match="not installed"):
        manifests.verify_ollama_model([], "qwen3:4b-instruct")
