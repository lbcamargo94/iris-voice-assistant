"""Model-free stand-ins for the speech and translation workers, for LiveSession tests (CI, no GPU).

    python -I -m iris_workers.testing.fake_pipeline_workers speech [--crash-at SECONDS --crash-marker PATH]
    python -I -m iris_workers.testing.fake_pipeline_workers translation

The fake speech worker emits one ``stt.final`` per second of contiguous audio of each stream, named after the
second it covers (``s1-0003 with Francina`` = system stream, fourth second; "Francina" is reported as a
vocabulary term so learning can be tested), followed by ``stt.ack`` at its end. With
``--crash-at`` it exits (code 5) once, after receiving that many seconds of system audio; the marker file makes
the restarted process keep running. The fake translation worker answers ``translate`` with ``"PT: " + text``.
"""

from __future__ import annotations

import argparse
import os
import threading
import time
from pathlib import Path
from typing import Any

from iris_workers.protocol import AudioFrame, WorkerServer

RATE = 16_000


def speech(args: argparse.Namespace) -> int:
    state: dict[int, dict[str, int]] = {}
    received = {"system": 0}
    server: WorkerServer

    def on_audio(frame: AudioFrame) -> None:
        s = state.get(frame.stream)
        if s is None or frame.position != s["expected"]:
            s = state[frame.stream] = {"block_start": frame.position - frame.position % RATE, "expected": frame.position}
        s["expected"] = frame.position + frame.sample_count
        if frame.stream == 1:
            received["system"] += frame.sample_count
            if args.crash_at and received["system"] >= args.crash_at * RATE and not Path(args.crash_marker).exists():
                Path(args.crash_marker).write_text("crashed", encoding="utf-8")
                os._exit(5)
        while s["expected"] >= s["block_start"] + RATE:
            start, end = s["block_start"], s["block_start"] + RATE
            server.send({"type": "stt.partial", "stream": frame.stream, "text": "...", "revision": end})
            server.send({"type": "stt.final", "stream": frame.stream, "text": f"s{frame.stream}-{start // RATE:04d} with Francina",
                         "start": start, "end": end, "words": 3, "forced": False,
                         "terms": [{"text": "Francina", "kind": "name"}]})
            server.send({"type": "stt.ack", "stream": frame.stream, "replay_from": end})
            s["block_start"] = end

    hotwords: list[str] = []

    def set_hotwords(params: dict[str, Any], cancel: threading.Event) -> Any:
        hotwords[:] = [str(t) for t in params.get("terms", [])]
        return {"hotwords": len(hotwords)}

    server = WorkerServer("speech", "fake", {"flush": lambda p, c: {"finals": 0}, "end_segment": lambda p, c: True,
                                             "set_hotwords": set_hotwords,
                                             "status": lambda p, c: {"ready": True, "hotwords": hotwords}},
                          on_audio=on_audio)

    def announce() -> None:
        time.sleep(0.2)  # after hello, like the real worker's background model load
        server.send({"type": "status", "state": "ready", "model": "fake"})

    threading.Thread(target=announce, daemon=True).start()
    return server.serve()


def translation(args: argparse.Namespace) -> int:
    def translate(params: dict[str, Any], cancel: threading.Event) -> Any:
        return {"text": "PT: " + str(params.get("text", "")), "seconds": 0.0, "protected_terms": 0, "glossary_fallbacks": 0}

    server = WorkerServer("translation", "fake", {"translate": translate, "status": lambda p, c: {"ready": True},
                                                  "set_protected_terms": lambda p, c: {"protected": len(p.get("terms", []))}})
    threading.Thread(target=lambda: (time.sleep(0.2), server.send({"type": "status", "state": "ready"})), daemon=True).start()
    return server.serve()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("worker", choices=["speech", "translation"])
    parser.add_argument("--crash-at", type=float, default=0.0)
    parser.add_argument("--crash-marker", default="")
    args = parser.parse_args(argv)
    return speech(args) if args.worker == "speech" else translation(args)


if __name__ == "__main__":
    raise SystemExit(main())
