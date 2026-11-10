"""POC-001A step 2: faster-whisper offline vs. LocalAgreement-2 streaming on AMI meetings.

Streaming latency uses a simulated real-time clock: audio arrives in ``--chunk-seconds`` steps; a
step starts when its audio is available *and* the previous step finished, and takes the measured
compute time. Latencies are therefore what a live session on this machine would observe.

Models are only downloaded with ``--allow-download`` (explicit downloads, AGENTS.md §2).
The report contains metrics only; ``--dump-text`` writes transcripts under the git-ignored data/.

    uv run --group bench python -m iris_workers.bench.stt --model small.en --compute-type float16
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import time
from dataclasses import asdict
from datetime import UTC, datetime
from pathlib import Path

from iris_workers import environment
from iris_workers.bench.ami import load_audio, load_reference
from iris_workers.bench.resources import ResourceSampler
from iris_workers.bench.text import normalize_words, percentile, word_error_rate
from iris_workers.cuda_runtime import register_cuda_dll_directories
from iris_workers.speech.local_agreement import LocalAgreementStreamer, TimedWord

REPO_ROOT = Path(__file__).resolve().parents[4]
SAMPLE_RATE = 16_000

os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def resolve_model(name: str, models_dir: Path, allow_download: bool) -> tuple[Path, dict[str, str]]:
    from faster_whisper.utils import _MODELS, download_model

    path = Path(download_model(name, cache_dir=str(models_dir), local_files_only=not allow_download))
    info = {
        "name": name,
        "repo": _MODELS.get(name, name),
        "revision": path.name,
        "model_bin_sha256": sha256_of(path / "model.bin"),
    }
    return path, info


def make_transcriber(model, beam_size: int, vad: bool):
    def transcribe(audio, prompt: str) -> list[TimedWord]:
        segments, _ = model.transcribe(
            audio,
            language="en",
            beam_size=beam_size,
            word_timestamps=True,
            condition_on_previous_text=False,
            initial_prompt=prompt or None,
            vad_filter=vad,
        )
        return [TimedWord(w.start, w.end, w.word) for s in segments for w in (s.words or [])]

    return transcribe


def run_offline(model, audio, beam_size: int) -> tuple[list[str], dict[str, float]]:
    started = time.perf_counter()
    segments, _ = model.transcribe(audio, language="en", beam_size=beam_size, vad_filter=True)
    text = [s.text for s in segments]
    elapsed = time.perf_counter() - started
    duration = len(audio) / SAMPLE_RATE
    return text, {"compute_seconds": round(elapsed, 2), "rtf": round(elapsed / duration, 4)}


def run_streaming(model, audio, args) -> tuple[list[TimedWord], dict[str, object]]:
    streamer = LocalAgreementStreamer(
        make_transcriber(model, args.beam_size, vad=True),
        trim_seconds=args.trim_seconds,
        max_buffer_seconds=args.max_buffer_seconds,
    )
    duration = len(audio) / SAMPLE_RATE
    step = int(args.chunk_seconds * SAMPLE_RATE)
    first_seen: dict[tuple[str, int], float] = {}
    commit_latency: list[float] = []
    first_seen_latency: list[float] = []
    step_compute: list[float] = []
    lag: list[float] = []
    clock = 0.0

    def remember(words: list[TimedWord], at: float) -> None:
        for w in words:
            first_seen.setdefault((w.key, round(w.start)), at)

    def seen_at(w: TimedWord, default: float) -> float:
        times = [first_seen[k] for d in (-1, 0, 1) if (k := (w.key, round(w.start) + d)) in first_seen]
        return min(times, default=default)

    for position in range(0, len(audio), step):
        chunk = audio[position : position + step]
        audio_time = (position + len(chunk)) / SAMPLE_RATE
        streamer.insert_audio(chunk)
        start = max(clock, audio_time)
        t0 = time.perf_counter()
        update = streamer.process()
        compute = time.perf_counter() - t0
        clock = start + compute
        step_compute.append(compute)
        lag.append(clock - audio_time)
        remember(update.committed + update.tentative, clock)
        for w in update.committed:
            commit_latency.append(clock - w.end)
            first_seen_latency.append(seen_at(w, clock) - w.end)

    final = streamer.finish()
    for w in final.committed:
        first_seen_latency.append(seen_at(w, clock) - w.end)

    def stats(values: list[float]) -> dict[str, float | None]:
        return {f"p{q}": None if (v := percentile(values, q)) is None else round(v, 3) for q in (50, 95, 99)}

    metrics: dict[str, object] = {
        "chunk_seconds": args.chunk_seconds,
        "steps": len(step_compute),
        "rtf": round(sum(step_compute) / duration, 4),
        "step_compute_seconds": stats(step_compute),
        "commit_latency_seconds": stats(commit_latency),
        "first_seen_latency_seconds": stats(first_seen_latency),
        "lag_seconds_max": round(max(lag, default=0.0), 3),
        "lag_seconds_final": round(lag[-1], 3) if lag else 0.0,
        "words_committed_at_finish": len(final.committed),
        "silence_trimmed_seconds": round(streamer.silence_trimmed_seconds, 1),
        "forced_trims": streamer.forced_trims,
        "forced_trim_ranges": [[round(a, 2), round(b, 2)] for a, b in streamer.forced_trim_ranges],
    }
    return list(streamer.committed), metrics


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", required=True, help="faster-whisper model name, e.g. small.en, large-v3-turbo")
    parser.add_argument("--compute-type", default="float16")
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--beam-size", type=int, default=5)
    parser.add_argument("--meetings", nargs="+", default=["IS1009a", "ES2004a"])
    parser.add_argument("--max-seconds", type=float, default=300.0, help="evaluate only the first N seconds")
    parser.add_argument("--mode", choices=["offline", "streaming", "both"], default="both")
    parser.add_argument("--chunk-seconds", type=float, default=1.0)
    parser.add_argument("--trim-seconds", type=float, default=15.0)
    parser.add_argument("--max-buffer-seconds", type=float, default=30.0)
    parser.add_argument("--corpus", type=Path, default=REPO_ROOT / "data" / "corpus" / "ami")
    parser.add_argument("--models-dir", type=Path, default=REPO_ROOT / "models" / "whisper")
    parser.add_argument("--out", type=Path, default=REPO_ROOT / "benchmarks" / "results" / "stt")
    parser.add_argument("--allow-download", action="store_true")
    parser.add_argument("--dump-text", action="store_true", help="write transcripts to data/bench-text (git-ignored)")
    args = parser.parse_args(argv)

    register_cuda_dll_directories()
    import ctranslate2
    import faster_whisper
    from faster_whisper import WhisperModel

    model_path, model_info = resolve_model(args.model, args.models_dir, args.allow_download)
    report: dict[str, object] = {
        "benchmark": "stt",
        "created_utc": datetime.now(UTC).isoformat(timespec="seconds"),
        "environment": environment.collect().to_dict(),
        "versions": {"faster_whisper": faster_whisper.__version__, "ctranslate2": ctranslate2.__version__},
        "model": model_info,
        "config": {k: v for k, v in vars(args).items() if k not in {"corpus", "models_dir", "out"}},
        "normalization": "lowercase, no punctuation, hyphens split, fillers removed, numbers NOT normalized",
        "meetings": [],
    }

    with ResourceSampler() as sampler:
        t0 = time.perf_counter()
        model = WhisperModel(str(model_path), device=args.device, compute_type=args.compute_type)
        report["model_load_seconds"] = round(time.perf_counter() - t0, 2)

        for meeting in args.meetings:
            audio = load_audio(args.corpus, meeting)
            if args.max_seconds:
                audio = audio[: int(args.max_seconds * SAMPLE_RATE)]
            duration = len(audio) / SAMPLE_RATE
            reference = load_reference(args.corpus, meeting)
            ref_tokens = normalize_words(w.text for w in reference.words if w.end <= duration)
            entry: dict[str, object] = {"meeting": meeting, "seconds": round(duration, 1), "reference_words": len(ref_tokens)}

            if args.mode in ("offline", "both"):
                text, metrics = run_offline(model, audio, args.beam_size)
                metrics["wer"] = round(word_error_rate(ref_tokens, normalize_words(text)), 4)
                entry["offline"] = metrics
                print(f"[{meeting}] offline   WER={metrics['wer']:.3f} RTF={metrics['rtf']:.3f}", flush=True)
                if args.dump_text:
                    _dump(args, meeting, "offline", " ".join(text))

            if args.mode in ("streaming", "both"):
                words, metrics = run_streaming(model, audio, args)
                metrics["wer"] = round(word_error_rate(ref_tokens, normalize_words(w.text for w in words)), 4)
                ranges = metrics["forced_trim_ranges"]
                metrics["reference_words_in_forced_trims"] = sum(
                    1
                    for w in reference.words
                    if w.end <= duration and any(a <= w.start and w.end <= b for a, b in ranges)
                )
                metrics["committed_words_in_forced_trims"] = sum(
                    1 for w in words if any(a <= w.start and w.end <= b for a, b in ranges)
                )
                entry["streaming"] = metrics
                print(
                    f"[{meeting}] streaming WER={metrics['wer']:.3f} RTF={metrics['rtf']:.3f} "
                    f"commit p50/p95={metrics['commit_latency_seconds']['p50']}/{metrics['commit_latency_seconds']['p95']}s "
                    f"first-seen p50/p95={metrics['first_seen_latency_seconds']['p50']}/"
                    f"{metrics['first_seen_latency_seconds']['p95']}s lag max={metrics['lag_seconds_max']}s",
                    flush=True,
                )
                if args.dump_text:
                    _dump(args, meeting, "streaming", "".join(w.text for w in words))
            report["meetings"].append(entry)

    report["resources"] = asdict(sampler.summary())
    args.out.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    chunk = "" if args.chunk_seconds == 1.0 else f"_c{args.chunk_seconds:g}"
    out_file = args.out / f"{stamp}_{args.model}_{args.compute_type}_b{args.beam_size}{chunk}.json"
    out_file.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"report: {out_file}")
    return 0


def _dump(args: argparse.Namespace, meeting: str, mode: str, text: str) -> None:
    folder = REPO_ROOT / "data" / "bench-text"
    folder.mkdir(parents=True, exist_ok=True)
    (folder / f"{meeting}_{args.model}_{args.compute_type}_{mode}.txt").write_text(text, encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(main())
