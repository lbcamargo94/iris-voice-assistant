"""Piper TTS adapter that talks to a separate ``piper`` process over stdio (ADR-011, POC-001A step 5).

piper-tts is GPL-3.0-or-later, so it runs from its own environment (``.tools/piper``) as a child
process and this MIT code never imports it. The process is started once with ``--output-raw``: it
keeps the voice loaded, reads one utterance per stdin line and writes 16-bit mono PCM to stdout,
flushing after each sentence.

The raw stream has no end-of-utterance marker. An utterance is taken as finished when stdout stays
quiet for ``quiet_seconds`` after audio started; the reported end time is the last byte received,
not the end of the quiet wait.
"""

from __future__ import annotations

import queue
import re
import subprocess
import threading
import time
import wave
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

_WHITESPACE = re.compile(r"\s+")
_RAW_PHONEMES = re.compile(r"\[\[|\]\]")


def sanitize_text(text: str) -> str:
    """One line per utterance: collapse newlines, and drop ``[[ ]]`` so text cannot inject raw phonemes."""
    return _WHITESPACE.sub(" ", _RAW_PHONEMES.sub(" ", text)).strip()


def build_command(python: Path, model: Path, *, length_scale: float | None = None,
                  sentence_silence: float = 0.0) -> list[str]:
    # -I isolates the child from our environment; -X utf8 still applies (PYTHONUTF8 would be ignored).
    command = [str(python), "-I", "-X", "utf8", "-m", "piper", "-m", str(model), "--output-raw",
               "--sentence-silence", str(sentence_silence)]
    if length_scale is not None:
        command += ["--length-scale", str(length_scale)]
    return command


def pcm_seconds(byte_count: int, sample_rate: int, sample_width: int = 2, channels: int = 1) -> float:
    return byte_count / (sample_rate * sample_width * channels)


def write_wav(path: Path, pcm: bytes, sample_rate: int) -> None:
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes(pcm)


@dataclass(frozen=True)
class Synthesis:
    pcm: bytes
    first_audio_seconds: float
    """From writing the text to the first PCM byte: when playback could start."""
    last_audio_seconds: float
    audio_seconds: float


class PiperError(RuntimeError):
    pass


class PiperProcess:
    def __init__(self, command: Sequence[str], sample_rate: int, *, quiet_seconds: float = 1.0,
                 timeout_seconds: float = 30.0) -> None:
        """``quiet_seconds`` must exceed the synthesis time of one sentence, because Piper flushes
        once per sentence. A framed protocol replaces this heuristic in the worker (POC-001C)."""
        self.sample_rate = sample_rate
        self.quiet_seconds = quiet_seconds
        self.timeout_seconds = timeout_seconds
        self._process = subprocess.Popen(
            list(command), stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        self._chunks: queue.Queue[tuple[float, bytes]] = queue.Queue()
        self._stderr: list[str] = []
        threading.Thread(target=self._read_stdout, daemon=True).start()
        threading.Thread(target=self._read_stderr, daemon=True).start()

    @property
    def pid(self) -> int:
        return self._process.pid

    def _read_stdout(self) -> None:
        stream = self._process.stdout
        assert stream is not None
        while chunk := stream.read1(65536):
            self._chunks.put((time.perf_counter(), chunk))

    def _read_stderr(self) -> None:
        stream = self._process.stderr
        assert stream is not None
        for line in stream:
            self._stderr.append(line.decode("utf-8", "replace").rstrip())
            del self._stderr[:-50]

    def synthesize(self, text: str) -> Synthesis:
        line = sanitize_text(text)
        if not line:
            raise ValueError("empty text")
        if self._process.poll() is not None:
            raise PiperError(f"piper exited ({self._process.returncode}): {self.stderr_tail()}")
        while not self._chunks.empty():  # stale audio from a previous, timed-out utterance
            self._chunks.get_nowait()
        assert self._process.stdin is not None
        start = time.perf_counter()
        self._process.stdin.write((line + "\n").encode("utf-8"))
        self._process.stdin.flush()
        deadline = start + self.timeout_seconds
        while True:
            try:
                first_at, data = self._chunks.get(timeout=0.05)
                break
            except queue.Empty:
                if self._process.poll() is not None:
                    raise PiperError(f"piper exited ({self._process.returncode}): {self.stderr_tail()}") from None
                if time.perf_counter() > deadline:
                    raise PiperError(f"no audio after {self.timeout_seconds} s: {self.stderr_tail()}") from None
        parts, last_at = [data], first_at
        while True:
            try:
                last_at, data = self._chunks.get(timeout=self.quiet_seconds)
            except queue.Empty:
                break
            parts.append(data)
        pcm = b"".join(parts)
        return Synthesis(pcm, first_at - start, last_at - start, pcm_seconds(len(pcm), self.sample_rate))

    def stderr_tail(self) -> str:
        return " | ".join(self._stderr[-5:])

    def close(self) -> None:
        if self._process.stdin and not self._process.stdin.closed:
            self._process.stdin.close()
        try:
            self._process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self._process.kill()
            self._process.wait()

    def __enter__(self) -> PiperProcess:
        return self

    def __exit__(self, *_: object) -> None:
        self.close()
