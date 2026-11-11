"""POC-001A step 6: STT + translation + LLM + TTS running together on this machine.

Unlike the STT benchmark (simulated clock), audio is fed in **real time**: a chunk is processed only
after its audio would have arrived, so every component competes for GPU and CPU as in a live meeting.

- STT: two AMI meetings at once (system + microphone) on one faster-whisper model, with the default
  hybrid cadence (ADR-008: 0.5 s display passes with beam 1, skipped when late; 1 s agreement, beam 5).
- Translation: committed words of the **system** stream are grouped into sentences and translated
  by Opus-MT + glossary (ADR-010; microphone translation is off by default, ADR-011).
- LLM (optional, Ollama on loopback): each translation is refined and checked by the fact guard;
  questions are also classified. The LLM queue is bounded; skipped refinements are counted (they are
  optional derivatives, the fast translation is never skipped).
- TTS (optional): every ``--tts-interval`` seconds the latest translation is read by the Iris voice
  (Kokoro host, separate process), as a user pressing "Ouvir".

Latencies are measured from the moment the last word of a sentence was spoken. The report holds
metrics only (no transcripts or translations of the AMI audio).

    uv run --group bench python -m iris_workers.bench.combined --translation-device cpu
"""

from __future__ import annotations

import argparse
import json
import os
import queue
import threading
import time
from dataclasses import asdict
from datetime import UTC, datetime
from pathlib import Path

import psutil

from iris_workers import environment
from iris_workers.bench import manifests
from iris_workers.bench.ami import load_audio
from iris_workers.bench.resources import ResourceSampler, vram_used_mib
from iris_workers.bench.stt import make_transcriber, resolve_model
from iris_workers.bench.text import percentile
from iris_workers.bench.translation import engine_paths
from iris_workers.cuda_runtime import register_cuda_dll_directories
from iris_workers.llm.ollama import OllamaClient
from iris_workers.llm.prompts import (
    CLASSIFY_SCHEMA,
    build_classify_messages,
    build_refine_messages,
    parse_classification,
    parse_refine_output,
)
from iris_workers.speech.local_agreement import LocalAgreementStreamer
from iris_workers.speech.sentences import SentenceAssembler
from iris_workers.translation.glossary import TermProtector
from iris_workers.translation.refine_guard import check_refinement
from iris_workers.tts.framed_host import FramedTtsHost

REPO_ROOT = Path(__file__).resolve().parents[4]
SAMPLE_RATE = 16_000
os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")


def stats(values: list[float], scale: float = 1.0, digits: int = 3) -> dict[str, float | None]:
    out: dict[str, float | None] = {"count": len(values)}
    for q in (50, 95, 99):
        v = percentile(values, q)
        out[f"p{q}"] = None if v is None else round(v * scale, digits)
    out["max"] = round(max(values) * scale, digits) if values else None
    return out


class SystemSampler:
    """Machine-wide CPU% (all processes), sampled every 0.5 s."""

    def __init__(self) -> None:
        self.values: list[float] = []
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)

    def _run(self) -> None:
        psutil.cpu_percent(None)
        while not self._stop.wait(0.5):
            self.values.append(psutil.cpu_percent(None))

    def __enter__(self) -> SystemSampler:
        self._thread.start()
        return self

    def __exit__(self, *_: object) -> None:
        self._stop.set()
        self._thread.join()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--stt-model", default="large-v3-turbo")
    parser.add_argument("--stt-compute-type", default="int8_float16")
    parser.add_argument("--meetings", nargs=2, default=["IS1009a", "ES2004a"], metavar=("SYSTEM", "MIC"))
    parser.add_argument("--max-seconds", type=float, default=300.0)
    parser.add_argument("--translation-device", choices=["cpu", "cuda"], default="cpu")
    parser.add_argument("--translation-threads", type=int, default=4)
    parser.add_argument("--llm-model", default="qwen3:4b-instruct", help="'none' disables the LLM")
    parser.add_argument("--llm-queue", type=int, default=4)
    parser.add_argument("--tts", choices=["kokoro", "none"], default="kokoro")
    parser.add_argument("--tts-threads", type=int, default=0)
    parser.add_argument("--tts-interval", type=float, default=30.0)
    parser.add_argument("--corpus", type=Path, default=REPO_ROOT / "data" / "corpus" / "ami")
    parser.add_argument("--models-dir", type=Path, default=REPO_ROOT / "models")
    parser.add_argument("--out", type=Path, default=REPO_ROOT / "benchmarks" / "results" / "combined")
    args = parser.parse_args(argv)

    register_cuda_dll_directories()
    import ctranslate2
    from faster_whisper import WhisperModel

    from iris_workers.translation.ctranslate2_marian import MarianTranslator

    audios = []
    for meeting in args.meetings:
        audio = load_audio(args.corpus, meeting)
        audios.append(audio[: int(args.max_seconds * SAMPLE_RATE)] if args.max_seconds else audio)

    # --- load everything before the clock starts (the app would preload at meeting start) ---
    load: dict[str, float] = {}
    llm = None if args.llm_model == "none" else OllamaClient()
    try:  # baseline = desktop only, also when this run does not use the LLM
        ollama = llm or OllamaClient(timeout_seconds=5)
        for running in ollama.running_models():
            ollama.unload(running["name"])
        time.sleep(2)
    except OSError:
        pass  # Ollama not running
    vram_before_load = vram_used_mib()
    t = time.perf_counter()
    stt_path, stt_info = resolve_model(args.stt_model, args.models_dir / "whisper", allow_download=False)
    whisper = WhisperModel(str(stt_path), device="cuda", compute_type=args.stt_compute_type)
    for beam in (5, 1):  # first calls initialize CUDA/cuDNN kernels; keep that out of the live clock
        make_transcriber(whisper, beam, vad=True)(audios[0][: 5 * SAMPLE_RATE], "")
    load["stt_seconds"] = round(time.perf_counter() - t, 2)

    t = time.perf_counter()
    paths = engine_paths("opus", args.models_dir / "translation")
    cpu = args.translation_device == "cpu"
    translator = MarianTranslator(paths["model_dir"], paths["source_spm"], protector=TermProtector(),
                                  target_token=">>pob<<", device=args.translation_device,
                                  compute_type="int8" if cpu else "int8_float16", beam_size=4,
                                  intra_threads=args.translation_threads if cpu else 0)
    translator.translate("Warm-up sentence.")
    load["translation_seconds"] = round(time.perf_counter() - t, 2)

    llm_options = {"temperature": 0, "seed": 7, "num_ctx": 2048}
    llm_pinned = None
    if llm:
        try:
            llm_pinned = manifests.verify_ollama_model(llm.list_models(), args.llm_model)
        except manifests.ManifestMismatch as error:
            raise SystemExit(str(error)) from None
        t = time.perf_counter()
        llm.chat(args.llm_model, [{"role": "user", "content": "Responda apenas: ok"}], options=llm_options,
                 keep_alive="30m")
        load["llm_seconds"] = round(time.perf_counter() - t, 2)

    tts = None
    if args.tts == "kokoro":
        manifest = json.loads((REPO_ROOT / "benchmarks/models/kokoro.manifest.json").read_text(encoding="utf-8"))
        iris = manifest["irisVoice"]
        t = time.perf_counter()
        tts = FramedTtsHost([
            str(REPO_ROOT / ".tools/kokoro/Scripts/python.exe"), "-I", "-X", "utf8",
            str(REPO_ROOT / "hosts/kokoro/kokoro_host.py"), "--model-dir", str(args.models_dir / "tts/kokoro"),
            "--voice", iris["mix"], "--speed", str(iris["speed"]), "--lang", iris["lang"],
            "--threads", str(args.tts_threads),
        ])
        load["tts_seconds"] = round(time.perf_counter() - t, 2)

    # --- pipeline state ---
    translate_q: queue.Queue = queue.Queue()  # unbounded: final sentences are never dropped (ADR-009)
    llm_q: queue.Queue = queue.Queue(maxsize=args.llm_queue)
    stop = threading.Event()
    lock = threading.Lock()
    m: dict[str, list] = {k: [] for k in (
        "translation_from_speech", "translation_queue_wait", "translation_compute", "refine_from_speech",
        "refine_compute", "classify_compute", "tts_first_audio", "tts_last_audio")}
    counts = {"sentences": 0, "forced_sentences": 0, "translations": 0, "llm_skipped": 0, "refined": 0,
              "guard_rejected": 0, "classified": 0, "classify_invalid": 0, "tts_requests": 0, "errors": 0}
    latest: dict[str, object] = {}
    history: list[str] = []
    t0 = 0.0

    def now() -> float:
        return time.perf_counter() - t0

    def translation_worker() -> None:
        while True:
            item = translate_q.get()
            if item is None:
                return
            sentence, enqueued = item
            started = now()
            result = translator.translate(sentence.text)
            done = now()
            with lock:
                m["translation_queue_wait"].append(started - enqueued)
                m["translation_compute"].append(done - started)
                m["translation_from_speech"].append(done - sentence.end)
                counts["translations"] += 1
                latest["text"] = result.text
            if llm:
                try:
                    llm_q.put_nowait((sentence, result.text))
                except queue.Full:
                    with lock:
                        counts["llm_skipped"] += 1

    def llm_worker() -> None:
        protector = TermProtector()
        while not stop.is_set() or not llm_q.empty():
            try:
                sentence, fast = llm_q.get(timeout=0.2)
            except queue.Empty:
                continue
            try:
                terms = [surface for _, surface, _ in protector.protect(sentence.text).slots]
                started = now()
                refined = llm.chat(args.llm_model, build_refine_messages(sentence.text, fast, terms),
                                   options=llm_options, keep_alive="30m")
                parsed = parse_refine_output(refined.text)
                guard = check_refinement(fast, parsed.text, terms, clean=parsed.clean)
                done = now()
                with lock:
                    m["refine_compute"].append(done - started)
                    m["refine_from_speech"].append(done - sentence.end)
                    counts["refined"] += 1
                    counts["guard_rejected"] += 0 if guard.accepted or parsed.text == fast else 1
                if sentence.text.rstrip().endswith("?"):
                    context = " / ".join(f"Participante: {h}" for h in history[-3:])
                    started = now()
                    answer = llm.chat(args.llm_model, build_classify_messages(context, sentence.text, "Ana"),
                                      options=llm_options, response_format=CLASSIFY_SCHEMA, keep_alive="30m")
                    with lock:
                        m["classify_compute"].append(now() - started)
                        counts["classified"] += 1
                        counts["classify_invalid"] += 0 if parse_classification(answer.text).valid else 1
                history.append(sentence.text)
            except Exception:  # count and continue: the LLM is optional (RNF-011)
                with lock:
                    counts["errors"] += 1

    def tts_worker() -> None:
        next_at = args.tts_interval
        while not stop.wait(0.1):
            if now() < next_at or "text" not in latest:
                continue
            next_at += args.tts_interval
            try:
                result = tts.synthesize(str(latest["text"]))
                with lock:
                    m["tts_first_audio"].append(result.first_audio_seconds)
                    m["tts_last_audio"].append(result.last_audio_seconds)
                    counts["tts_requests"] += 1
            except Exception:
                with lock:
                    counts["errors"] += 1

    full = make_transcriber(whisper, 5, vad=True)
    partial = make_transcriber(whisper, 1, vad=True)
    streams = [{"streamer": LocalAgreementStreamer(full, trim_seconds=15.0, max_buffer_seconds=30.0,
                                                   agreement_interval_seconds=1.0, partial_transcribe=partial),
                "audio": audio, "commit": [], "first_seen": {}, "display": [], "lag": [], "skipped": 0,
                "words": 0} for audio in audios]

    def first_display(state, word, default: float) -> float:
        """When the word first appeared on screen (tentative or committed), matched as in bench.stt."""
        seen = state["first_seen"]
        times = [seen[k] for d in (-1, 0, 1) if (k := (word.key, round(word.start) + d)) in seen]
        return min(times, default=default)
    assembler = SentenceAssembler()
    step = int(0.5 * SAMPLE_RATE)

    workers = [threading.Thread(target=translation_worker, daemon=True)]
    if llm:
        workers.append(threading.Thread(target=llm_worker, daemon=True))
    if tts:
        workers.append(threading.Thread(target=tts_worker, daemon=True))

    def on_system_sentences(sentences) -> None:
        for sentence in sentences:
            counts["sentences"] += 1
            counts["forced_sentences"] += int(sentence.forced)
            translate_q.put((sentence, now()))

    with ResourceSampler(interval_seconds=0.5) as sampler, SystemSampler() as system:
        t0 = time.perf_counter()
        for w in workers:
            w.start()
        longest = max(len(a) for a in audios)
        for position in range(0, longest, step):
            audio_time = (position + step) / SAMPLE_RATE
            wait = audio_time - now()
            if wait > 0:
                time.sleep(wait)
            for index, state in enumerate(streams):
                audio = state["audio"]
                if position >= len(audio):
                    continue
                streamer = state["streamer"]
                streamer.insert_audio(audio[position : position + step])
                chunk_end = min(audio_time, len(audio) / SAMPLE_RATE)
                if not streamer.next_pass_is_agreement and now() - chunk_end > 0.25:
                    state["skipped"] += 1
                    continue
                update = streamer.process()
                finished = now()
                state["lag"].append(finished - chunk_end)
                for w in update.committed + update.tentative:
                    state["first_seen"].setdefault((w.key, round(w.start)), finished)
                state["commit"].extend(finished - w.end for w in update.committed)
                state["display"].extend(first_display(state, w, finished) - w.end for w in update.committed)
                state["words"] += len(update.committed)
                if index == 0:
                    on_system_sentences(assembler.add(update.committed))
        for index, state in enumerate(streams):
            final = state["streamer"].finish()
            state["words"] += len(final.committed)
            if index == 0:
                on_system_sentences(assembler.add(final.committed))
                if (rest := assembler.flush()) is not None:
                    on_system_sentences([rest])
        translate_q.put(None)
        workers[0].join()
        stop.set()
        for w in workers[1:]:
            w.join(timeout=120)
        wall = now()

    if tts:
        tts.close()
    with lock:
        report = {
            "benchmark": "combined",
            "created_utc": datetime.now(UTC).isoformat(timespec="seconds"),
            "environment": environment.collect().to_dict(),
            "versions": {"ctranslate2": ctranslate2.__version__,
                         "ollama": llm.version() if llm else None},
            "config": {k: (str(v) if isinstance(v, Path) else v) for k, v in vars(args).items()
                       if k not in {"corpus", "models_dir", "out"}},
            "stt_model": stt_info,
            "llm_model": {"name": args.llm_model, "pinned": llm_pinned} if llm else None,
            "load_seconds": load,
            "wall_seconds": round(wall, 1),
            "stt": [{"meeting": meeting, "role": role, "committed_words": s["words"],
                     "first_display_latency_seconds": stats(s["display"]),
                     "commit_latency_seconds": stats(s["commit"]), "lag_seconds_max": round(max(s["lag"]), 3),
                     "skipped_display_passes": s["skipped"],
                     "forced_trims": s["streamer"].forced_trims}
                    for meeting, role, s in zip(args.meetings, ("system", "microphone"), streams)],
            "counts": counts,
            "translation_from_speech_seconds": stats(m["translation_from_speech"]),
            "translation_queue_wait_seconds": stats(m["translation_queue_wait"]),
            "translation_compute_seconds": stats(m["translation_compute"]),
            "refine_from_speech_seconds": stats(m["refine_from_speech"]),
            "refine_compute_seconds": stats(m["refine_compute"]),
            "classify_compute_seconds": stats(m["classify_compute"]),
            "tts_first_audio_seconds": stats(m["tts_first_audio"]),
            "tts_last_audio_seconds": stats(m["tts_last_audio"]),
            "resources": asdict(sampler.summary()) | {
                "vram_used_mib_before_load": None if vram_before_load is None else round(vram_before_load, 1),
                "system_cpu_percent": stats(system.values, digits=1),
                "llm_vram_mib": next((round(p.get("size_vram", 0) / 2**20) for p in llm.running_models()), None)
                if llm else None},
            "resources_note": "VRAM is device-wide; system CPU covers all processes (Ollama, Kokoro, desktop).",
        }
    args.out.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    label = (f"tr-{args.translation_device}{args.translation_threads if cpu else ''}"
             f"_llm-{'on' if llm else 'off'}_tts-{args.tts}{args.tts_threads or ''}")
    out_file = args.out / f"{stamp}_{label}.json"
    out_file.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    r = report
    print(f"{label}: wall {r['wall_seconds']} s, VRAM max {r['resources']['vram_used_mib_max']} MiB "
          f"(before load {r['resources']['vram_used_mib_before_load']}, loaded "
          f"{r['resources']['vram_used_mib_baseline']}), system CPU p50/p95 "
          f"{r['resources']['system_cpu_percent']['p50']}/{r['resources']['system_cpu_percent']['p95']}%")
    for s in r["stt"]:
        c, d = s["commit_latency_seconds"], s["first_display_latency_seconds"]
        print(f"  STT {s['role']:<10} display p50/p95 {d['p50']}/{d['p95']} s, commit p50/p95 {c['p50']}/{c['p95']} s, "
              f"lag max {s['lag_seconds_max']} s, "
              f"skipped display {s['skipped_display_passes']}, forced trims {s['forced_trims']}")
    tr = r["translation_from_speech_seconds"]
    print(f"  translation {r['counts']['translations']}/{r['counts']['sentences']} sentences: from speech "
          f"p50/p95/max {tr['p50']}/{tr['p95']}/{tr['max']} s, compute p50 {r['translation_compute_seconds']['p50']} s")
    if llm:
        rf = r["refine_from_speech_seconds"]
        print(f"  LLM refined {r['counts']['refined']} (skipped {r['counts']['llm_skipped']}, guard rejected "
              f"{r['counts']['guard_rejected']}): from speech p50/p95 {rf['p50']}/{rf['p95']} s; classified "
              f"{r['counts']['classified']}")
    if tts:
        ft = r["tts_first_audio_seconds"]
        print(f"  TTS {r['counts']['tts_requests']} requests: first audio p50/p95/max {ft['p50']}/{ft['p95']}/{ft['max']} s")
    print(f"  errors {r['counts']['errors']}\nreport: {out_file}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
