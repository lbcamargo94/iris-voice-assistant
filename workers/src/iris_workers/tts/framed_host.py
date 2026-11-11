"""Client for TTS hosts that speak the framed stdio protocol v1 (``hosts/README.md``).

The host is a separate process from its own environment (e.g. ``hosts/kokoro/kokoro_host.py``, GPL);
this MIT code only exchanges JSON lines and PCM frames with it. Unlike the Piper CLI, each request
ends with an explicit ``end`` or ``error`` frame, so no quiet-time heuristic is needed.
"""

from __future__ import annotations

import itertools
import json
import queue
import subprocess
import threading
import time
from collections.abc import Sequence
from dataclasses import dataclass

from iris_workers.tts.piper_cli import Synthesis, pcm_seconds, sanitize_text

PROTOCOL = 1


class TtsHostError(RuntimeError):
    pass


@dataclass(frozen=True)
class Frame:
    header: dict
    payload: bytes
    received: float


class FramedTtsHost:
    def __init__(self, command: Sequence[str], *, ready_timeout_seconds: float = 120.0,
                 timeout_seconds: float = 30.0) -> None:
        self.timeout_seconds = timeout_seconds
        self._process = subprocess.Popen(
            list(command), stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        self._frames: queue.Queue[Frame | None] = queue.Queue()
        self._stderr: list[str] = []
        self._ids = itertools.count(1)
        threading.Thread(target=self._read_stdout, daemon=True).start()
        threading.Thread(target=self._read_stderr, daemon=True).start()
        ready = self._next_frame(time.perf_counter() + ready_timeout_seconds)
        if ready.header.get("type") != "ready":
            raise TtsHostError(f"expected ready, got {ready.header}")
        self.sample_rate = int(ready.header["sample_rate"])
        self.load_seconds = float(ready.header.get("load_seconds", 0.0))

    @property
    def pid(self) -> int:
        return self._process.pid

    def _read_stdout(self) -> None:
        stream = self._process.stdout
        assert stream is not None
        try:
            while line := stream.readline():
                header = json.loads(line)
                if header.get("v") != PROTOCOL:
                    raise ValueError(f"unsupported protocol version: {header.get('v')}")
                size = int(header.get("bytes", 0))
                payload = stream.read(size) if size else b""
                if len(payload) != size:
                    raise ValueError("truncated audio frame")
                self._frames.put(Frame(header, payload, time.perf_counter()))
        except (ValueError, json.JSONDecodeError) as error:
            self._stderr.append(f"protocol error: {error}")
        finally:
            self._frames.put(None)  # stream closed or corrupt

    def _read_stderr(self) -> None:
        stream = self._process.stderr
        assert stream is not None
        for line in stream:
            self._stderr.append(line.decode("utf-8", "replace").rstrip())
            del self._stderr[:-50]

    def _next_frame(self, deadline: float) -> Frame:
        remaining = deadline - time.perf_counter()
        try:
            frame = self._frames.get(timeout=max(remaining, 0.001))
        except queue.Empty:
            raise TtsHostError(f"host timed out: {self.stderr_tail()}") from None
        if frame is None:
            self._process.poll()
            raise TtsHostError(f"host stopped ({self._process.returncode}): {self.stderr_tail()}")
        return frame

    def synthesize(self, text: str) -> Synthesis:
        line = sanitize_text(text)
        if not line:
            raise ValueError("empty text")
        request_id = str(next(self._ids))
        assert self._process.stdin is not None
        start = time.perf_counter()
        try:
            self._process.stdin.write(json.dumps({"id": request_id, "text": line}).encode("utf-8") + b"\n")
            self._process.stdin.flush()
        except OSError as error:
            raise TtsHostError(f"host not accepting input: {error}; {self.stderr_tail()}") from None
        deadline = start + self.timeout_seconds
        parts: list[bytes] = []
        first_at = last_at = None
        while True:
            frame = self._next_frame(deadline)
            header = frame.header
            if header.get("id") not in (request_id, None):
                continue  # late frame of an earlier, abandoned request
            kind = header.get("type")
            if kind == "audio":
                parts.append(frame.payload)
                first_at = first_at or frame.received
                last_at = frame.received
            elif kind == "end":
                break
            elif kind == "error":
                raise TtsHostError(str(header.get("message")))
        if first_at is None or last_at is None:
            raise TtsHostError("host returned no audio")
        pcm = b"".join(parts)
        return Synthesis(pcm, first_at - start, last_at - start, pcm_seconds(len(pcm), self.sample_rate))

    def stderr_tail(self) -> str:
        return " | ".join(self._stderr[-5:])

    def close(self) -> None:
        try:
            if self._process.stdin and not self._process.stdin.closed:
                self._process.stdin.write(b'{"op": "shutdown"}\n')
                self._process.stdin.close()
        except OSError:
            pass
        try:
            self._process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self._process.kill()
            self._process.wait()

    def __enter__(self) -> FramedTtsHost:
        return self

    def __exit__(self, *_: object) -> None:
        self.close()
