"""Minimal client for a local Ollama server (stdlib HTTP, streaming chat).

Only loopback URLs are accepted: prompts contain meeting content and must never leave the machine
(AGENTS.md section 2, docs/SECURITY.md).
"""

from __future__ import annotations

import ipaddress
import json
import time
import urllib.error
import urllib.request
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Any
from urllib.parse import urlparse

DEFAULT_URL = "http://127.0.0.1:11434"


class OllamaError(RuntimeError):
    pass


def require_loopback(url: str) -> str:
    host = urlparse(url).hostname or ""
    if host == "localhost":
        return url
    try:
        if ipaddress.ip_address(host).is_loopback:
            return url
    except ValueError:
        pass
    raise ValueError(f"Ollama URL must be loopback, got {url!r}")


@dataclass(frozen=True)
class ChatResult:
    text: str
    first_token_seconds: float | None
    total_seconds: float
    prompt_tokens: int
    output_tokens: int
    load_seconds: float
    """Model load time reported by Ollama (0 when the model was already resident)."""


class OllamaClient:
    def __init__(self, base_url: str = DEFAULT_URL, timeout_seconds: float = 120.0) -> None:
        self._base = require_loopback(base_url).rstrip("/")
        self._timeout = timeout_seconds
        # Never route prompts through a system/env proxy (urllib honors HTTP_PROXY and Windows settings).
        self._opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def _request(self, path: str, payload: Mapping[str, Any] | None = None):
        data = None if payload is None else json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(
            self._base + path, data=data, headers={"Content-Type": "application/json"},
            method="GET" if payload is None else "POST",
        )
        try:
            return self._opener.open(request, timeout=self._timeout)
        except urllib.error.HTTPError as error:
            raise OllamaError(f"{path}: HTTP {error.code} {error.read().decode('utf-8', 'replace')}") from error
        except urllib.error.URLError as error:
            raise OllamaError(f"{path}: Ollama not reachable at {self._base} ({error.reason})") from error

    def version(self) -> str:
        with self._request("/api/version") as response:
            return json.load(response)["version"]

    def list_models(self) -> list[dict[str, Any]]:
        with self._request("/api/tags") as response:
            return json.load(response).get("models", [])

    def running_models(self) -> list[dict[str, Any]]:
        with self._request("/api/ps") as response:
            return json.load(response).get("models", [])

    def show(self, model: str) -> dict[str, Any]:
        with self._request("/api/show", {"model": model}) as response:
            return json.load(response)

    def unload(self, model: str) -> None:
        with self._request("/api/generate", {"model": model, "keep_alive": 0}) as response:
            response.read()

    def chat(
        self,
        model: str,
        messages: Sequence[Mapping[str, str]],
        *,
        options: Mapping[str, Any] | None = None,
        response_format: Mapping[str, Any] | str | None = None,
        keep_alive: str | int = "10m",
    ) -> ChatResult:
        payload: dict[str, Any] = {
            "model": model, "messages": list(messages), "stream": True,
            "options": dict(options or {}), "keep_alive": keep_alive,
        }
        if response_format is not None:
            payload["format"] = response_format
        start = time.perf_counter()
        first: float | None = None
        parts: list[str] = []
        final: dict[str, Any] = {}
        with self._request("/api/chat", payload) as response:
            for line in response:
                if not line.strip():
                    continue
                chunk = json.loads(line)
                if "error" in chunk:
                    raise OllamaError(chunk["error"])
                content = chunk.get("message", {}).get("content", "")
                if content:
                    if first is None:
                        first = time.perf_counter() - start
                    parts.append(content)
                if chunk.get("done"):
                    final = chunk
        return ChatResult(
            text="".join(parts),
            first_token_seconds=first,
            total_seconds=time.perf_counter() - start,
            prompt_tokens=int(final.get("prompt_eval_count", 0)),
            output_tokens=int(final.get("eval_count", 0)),
            load_seconds=final.get("load_duration", 0) / 1e9,
        )
