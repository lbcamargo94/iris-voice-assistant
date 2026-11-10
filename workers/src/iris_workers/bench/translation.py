"""POC-001A step 3: Opus-MT tc-big en->pt vs. Argos en->pt on the PT-BR meeting reference set.

Measures per-sentence latency (batch 1, as the pipeline translates each confirmed clause), corpus chrF
against ``benchmarks/translation/reference-en-ptbr.md`` and European-Portuguese markers in the output.
Models must already be downloaded and converted (``benchmarks/fetch-translation-models.ps1``); this
script never downloads anything. The reference sentences are original project text (MIT), so the
report includes the translations for review.

    uv run --group bench python -m iris_workers.bench.translation --engine opus --target-token ">>pob<<"
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import time
from collections import defaultdict
from dataclasses import asdict
from datetime import UTC, datetime
from pathlib import Path

from iris_workers import environment
from iris_workers.bench.resources import ResourceSampler
from iris_workers.bench.stt import sha256_of
from iris_workers.bench.text import percentile
from iris_workers.bench.translation_quality import chrf, find_pt_pt_markers, load_references
from iris_workers.cuda_runtime import register_cuda_dll_directories

REPO_ROOT = Path(__file__).resolve().parents[4]
MODELS = REPO_ROOT / "models" / "translation"
WARMUP_SENTENCES = 3


def engine_paths(engine: str, models: Path) -> dict[str, Path]:
    if engine == "opus":
        return {
            "model_dir": models / "opus-mt-tc-big-en-pt-ct2-f16",
            "source_spm": models / "opus-mt-tc-big-en-pt-marian" / "source.spm",
        }
    if engine == "argos":
        package = models / "argos" / "translate-en_pt-1_9"
        return {"model_dir": package / "model", "source_spm": package / "sentencepiece.model"}
    raise ValueError(f"unknown engine {engine!r}")


def argos_with_language_token(model_dir: Path, token: str, models: Path) -> Path:
    """Argos forces its language token via ``bos_token``; derive a model dir with another token.

    The derived dir hard-links (or copies) the weights and only rewrites ``config.json``.
    """
    config = json.loads((model_dir / "config.json").read_text(encoding="utf-8"))
    if config.get("bos_token") == token:
        return model_dir
    derived = models / "derived" / f"argos-en_pt-1_9-{token.strip('<>')}"
    derived.mkdir(parents=True, exist_ok=True)
    for name in ("model.bin", "shared_vocabulary.json"):
        target = derived / name
        if not target.exists():
            try:
                os.link(model_dir / name, target)
            except OSError:
                shutil.copy2(model_dir / name, target)
    config["bos_token"] = token
    (derived / "config.json").write_text(json.dumps(config, indent=2), encoding="utf-8")
    return derived


def latency_summary(seconds: list[float]) -> dict[str, float | None]:
    ms = [s * 1000 for s in seconds]
    return {
        "p50": round(percentile(ms, 50), 1),
        "p95": round(percentile(ms, 95), 1),
        "max": round(max(ms), 1),
        "mean": round(sum(ms) / len(ms), 1),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--engine", choices=["opus", "argos"], required=True)
    parser.add_argument("--target-token", default=None,
                        help="language token, e.g. >>pob<< or >>por<< (Opus-MT default >>pob<<; Argos default: its own)")
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--compute-type", default="int8_float16")
    parser.add_argument("--beam-size", type=int, default=4)
    parser.add_argument("--intra-threads", type=int, default=0, help="CPU threads (0 = CTranslate2 default)")
    parser.add_argument("--rounds", type=int, default=3, help="passes over the reference set for latency")
    parser.add_argument("--references", type=Path,
                        default=REPO_ROOT / "benchmarks" / "translation" / "reference-en-ptbr.md")
    parser.add_argument("--models-dir", type=Path, default=MODELS)
    parser.add_argument("--out", type=Path, default=REPO_ROOT / "benchmarks" / "results" / "translation")
    args = parser.parse_args(argv)

    register_cuda_dll_directories()
    import ctranslate2
    import sentencepiece

    from iris_workers.translation.ctranslate2_marian import MarianTranslator

    paths = engine_paths(args.engine, args.models_dir)
    token = args.target_token
    if args.engine == "opus" and token is None:
        token = ">>pob<<"
    if args.engine == "argos" and token is not None:
        paths["model_dir"] = argos_with_language_token(paths["model_dir"], token, args.models_dir)
        token = None  # injected through config.json (add_source_bos)
    effective_token = args.target_token or (">>pob<<" if args.engine == "opus" else "config default (>>por<<)")

    references = load_references(args.references)
    report: dict[str, object] = {
        "benchmark": "translation",
        "created_utc": datetime.now(UTC).isoformat(timespec="seconds"),
        "environment": environment.collect().to_dict(),
        "versions": {"ctranslate2": ctranslate2.__version__, "sentencepiece": sentencepiece.__version__},
        "model": {
            "engine": args.engine,
            "model_dir": str(paths["model_dir"].relative_to(REPO_ROOT)),
            "model_bin_sha256": sha256_of(paths["model_dir"] / "model.bin"),
            "target_token": effective_token,
        },
        "config": {k: (str(v) if isinstance(v, Path) else v) for k, v in vars(args).items()
                   if k not in {"models_dir", "out"}},
        "reference_sentences": len(references),
        "reference_states": dict(sorted({r.state: sum(1 for x in references if x.state == r.state)
                                         for r in references}.items())),
        "metric_notes": "chrF: char 1-6-grams, beta 2, case-sensitive, whitespace ignored, single reference "
                        "(alternatives in Notas are not scored). PT-PT markers: unambiguous European forms only.",
    }

    with ResourceSampler() as sampler:
        t0 = time.perf_counter()
        translator = MarianTranslator(
            paths["model_dir"], paths["source_spm"], target_token=token, device=args.device, compute_type=args.compute_type,
            beam_size=args.beam_size, intra_threads=args.intra_threads,
        )
        report["model_load_seconds"] = round(time.perf_counter() - t0, 2)
        for sentence in references[:WARMUP_SENTENCES]:
            translator.translate(sentence.source)

        latencies: list[float] = []
        outputs: list[str] = []
        for round_index in range(args.rounds):
            for sentence in references:
                result = translator.translate(sentence.source)
                latencies.append(result.seconds)
                if round_index == 0:
                    outputs.append(result.text)

    by_category: dict[str, list[int]] = defaultdict(list)
    for i, sentence in enumerate(references):
        by_category[sentence.category].append(i)
    markers = [find_pt_pt_markers(text) for text in outputs]
    marker_counts: dict[str, int] = defaultdict(int)
    for found in markers:
        for name in found:
            marker_counts[name] += 1

    report["results"] = {
        "chrf": round(chrf(outputs, [r.reference for r in references]), 2),
        "chrf_by_category": {
            category: round(chrf([outputs[i] for i in idx], [references[i].reference for i in idx]), 2)
            for category, idx in by_category.items()
        },
        "latency_ms": latency_summary(latencies),
        "source_chars_mean": round(sum(len(r.source) for r in references) / len(references), 1),
        "pt_pt_sentences": sum(1 for found in markers if found),
        "pt_pt_markers": dict(sorted(marker_counts.items())),
    }
    report["resources"] = asdict(sampler.summary())
    report["translations"] = [
        {"id": r.id, "category": r.category, "source": r.source, "reference": r.reference,
         "output": out, "pt_pt_markers": found}
        for r, out, found in zip(references, outputs, markers)
    ]

    args.out.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    token_tag = effective_token.strip("<>") if effective_token.startswith(">>") else "default"
    out_file = args.out / f"{stamp}_{args.engine}_{token_tag}_{args.device}_{args.compute_type}_b{args.beam_size}.json"
    out_file.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    res = report["results"]
    print(
        f"{args.engine} {effective_token} {args.device}/{args.compute_type} b{args.beam_size}: chrF={res['chrf']} "
        f"latency p50/p95={res['latency_ms']['p50']}/{res['latency_ms']['p95']} ms "
        f"PT-PT sentences={res['pt_pt_sentences']} {res['pt_pt_markers']}",
        flush=True,
    )
    print(f"report: {out_file}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
