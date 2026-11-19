"""ADR-015: does learned vocabulary (hotwords) improve recognition? Learn on one stretch, measure on the next.

For each AMI meeting: transcribe ``[0, learn)`` offline, extract vocabulary candidates from the sentences with
the same rule as the live worker (``speech/vocabulary.py``), keep the terms seen in at least ``--min-sentences``
sentences, then transcribe ``[learn, learn + evaluate)`` without and with those terms as ``hotwords`` and compare
WER against the AMI reference. Also counts how often the learned terms appear correctly spelled. Metrics only.

    uv run --group bench python -m iris_workers.bench.vocabulary
"""

from __future__ import annotations

import argparse
from types import SimpleNamespace
import json
import re
from collections import Counter
from datetime import UTC, datetime
from pathlib import Path

from iris_workers import environment
from iris_workers.bench.ami import load_audio, load_reference
from iris_workers.bench.stt import resolve_model, run_streams
from iris_workers.bench.text import normalize_words, word_error_rate
from iris_workers.cuda_runtime import register_cuda_dll_directories
from iris_workers.speech.vocabulary import extract_terms

REPO_ROOT = Path(__file__).resolve().parents[4]
RATE = 16_000


def transcribe(model, audio, hotwords: str | None) -> str:
    segments, _ = model.transcribe(audio, language="en", beam_size=5, vad_filter=True,
                                   condition_on_previous_text=False, hotwords=hotwords or None)
    return " ".join(s.text.strip() for s in segments)


def sentences(text: str) -> list[str]:
    return [s for s in re.split(r"(?<=[.!?])\s+", text) if s.strip()]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--meetings", nargs="+", default=["IS1009a", "ES2004a"])
    parser.add_argument("--learn-seconds", type=float, default=300)
    parser.add_argument("--evaluate-seconds", type=float, default=300)
    parser.add_argument("--min-sentences", type=int, default=3)
    parser.add_argument("--model", default="large-v3-turbo")
    parser.add_argument("--compute-type", default="int8_float16")
    parser.add_argument("--corpus", type=Path, default=REPO_ROOT / "data" / "corpus" / "ami")
    parser.add_argument("--out", type=Path, default=REPO_ROOT / "benchmarks" / "results" / "vocabulary")
    args = parser.parse_args(argv)

    register_cuda_dll_directories()
    from faster_whisper import WhisperModel

    path, info = resolve_model(args.model, REPO_ROOT / "models" / "whisper", allow_download=False)
    model = WhisperModel(str(path), device="cuda", compute_type=args.compute_type)
    tokenizer = model.hf_tokenizer

    def tokens(word: str) -> int:
        return len(tokenizer.encode(" " + word, add_special_tokens=False).ids)

    report: dict[str, object] = {"benchmark": "vocabulary", "created_utc": datetime.now(UTC).isoformat(timespec="seconds"),
                                 "environment": environment.collect().to_dict(), "model": info,
                                 "config": {k: str(v) for k, v in vars(args).items() if k not in {"corpus", "out"}},
                                 "meetings": []}
    for meeting in args.meetings:
        audio = load_audio(args.corpus, meeting)
        learn_end = int(args.learn_seconds * RATE)
        eval_end = learn_end + int(args.evaluate_seconds * RATE)
        learned_text = transcribe(model, audio[:learn_end], None)
        seen: Counter[str] = Counter()
        display: dict[str, str] = {}
        for sentence in sentences(learned_text):
            for term in extract_terms(sentence, tokens):
                seen[term.text.lower()] += 1
                display[term.text.lower()] = term.text
        learned = [display[k] for k, n in seen.most_common() if n >= args.min_sentences][:40]

        reference = load_reference(args.corpus, meeting)
        ref_words = normalize_words(w.text for w in reference.words
                                    if args.learn_seconds <= w.start and w.end <= args.learn_seconds + args.evaluate_seconds)
        evaluation = audio[learn_end:eval_end]
        without = transcribe(model, evaluation, None)
        with_terms = transcribe(model, evaluation, " ".join(learned))
        hyp_without, hyp_with = normalize_words(without.split()), normalize_words(with_terms.split())

        def streaming(hotwords: str | None) -> list[str]:
            config = SimpleNamespace(beam_size=5, partial_beam_size=1, trim_seconds=15.0, max_buffer_seconds=30.0,
                                     agreement_interval=1.0, pause_commit=0.6, chunk_seconds=0.5,
                                     skip_partials_when_late=0.25, hotwords=hotwords)
            [(words, _)] = run_streams(model, [evaluation], config)
            return normalize_words(w.text for w in words)

        stream_without, stream_with = streaming(None), streaming(" ".join(learned) if learned else None)
        learned_keys = {t.lower() for t in learned}
        in_reference = sum(1 for w in ref_words if w in learned_keys)

        entry = {
            "meeting": meeting,
            "learned_terms": learned,
            "candidates_seen": len(seen),
            "reference_words": len(ref_words),
            "wer_without": round(word_error_rate(ref_words, hyp_without), 4),
            "wer_with": round(word_error_rate(ref_words, hyp_with), 4),
            "streaming_wer_without": round(word_error_rate(ref_words, stream_without), 4),
            "streaming_wer_with": round(word_error_rate(ref_words, stream_with), 4),
            "learned_terms_in_reference": in_reference,
            "learned_terms_in_hypothesis_without": sum(1 for w in hyp_without if w in learned_keys),
            "learned_terms_in_hypothesis_with": sum(1 for w in hyp_with if w in learned_keys),
        }
        report["meetings"].append(entry)
        print(f"{meeting}: learned {len(learned)} {learned[:12]} | offline WER without/with {entry['wer_without']}/{entry['wer_with']} "
              f"| streaming WER without/with {entry['streaming_wer_without']}/{entry['streaming_wer_with']} "
              f"| learned terms in reference {in_reference}, hypothesis without/with "
              f"{entry['learned_terms_in_hypothesis_without']}/{entry['learned_terms_in_hypothesis_with']}")

    args.out.mkdir(parents=True, exist_ok=True)
    out = args.out / f"{datetime.now(UTC):%Y%m%dT%H%M%SZ}_vocabulary_min{args.min_sentences}.json"
    out.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"report: {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
