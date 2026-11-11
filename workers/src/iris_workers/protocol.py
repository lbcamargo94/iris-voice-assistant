"""Iris Worker Protocol v1 (ADR-014): stdio framing shared by every worker. Standard library only.

Message: ``[u32 LE length][kind u8][body]``, at most ``MAX_MESSAGE`` bytes.
- kind 0x01: UTF-8 JSON control object with a ``type`` field;
- kind 0x02: audio frame, 24-byte header ``<BBHqqI`` (kind, stream, markers, sequence, position, sample count)
  followed by float32 LE samples.

``WorkerServer`` reads stdin on one thread and runs requests on another, so ``ping`` and ``cancel`` are answered
while a request is busy. stdout carries only protocol bytes: it is taken over at start and ``sys.stdout`` is
pointed at stderr, so a stray ``print`` cannot corrupt the stream.
"""

from __future__ import annotations

import json
import queue
import struct
import sys
import threading
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any, BinaryIO

VERSION = 1
MAX_MESSAGE = 1 << 20
KIND_CONTROL = 0x01
KIND_AUDIO = 0x02
_AUDIO_HEADER = struct.Struct("<BBHqqI")  # kind, stream, markers, sequence, position, sample count = 24 bytes
AUDIO_HEADER_BYTES = _AUDIO_HEADER.size


class ProtocolError(Exception):
    """The peer violated the protocol; the connection cannot continue."""


@dataclass(frozen=True)
class AudioFrame:
    stream: int
    sequence: int
    position: int
    samples: bytes  # float32 LE, canonical 16 kHz mono (ADR-007)
    markers: int = 0

    @property
    def sample_count(self) -> int:
        return len(self.samples) // 4


def _frame(payload: bytes) -> bytes:
    if len(payload) > MAX_MESSAGE:
        raise ProtocolError(f"message of {len(payload)} bytes exceeds {MAX_MESSAGE}")
    return struct.pack("<I", len(payload)) + payload


def encode_control(message: dict[str, Any]) -> bytes:
    if not isinstance(message.get("type"), str):
        raise ValueError("control messages need a string 'type'")
    return _frame(bytes([KIND_CONTROL]) + json.dumps(message, separators=(",", ":")).encode("utf-8"))


def encode_audio(frame: AudioFrame) -> bytes:
    if len(frame.samples) % 4:
        raise ValueError("samples must be whole float32 values")
    header = _AUDIO_HEADER.pack(KIND_AUDIO, frame.stream, frame.markers, frame.sequence, frame.position, frame.sample_count)
    return _frame(header + frame.samples)


def decode(payload: bytes) -> dict[str, Any] | AudioFrame:
    if not payload:
        raise ProtocolError("empty message")
    if payload[0] == KIND_CONTROL:
        try:
            message = json.loads(payload[1:].decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise ProtocolError(f"invalid control JSON: {error}") from None
        if not isinstance(message, dict) or not isinstance(message.get("type"), str):
            raise ProtocolError("control message without a string 'type'")
        return message
    if payload[0] == KIND_AUDIO:
        if len(payload) < AUDIO_HEADER_BYTES:
            raise ProtocolError("truncated audio header")
        _, stream, markers, sequence, position, count = _AUDIO_HEADER.unpack_from(payload)
        samples = payload[AUDIO_HEADER_BYTES:]
        if len(samples) != count * 4:
            raise ProtocolError(f"audio frame declares {count} samples but carries {len(samples)} bytes")
        return AudioFrame(stream, sequence, position, bytes(samples), markers)
    raise ProtocolError(f"unknown message kind 0x{payload[0]:02X}")


def _read_exact(stream: BinaryIO, size: int) -> bytes | None:
    chunks, remaining = [], size
    while remaining:
        chunk = stream.read(remaining)
        if not chunk:
            return None if remaining == size else b""
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def read_message(stream: BinaryIO) -> dict[str, Any] | AudioFrame | None:
    """One message, or None at a clean end of stream (the host closed stdin)."""
    header = _read_exact(stream, 4)
    if header is None:
        return None
    if len(header) != 4:
        raise ProtocolError("truncated length prefix")
    (length,) = struct.unpack("<I", header)
    if not 0 < length <= MAX_MESSAGE:
        raise ProtocolError(f"message length {length} outside 1..{MAX_MESSAGE}")
    payload = _read_exact(stream, length)
    if not payload or len(payload) != length:
        raise ProtocolError("truncated message")
    return decode(payload)


class Cancelled(Exception):
    """Raised by a handler that noticed its cancel event."""


Handler = Callable[[dict[str, Any], threading.Event], Any]


class WorkerServer:
    """Serves requests over stdio. Handlers get ``(params, cancel_event)`` and return JSON-able data."""

    def __init__(self, worker: str, version: str, handlers: dict[str, Handler], *,
                 capabilities: list[str] | None = None, on_audio: Callable[[AudioFrame], None] | None = None,
                 stdin: BinaryIO | None = None, stdout: BinaryIO | None = None,
                 protocol_version: int = VERSION) -> None:
        self.worker = worker
        self.version = version
        self.handlers = handlers
        self.capabilities = capabilities or sorted(handlers)
        self.on_audio = on_audio
        self.protocol_version = protocol_version
        self._in = stdin or sys.stdin.buffer
        if stdout is None:
            stdout = sys.stdout.buffer
            sys.stdout = sys.stderr  # stdout now carries only protocol bytes
        self._out = stdout
        self._write_lock = threading.Lock()
        self._requests: queue.Queue[dict[str, Any] | None] = queue.Queue()
        self._cancels: dict[int, threading.Event] = {}
        self._cancels_lock = threading.Lock()
        self.stopping = threading.Event()

    def send(self, message: dict[str, Any] | AudioFrame) -> None:
        data = encode_audio(message) if isinstance(message, AudioFrame) else encode_control(message)
        with self._write_lock:
            self._out.write(data)
            self._out.flush()

    def serve(self) -> int:
        self.send({"type": "hello", "protocol": self.protocol_version, "worker": self.worker,
                   "version": self.version, "capabilities": self.capabilities})
        executor = threading.Thread(target=self._run_requests, name="requests", daemon=True)
        executor.start()
        try:
            while not self.stopping.is_set():
                message = read_message(self._in)
                if message is None:
                    break  # host closed stdin: exit
                if isinstance(message, AudioFrame):
                    if self.on_audio is not None:
                        self.on_audio(message)
                    continue
                self._dispatch(message)
        finally:
            self.stopping.set()
            self._requests.put(None)
            executor.join(timeout=5)
        return 0

    def _dispatch(self, message: dict[str, Any]) -> None:
        kind = message["type"]
        if kind == "ping":
            self.send({"type": "pong", "seq": message.get("seq")})
        elif kind == "request":
            with self._cancels_lock:
                self._cancels[message["id"]] = threading.Event()
            self._requests.put(message)
        elif kind == "cancel":
            with self._cancels_lock:
                event = self._cancels.get(message.get("id"))
            if event is not None:
                event.set()
        elif kind == "shutdown":
            self.stopping.set()
        else:
            self.send({"type": "error", "id": message.get("id"), "code": "unknown_type",
                       "message": f"unknown message type {kind!r}"})

    def _run_requests(self) -> None:
        while (request := self._requests.get()) is not None:
            request_id = request["id"]
            with self._cancels_lock:
                cancel = self._cancels[request_id]
            handler = self.handlers.get(request.get("method"))
            if handler is None:
                self.send({"type": "error", "id": request_id, "code": "unknown_method",
                           "message": f"unknown method {request.get('method')!r}"})
                with self._cancels_lock:
                    self._cancels.pop(request_id, None)
                continue
            try:
                if cancel.is_set():
                    raise Cancelled()
                data = handler(request.get("params") or {}, cancel)
                if cancel.is_set():
                    raise Cancelled()
                self.send({"type": "result", "id": request_id, "data": data})
            except Cancelled:
                self.send({"type": "cancelled", "id": request_id})
            except Exception as error:  # report, keep serving
                self.send({"type": "error", "id": request_id, "code": "handler_failed",
                           "message": f"{type(error).__name__}: {error}"})
            finally:
                with self._cancels_lock:
                    self._cancels.pop(request_id, None)
