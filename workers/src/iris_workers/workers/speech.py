"""Speech-to-text worker (POC-001D): faster-whisper + LocalAgreement policy v3 over the Iris Worker Protocol.

    python -I -m iris_workers.workers.speech [--model large-v3-turbo] [--compute-type int8_float16]

Protocol (ADR-014):
- sends ``hello`` at once, then loads the pinned model (benchmarks/models/whisper.manifest.json, SHA-256 checked)
  and announces ``status`` ``ready`` (or ``failed``);
- receives audio frames of both streams; emits ``stt.partial`` / ``stt.final`` / ``stt.ack`` events
  (``iris_workers.workers.speech_engine``);
- requests: ``status`` (load state, queue and policy counters), ``end_segment`` ``{stream}`` (capture paused or
  stopped: finalize that stream's last words), ``flush`` (end of session: transcribe everything queued).

Only words and timings leave the worker; audio is never stored.
"""

from __future__ import annotations

import argparse
import os
import threading
from pathlib import Path
from typing import Any

import numpy as np

from iris_workers.bench import manifests
from iris_workers.protocol import AudioFrame, WorkerServer
from iris_workers.speech.local_agreement import TimedWord
from iris_workers.workers.common import REPO_ROOT, load_in_background, log, verify_sha256
from iris_workers.workers.speech_engine import SpeechConfig, SpeechEngine

VERSION = "0.1.0"
os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")
os.environ["HF_HUB_OFFLINE"] = "1"  # the worker never downloads: models are fetched explicitly beforehand


def resolve_model(name: str, models_dir: Path) -> Path:
    from faster_whisper.utils import download_model

    pinned = manifests.entry("whisper", name)
    if pinned is None:
        raise ValueError(f"{name} is not in whisper.manifest.json")
    path = Path(download_model(pinned["repo"], cache_dir=str(models_dir), local_files_only=True, revision=pinned["revision"]))
    verify_sha256(path / "model.bin", pinned["modelBinSha256"])
    return path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="large-v3-turbo")
    parser.add_argument("--compute-type", default="int8_float16")
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--models-dir", type=Path, default=REPO_ROOT / "models" / "whisper")
    parser.add_argument("--pause-commit", type=float, default=0.6)
    parser.add_argument("--beam-size", type=int, default=5)
    args = parser.parse_args(argv)

    ready = threading.Event()
    holder: dict[str, Any] = {}

    def transcriber(beam: int):
        def transcribe(audio: np.ndarray, prompt: str) -> list[TimedWord]:
            segments, _ = holder["model"].transcribe(
                audio, language="en", beam_size=beam, word_timestamps=True,
                condition_on_previous_text=False, initial_prompt=prompt or None, vad_filter=True)
            return [TimedWord(w.start, w.end, w.word) for s in segments for w in (s.words or [])]
        return transcribe

    server: WorkerServer
    engine = SpeechEngine(transcriber(args.beam_size), transcriber(1), lambda event: server.send(event),
                          SpeechConfig(pause_commit_seconds=args.pause_commit))

    def on_audio(frame: AudioFrame) -> None:
        engine.add_frame(frame.stream, frame.position, np.frombuffer(frame.samples, dtype="<f4").copy())

    def status(params: dict[str, Any], cancel: threading.Event) -> Any:
        return {"ready": ready.is_set(), "model": args.model, "compute_type": args.compute_type,
                "pause_commit_seconds": args.pause_commit, "streams": engine.stats(), "finals": engine.finals}

    def end_segment(params: dict[str, Any], cancel: threading.Event) -> Any:
        engine.end_segment(int(params["stream"]))
        return True

    def flush(params: dict[str, Any], cancel: threading.Event) -> Any:
        """Lets the processing thread finalize everything queued (it is the only one that transcribes)."""
        ready.wait()
        before = engine.finals
        engine.close_all()
        while not engine.is_idle():
            if cancel.wait(0.02):
                break
        return {"finals": engine.finals - before}

    server = WorkerServer("speech", VERSION, {"status": status, "end_segment": end_segment, "flush": flush},
                          on_audio=on_audio)

    def load() -> dict[str, Any]:
        from iris_workers.cuda_runtime import register_cuda_dll_directories

        register_cuda_dll_directories()
        from faster_whisper import WhisperModel

        path = resolve_model(args.model, args.models_dir)
        model = WhisperModel(str(path), device=args.device, compute_type=args.compute_type)
        silence = np.zeros(16_000, dtype=np.float32)
        for beam in (args.beam_size, 1):  # first calls initialize CUDA kernels; keep that out of the live path
            list(model.transcribe(silence, language="en", beam_size=beam, word_timestamps=True)[0])
        holder["model"] = model
        log(f"speech model {args.model} {args.compute_type} ready")
        return {"model": args.model, "compute_type": args.compute_type, "revision": path.name}

    def process() -> None:
        ready.wait()
        while not server.stopping.is_set():
            if not engine.step():
                engine.work_available.wait(0.05)

    load_in_background(server, load, ready)
    threading.Thread(target=process, name="stt", daemon=True).start()
    return server.serve()


if __name__ == "__main__":
    raise SystemExit(main())
