"""POC-001A step 4: local LLM via Ollama for translation refinement and directed-question classification.

Refinement input is the fast Opus-MT + glossary output stored in a translation report (deterministic),
so only the LLM is measured. Classification uses ``benchmarks/llm/directed-questions.md``. Both sets are
original project text (MIT), so the report includes the outputs.

The Ollama server must already run on loopback (``scripts/start-ollama.ps1``); nothing is downloaded.

    uv run --group bench python -m iris_workers.bench.llm --model qwen3:4b-instruct
"""

from __future__ import annotations

import argparse
import json
import time
from collections import Counter
from dataclasses import asdict
from datetime import UTC, datetime
from pathlib import Path

from iris_workers import environment
from iris_workers.bench import manifests
from iris_workers.bench.llm_dataset import parse_directed_questions
from iris_workers.bench.resources import ResourceSampler
from iris_workers.bench.text import percentile
from iris_workers.bench.translation_quality import chrf, find_pt_pt_markers, load_references
from iris_workers.llm.ollama import DEFAULT_URL, ChatResult, OllamaClient
from iris_workers.llm.prompts import (
    CLASSIFY_SCHEMA,
    LABELS,
    PROMPT_VERSION,
    build_classify_messages,
    build_refine_messages,
    parse_classification,
    parse_refine_output,
)
from iris_workers.translation.glossary import TermProtector
from iris_workers.translation.refine_guard import check_refinement

REPO_ROOT = Path(__file__).resolve().parents[4]
RESULTS = REPO_ROOT / "benchmarks" / "results"


def latest_fast_report() -> Path:
    candidates = sorted((RESULTS / "translation").glob("*_opus_pob_cuda_*_glossary.json"))
    if not candidates:
        raise FileNotFoundError("no Opus-MT glossary report; run iris_workers.bench.translation --glossary default")
    return candidates[-1]


def timing(results: list[ChatResult]) -> dict[str, object]:
    first = [r.first_token_seconds * 1000 for r in results if r.first_token_seconds is not None]
    total = [r.total_seconds * 1000 for r in results]
    out_tokens = sum(r.output_tokens for r in results)
    return {
        "first_token_ms": {"p50": round(percentile(first, 50), 1), "p95": round(percentile(first, 95), 1)},
        "total_ms": {"p50": round(percentile(total, 50), 1), "p95": round(percentile(total, 95), 1),
                     "max": round(max(total), 1)},
        "output_tokens_mean": round(out_tokens / len(results), 1),
        "prompt_tokens_mean": round(sum(r.prompt_tokens for r in results) / len(results), 1),
    }


def run_refine(client: OllamaClient, args, options) -> dict[str, object]:
    fast_report = json.loads(args.fast_report.read_text(encoding="utf-8"))
    references = {r.id: r for r in load_references(args.references)}
    rows = [t for t in fast_report["translations"] if t["id"] in references]
    protector = TermProtector()
    results, outputs, guarded, items = [], [], [], []
    for row in rows:
        terms = [surface for _, surface, _ in protector.protect(row["source"]).slots]
        result = client.chat(args.model, build_refine_messages(row["source"], row["output"], terms),
                             options=options, keep_alive=args.keep_alive)
        parsed = parse_refine_output(result.text)
        lowered = parsed.text.lower()
        kept = [t for t in terms if t.lower() in lowered]
        guard = check_refinement(row["output"], parsed.text, terms, clean=parsed.clean)
        results.append(result)
        outputs.append(parsed.text)
        guarded.append(parsed.text if guard.accepted else row["output"])
        items.append({
            "id": row["id"], "source": row["source"], "fast": row["output"], "refined": parsed.text,
            "reference": references[row["id"]].reference, "clean": parsed.clean,
            "terms": terms, "terms_kept": kept, "pt_pt_markers": find_pt_pt_markers(parsed.text),
            "guard_accepted": guard.accepted, "guard_reasons": list(guard.reasons),
        })
    refs = [references[row["id"]].reference for row in rows]
    changed = [item for item in items if item["refined"] != item["fast"]]
    return {
        "fast_report": args.fast_report.name,
        "chrf_fast": round(chrf([row["output"] for row in rows], refs), 2),
        "chrf_refined": round(chrf(outputs, refs), 2),
        "chrf_guarded": round(chrf(guarded, refs), 2),
        "changed": len(changed),
        "guard_rejected": sum(1 for item in changed if not item["guard_accepted"]),
        "unclean_outputs": sum(1 for item in items if not item["clean"]),
        "glossary_terms": sum(len(item["terms"]) for item in items),
        "glossary_terms_kept": sum(len(item["terms_kept"]) for item in items),
        "pt_pt_sentences": sum(1 for item in items if item["pt_pt_markers"]),
        "timing": timing(results),
        "items": items,
    }


def run_classify(client: OllamaClient, args, options) -> dict[str, object]:
    data = parse_directed_questions(args.dataset.read_text(encoding="utf-8"))
    results, items = [], []
    confusion: Counter[tuple[str, str]] = Counter()
    for item in data:
        result = client.chat(args.model, build_classify_messages(item.context, item.utterance, args.user_name),
                             options=options, response_format=CLASSIFY_SCHEMA, keep_alive=args.keep_alive)
        parsed = parse_classification(result.text)
        predicted = parsed.label or "invalid"
        confusion[(item.label, predicted)] += 1
        results.append(result)
        items.append({"id": item.id, "utterance": item.utterance, "expected": item.label,
                      "predicted": predicted, "reason": parsed.reason})
    correct = sum(n for (gold, pred), n in confusion.items() if gold == pred)

    def recall(label: str) -> float | None:
        total = sum(n for (gold, _), n in confusion.items() if gold == label)
        return round(confusion[(label, label)] / total, 3) if total else None

    predicted_directed = sum(n for (_, pred), n in confusion.items() if pred == "directed")
    return {
        "items_total": len(data),
        "accuracy": round(correct / len(data), 3),
        "invalid_outputs": sum(n for (_, pred), n in confusion.items() if pred == "invalid"),
        "recall": {label: recall(label) for label in LABELS},
        "directed_precision": round(confusion[("directed", "directed")] / predicted_directed, 3)
        if predicted_directed else None,
        "not_directed_flagged_directed": confusion[("not_directed", "directed")],
        "confusion": {f"{gold}->{pred}": n for (gold, pred), n in sorted(confusion.items())},
        "timing": timing(results),
        "items": items,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", required=True)
    parser.add_argument("--task", choices=["refine", "classify", "both"], default="both")
    parser.add_argument("--url", default=DEFAULT_URL)
    parser.add_argument("--num-ctx", type=int, default=2048)
    parser.add_argument("--keep-alive", default="10m")
    parser.add_argument("--user-name", default="Ana")
    parser.add_argument("--fast-report", type=Path, default=None)
    parser.add_argument("--references", type=Path,
                        default=REPO_ROOT / "benchmarks" / "translation" / "reference-en-ptbr.md")
    parser.add_argument("--dataset", type=Path, default=REPO_ROOT / "benchmarks" / "llm" / "directed-questions.md")
    parser.add_argument("--out", type=Path, default=RESULTS / "llm")
    args = parser.parse_args(argv)
    args.fast_report = args.fast_report or latest_fast_report()

    client = OllamaClient(args.url)
    options = {"temperature": 0, "seed": 7, "num_ctx": args.num_ctx}
    try:
        pinned = manifests.verify_ollama_model(client.list_models(), args.model)
    except manifests.ManifestMismatch as error:
        raise SystemExit(str(error)) from None
    info = client.show(args.model)
    for running in client.running_models():  # cold start: measure load time and VRAM from scratch
        client.unload(running["name"])

    report: dict[str, object] = {
        "benchmark": "llm",
        "created_utc": datetime.now(UTC).isoformat(timespec="seconds"),
        "environment": environment.collect().to_dict(),
        "versions": {"ollama": client.version()},
        "model": {
            "name": args.model,
            "digest": next((m.get("digest") for m in client.list_models() if m.get("name") == args.model), None),
            "parameter_size": info.get("details", {}).get("parameter_size"),
            "quantization": info.get("details", {}).get("quantization_level"),
            "family": info.get("details", {}).get("family"),
            "pinned": pinned,
        },
        "config": {"options": options, "keep_alive": args.keep_alive, "task": args.task, "user_name": args.user_name,
                   "prompt_version": PROMPT_VERSION},
    }

    with ResourceSampler() as sampler:
        t0 = time.perf_counter()
        warm = client.chat(args.model, [{"role": "user", "content": "Responda apenas: ok"}],
                           options=options, keep_alive=args.keep_alive)
        report["cold_load"] = {"wall_seconds": round(time.perf_counter() - t0, 2),
                               "ollama_load_seconds": round(warm.load_seconds, 2)}
        resident = next((m for m in client.running_models() if m.get("name") == args.model), {})
        report["model"]["vram_mib"] = round(resident.get("size_vram", 0) / 2**20, 1)
        report["model"]["resident_mib"] = round(resident.get("size", 0) / 2**20, 1)
        report["model"]["context_length"] = resident.get("context_length")
        if args.task in ("refine", "both"):
            report["refine"] = run_refine(client, args, options)
        if args.task in ("classify", "both"):
            report["classify"] = run_classify(client, args, options)
    report["resources"] = asdict(sampler.summary())
    report["resources_note"] = "VRAM is device-wide (includes the Ollama runner); CPU/RSS are this client only."

    args.out.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    out_file = args.out / f"{stamp}_{args.model.replace(':', '_').replace('/', '_')}_{args.task}_p{PROMPT_VERSION}.json"
    out_file.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    print(f"{args.model}: load {report['cold_load']['wall_seconds']}s vram {report['model']['vram_mib']} MiB")
    if "refine" in report:
        r = report["refine"]
        print(f"  refine   chrF fast/refined/guarded={r['chrf_fast']}/{r['chrf_refined']}/{r['chrf_guarded']} "
              f"changed={r['changed']} guard_rejected={r['guard_rejected']} "
              f"terms {r['glossary_terms_kept']}/{r['glossary_terms']} unclean={r['unclean_outputs']} "
              f"PT-PT={r['pt_pt_sentences']} first/total p50={r['timing']['first_token_ms']['p50']}/"
              f"{r['timing']['total_ms']['p50']} ms p95 total={r['timing']['total_ms']['p95']} ms")
    if "classify" in report:
        c = report["classify"]
        print(f"  classify acc={c['accuracy']} recall={c['recall']} directed precision={c['directed_precision']} "
              f"invalid={c['invalid_outputs']} total p50/p95={c['timing']['total_ms']['p50']}/"
              f"{c['timing']['total_ms']['p95']} ms")
    print(f"report: {out_file}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
