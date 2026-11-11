"""POC-001A step 5: on-demand reading latency (ADR-011, RNF-015: start <= 1.5 s).

Engines: Kokoro with the Iris voice mix (``hosts/kokoro/kokoro_host.py`` in ``.tools/kokoro``,
``benchmarks/fetch-kokoro.ps1``) or Piper (``.tools/piper``, ``benchmarks/fetch-tts.ps1``). Both run as
separate processes because they depend on GPL code; nothing is downloaded here.

The texts are the fast Opus-MT + glossary translations from the latest translation report, which is
what the "Ouvir" button would read (PT-BR with technical terms kept in English).

    uv run --group bench python -m iris_workers.bench.tts                      # Kokoro, Iris voice mix
    uv run --group bench python -m iris_workers.bench.tts --engine piper --voice pt_BR-cadu-medium

Measured per utterance with the voice already loaded: time to first audio (when playback can start),
time to last audio, audio length and real-time factor. Also the cold start (process spawn + model
load + first utterance) and the host process's CPU and peak RAM. WAV samples for listening go to
``tmp/tts-samples/`` (git-ignored, temporary).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import threading
import time
from datetime import UTC, datetime
from pathlib import Path

import psutil

from iris_workers import environment
from iris_workers.bench.text import percentile
from iris_workers.tts.framed_host import FramedTtsHost
from iris_workers.tts.piper_cli import PiperProcess, build_command, write_wav

REPO_ROOT = Path(__file__).resolve().parents[4]
RESULTS = REPO_ROOT / "benchmarks" / "results"
MANIFEST = REPO_ROOT / "benchmarks" / "models" / "tts.manifest.json"
KOKORO_MANIFEST = REPO_ROOT / "benchmarks" / "models" / "kokoro.manifest.json"
KOKORO_HOST = REPO_ROOT / "hosts" / "kokoro" / "kokoro_host.py"
SAMPLE_IDS = ("005", "012", "020", "035", "037", "044")


def latest_fast_report() -> Path:
    candidates = sorted((RESULTS / "translation").glob("*_opus_pob_cuda_*_glossary.json"))
    if not candidates:
        raise FileNotFoundError("no Opus-MT glossary report; run iris_workers.bench.translation --glossary default")
    return candidates[-1]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while block := handle.read(1 << 20):
            digest.update(block)
    return digest.hexdigest()


class ChildSampler:
    """Peak RSS and CPU of the Piper process tree (psutil), sampled every 50 ms.

    The tree matters on Windows: a uv venv's python.exe is a launcher that runs the real interpreter
    as a child process.
    """

    def __init__(self, pid: int) -> None:
        self.root = psutil.Process(pid)
        self.peak_rss = 0
        self._cpu: dict[int, float] = {}
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)

    def _sample(self) -> None:
        rss = 0
        try:
            tree = [self.root, *self.root.children(recursive=True)]
        except psutil.Error:
            return
        for process in tree:
            try:
                rss += process.memory_info().rss
                self._cpu[process.pid] = sum(process.cpu_times()[:2])
            except psutil.Error:
                continue
        self.peak_rss = max(self.peak_rss, rss)

    def _run(self) -> None:
        while not self._stop.wait(0.05):
            self._sample()

    def mark(self) -> None:
        """Exclude what ran so far (spawn, model load) from ``cpu_seconds``."""
        self._sample()
        self._baseline = sum(self._cpu.values())

    def __enter__(self) -> ChildSampler:
        self._baseline = 0.0
        self._t0 = time.perf_counter()
        self._thread.start()
        return self

    def __exit__(self, *_: object) -> None:
        self._sample()  # before the process tree exits
        self._stop.set()
        self._thread.join()
        self.cpu_seconds = sum(self._cpu.values()) - self._baseline
        self.wall_seconds = time.perf_counter() - self._t0


def summarize(values: list[float]) -> dict[str, float | None]:
    return {"p50": round(percentile(values, 50), 1), "p95": round(percentile(values, 95), 1),
            "max": round(max(values), 1)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--engine", choices=["kokoro", "piper"], default="kokoro")
    parser.add_argument("--voice", help="Piper voice name (Kokoro uses the Iris mix from kokoro.manifest.json)")
    parser.add_argument("--threads", type=int, default=0, help="Kokoro onnxruntime intra-op threads (0 = default)")
    parser.add_argument("--first-chunk-chars", type=int, default=60,
                        help="Kokoro: split a longer first sentence at its first clause (0 = sentences only)")
    parser.add_argument("--piper-python", type=Path, default=REPO_ROOT / ".tools" / "piper" / "Scripts" / "python.exe")
    parser.add_argument("--kokoro-python", type=Path,
                        default=REPO_ROOT / ".tools" / "kokoro" / "Scripts" / "python.exe")
    parser.add_argument("--models", type=Path, default=REPO_ROOT / "models" / "tts")
    parser.add_argument("--fast-report", type=Path, default=None)
    parser.add_argument("--rounds", type=int, default=2)
    parser.add_argument("--multi", type=int, default=3, help="sentences per multi-sentence utterance")
    parser.add_argument("--samples", type=Path, default=REPO_ROOT / "tmp" / "tts-samples")
    parser.add_argument("--out", type=Path, default=RESULTS / "tts")
    args = parser.parse_args(argv)
    args.fast_report = args.fast_report or latest_fast_report()

    if args.engine == "piper":
        if not args.voice:
            parser.error("--voice is required with --engine piper")
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        voice_entry = next(v for v in manifest["voices"] if v["name"] == args.voice)
        model = args.models / f"{args.voice}.onnx"
        pinned = next(f["sha256"] for f in voice_entry["files"] if f["path"].endswith(".onnx"))
        model_sha = sha256(model)
        if model_sha != pinned:
            raise SystemExit(f"{model} SHA-256 {model_sha} differs from the manifest; run benchmarks/fetch-tts.ps1")
        sample_rate = json.loads(model.with_suffix(".onnx.json").read_text(encoding="utf-8"))["audio"]["sample_rate"]
        command = build_command(args.piper_python, model)
        label = args.voice
        voice_report = {"name": args.voice, "sha256": model_sha, "revision": manifest["revision"],
                        "license": voice_entry["license"], "sample_rate": sample_rate}
        runtime = manifest["runtime"]

        def start():
            return PiperProcess(command, sample_rate, timeout_seconds=120)
    else:
        manifest = json.loads(KOKORO_MANIFEST.read_text(encoding="utf-8"))
        model_dir = args.models / "kokoro"
        hashes = {}
        for entry in manifest["files"]:
            hashes[entry["name"]] = sha256(model_dir / entry["name"])
            if hashes[entry["name"]] != entry["sha256"]:
                raise SystemExit(f"{entry['name']} SHA-256 differs from the manifest; run benchmarks/fetch-kokoro.ps1")
        iris = manifest["irisVoice"]
        command = [str(args.kokoro_python), "-I", "-X", "utf8", str(KOKORO_HOST), "--model-dir", str(model_dir),
                   "--voice", iris["mix"], "--speed", str(iris["speed"]), "--lang", iris["lang"],
                   "--threads", str(args.threads), "--first-chunk-chars", str(args.first_chunk_chars)]
        label = (iris["label"] + (f"_t{args.threads}" if args.threads else "")
                 + (f"_fc{args.first_chunk_chars}" if args.first_chunk_chars else "_sentences"))
        voice_report = {"name": iris["label"], "mix": iris["mix"], "speed": iris["speed"], "sha256": hashes,
                        "license": manifest["weightsLicense"], "sample_rate": 24000}
        runtime = manifest["runtime"] | {"threads": args.threads, "first_chunk_chars": args.first_chunk_chars}

        def start():
            return FramedTtsHost(command)

    rows = json.loads(args.fast_report.read_text(encoding="utf-8"))["translations"]
    texts = [(row["id"], row["output"]) for row in rows]
    multi = [(f"{texts[i][0]}+{args.multi - 1}", " ".join(t for _, t in texts[i:i + args.multi]))
             for i in range(0, len(texts) - args.multi + 1, 10)]

    spawn = time.perf_counter()
    with start() as piper, ChildSampler(piper.pid) as child:
        sample_rate = piper.sample_rate
        cold = piper.synthesize("Olá, eu sou a Iris.")
        cold_seconds = time.perf_counter() - spawn
        child.mark()
        items, multi_items = [], []
        samples: dict[str, bytes] = {}
        for round_index in range(args.rounds):
            for text_id, text in texts:
                result = piper.synthesize(text)
                if round_index == 0 and text_id in SAMPLE_IDS:
                    samples[text_id] = result.pcm
                items.append({"id": text_id, "round": round_index, "chars": len(text),
                              "first_audio_ms": round(result.first_audio_seconds * 1000, 1),
                              "last_audio_ms": round(result.last_audio_seconds * 1000, 1),
                              "audio_seconds": round(result.audio_seconds, 3)})
            for text_id, text in multi:
                result = piper.synthesize(text)
                multi_items.append({"id": text_id, "round": round_index, "chars": len(text),
                                    "first_audio_ms": round(result.first_audio_seconds * 1000, 1),
                                    "last_audio_ms": round(result.last_audio_seconds * 1000, 1),
                                    "audio_seconds": round(result.audio_seconds, 3)})

    args.samples.mkdir(parents=True, exist_ok=True)
    for text_id, pcm in samples.items():
        write_wav(args.samples / f"{label}_{text_id}.wav", pcm, sample_rate)

    synth = sum(i["last_audio_ms"] for i in items) / 1000
    audio = sum(i["audio_seconds"] for i in items)
    multi_synth = sum(i["last_audio_ms"] for i in multi_items) / 1000
    report = {
        "benchmark": "tts",
        "created_utc": datetime.now(UTC).isoformat(timespec="seconds"),
        "environment": environment.collect().to_dict(),
        "runtime": runtime | {"command": [Path(c).name if i in (0, 4) else c for i, c in enumerate(command)]},
        "voice": voice_report,
        "config": {"engine": args.engine, "rounds": args.rounds, "device": "cpu", "fast_report": args.fast_report.name,
                   "multi_sentences": args.multi},
        "cold_start": {"spawn_to_first_audio_seconds": round(cold_seconds, 2),
                       "first_utterance_first_audio_ms": round(cold.first_audio_seconds * 1000, 1)},
        "sentences": {
            "count": len(items),
            "first_audio_ms": summarize([i["first_audio_ms"] for i in items]),
            "last_audio_ms": summarize([i["last_audio_ms"] for i in items]),
            "audio_seconds_mean": round(audio / len(items), 2),
            "rtf": round(synth / audio, 3),
        },
        "multi_sentence": {
            "count": len(multi_items),
            "first_audio_ms": summarize([i["first_audio_ms"] for i in multi_items]),
            "last_audio_ms": summarize([i["last_audio_ms"] for i in multi_items]),
            "audio_seconds_mean": round(sum(i["audio_seconds"] for i in multi_items) / len(multi_items), 2),
        },
        "resources": {"host_peak_rss_mib": round(child.peak_rss / 2**20, 1),
                      "host_cpu_seconds": round(child.cpu_seconds, 1),
                      "host_cpu_cores_while_synthesizing": round(child.cpu_seconds / (synth + multi_synth), 2),
                      "logical_cpus": psutil.cpu_count()},
        "notes": "Piper: times exclude the 1 s quiet wait that detects the end of an utterance. "
                 "Kokoro: framed protocol with an explicit end frame.",
        "items": items,
        "multi_items": multi_items,
    }
    args.out.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    out_file = args.out / f"{stamp}_{args.engine}_{label}_cpu.json"
    out_file.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    s, m = report["sentences"], report["multi_sentence"]
    print(f"{label}: cold {report['cold_start']['spawn_to_first_audio_seconds']} s | "
          f"sentence first audio p50/p95 {s['first_audio_ms']['p50']}/{s['first_audio_ms']['p95']} ms, "
          f"last {s['last_audio_ms']['p50']}/{s['last_audio_ms']['p95']} ms, RTF {s['rtf']} | "
          f"{args.multi}-sentence first audio p50/p95 {m['first_audio_ms']['p50']}/{m['first_audio_ms']['p95']} ms | "
          f"RSS {report['resources']['host_peak_rss_mib']} MiB, "
          f"CPU {report['resources']['host_cpu_cores_while_synthesizing']} cores while synthesizing")
    print(f"report: {out_file}\nsamples: {args.samples}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
