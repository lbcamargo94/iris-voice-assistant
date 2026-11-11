"""Contract-test worker for the WorkerHost (ADR-014). It speaks the real protocol and can misbehave on demand.

    python -I -m iris_workers.testing.contract_worker [--protocol-version N] [--no-hello]

Methods: ``echo`` (returns params), ``sleep`` (seconds, honours cancel), ``fail`` (handler error),
``crash`` (process exits with code 3), ``freeze`` (stops answering pings), ``garbage`` (writes non-protocol bytes),
``oversize`` (announces a message above the limit), ``print`` (a stray print, which must not corrupt stdout),
``audio_stats`` (frames received so far), ``echo_audio`` (on/off: send every received frame back).
"""

from __future__ import annotations

import argparse
import os
import struct
import sys
import threading
import time
from typing import Any

from iris_workers.protocol import MAX_MESSAGE, AudioFrame, Cancelled, WorkerServer


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--protocol-version", type=int, default=1)
    parser.add_argument("--no-hello", action="store_true", help="never send hello (startup timeout test)")
    args = parser.parse_args(argv)
    if args.no_hello:
        sys.stdin.buffer.read()  # wait until the host gives up and closes stdin
        return 0

    stats = {"frames": 0, "first_position": None, "last_position": None, "last_sequence": None, "samples": 0}
    echo = {"on": False}
    server: WorkerServer

    def on_audio(frame: AudioFrame) -> None:
        stats["frames"] += 1
        stats["samples"] += frame.sample_count
        stats["first_position"] = stats["first_position"] if stats["first_position"] is not None else frame.position
        stats["last_position"] = frame.position
        stats["last_sequence"] = frame.sequence
        if echo["on"]:
            server.send(frame)

    def sleep(params: dict[str, Any], cancel: threading.Event) -> Any:
        if cancel.wait(float(params.get("seconds", 1))):
            raise Cancelled()
        return {"slept": params.get("seconds", 1)}

    def fail(params: dict[str, Any], cancel: threading.Event) -> Any:
        raise ValueError("requested failure")

    def crash(params: dict[str, Any], cancel: threading.Event) -> Any:
        os._exit(3)

    def freeze(params: dict[str, Any], cancel: threading.Event) -> Any:
        server._write_lock.acquire()  # noqa: SLF001 - test-only: no pong can be written any more
        time.sleep(3600)

    def garbage(params: dict[str, Any], cancel: threading.Event) -> Any:
        with server._write_lock:  # noqa: SLF001
            server._out.write(b"\x05\x00\x00\x00\xffjunk")  # valid length, unknown kind
            server._out.flush()
        return None

    def oversize(params: dict[str, Any], cancel: threading.Event) -> Any:
        with server._write_lock:  # noqa: SLF001
            server._out.write(struct.pack("<I", MAX_MESSAGE + 1))
            server._out.flush()
        return None

    def stray_print(params: dict[str, Any], cancel: threading.Event) -> Any:
        print("this goes to stderr, not into the protocol stream")
        return "printed"

    def set_echo(params: dict[str, Any], cancel: threading.Event) -> Any:
        echo["on"] = bool(params.get("on", True))
        return echo["on"]

    server = WorkerServer(
        "contract-test",
        "1.0",
        {
            "echo": lambda params, cancel: params,
            "sleep": sleep,
            "fail": fail,
            "crash": crash,
            "freeze": freeze,
            "garbage": garbage,
            "oversize": oversize,
            "print": stray_print,
            "audio_stats": lambda params, cancel: dict(stats),
            "echo_audio": set_echo,
        },
        on_audio=on_audio,
        protocol_version=args.protocol_version,
    )
    return server.serve()


if __name__ == "__main__":
    raise SystemExit(main())
