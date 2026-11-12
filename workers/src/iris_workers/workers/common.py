"""Shared pieces of the production workers: verified model files, background loading, the processing loop."""

from __future__ import annotations

import hashlib
import json
import sys
import threading
import time
import traceback
from collections.abc import Callable
from pathlib import Path
from typing import Any

from iris_workers.protocol import WorkerServer

REPO_ROOT = Path(__file__).resolve().parents[4]


class ModelVerificationError(RuntimeError):
    pass


def verify_sha256(path: Path, expected: str) -> None:
    """Checks ``path`` against the manifest SHA-256 (docs/MODEL-MANAGER.md: never load unverified artifacts).

    Hashing a 1.5 GB model on every start costs seconds on a hard disk, so the result is remembered in a marker
    next to the file and recomputed only when the file's size or modification time changes.
    """
    marker = path.with_name(path.name + ".iris-verified.json")
    stat = path.stat()
    identity = {"size": stat.st_size, "mtime_ns": stat.st_mtime_ns, "sha256": expected.lower()}
    try:
        if json.loads(marker.read_text(encoding="utf-8")) == identity:
            return
    except (OSError, ValueError):
        pass
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while block := handle.read(1 << 22):
            digest.update(block)
    if digest.hexdigest() != expected.lower():
        raise ModelVerificationError(f"{path.name}: SHA-256 {digest.hexdigest()} differs from the manifest")
    try:
        marker.write_text(json.dumps(identity), encoding="utf-8")
    except OSError:
        pass  # read-only location: verify again next time


def log(message: str) -> None:
    """Diagnostics go to stderr (stdout carries the protocol). Never log meeting content."""
    print(f"[{time.strftime('%H:%M:%S')}] {message}", file=sys.stderr, flush=True)


def load_in_background(server: WorkerServer, load: Callable[[], dict[str, Any]], ready: threading.Event) -> None:
    """Loads models after ``hello`` so pings are answered meanwhile; announces ``status`` ready or failed."""

    def run() -> None:
        started = time.perf_counter()
        try:
            details = load()
        except Exception as error:  # reported to the host, which shows it; the worker stays up to answer status
            log("model load failed:\n" + traceback.format_exc())
            server.send({"type": "status", "state": "failed", "message": f"{type(error).__name__}: {error}"})
            return
        ready.set()
        server.send({"type": "status", "state": "ready", "load_seconds": round(time.perf_counter() - started, 2), **details})

    threading.Thread(target=run, name="model-load", daemon=True).start()
