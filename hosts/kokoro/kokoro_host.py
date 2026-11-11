# SPDX-License-Identifier: GPL-3.0-or-later
# This host imports kokoro-onnx -> phonemizer (GPL-3.0-or-later) and loads espeak-ng (GPL-3.0), so it
# is licensed GPL-3.0-or-later, unlike the rest of the repository (MIT). It runs from its own
# environment (.tools/kokoro) as a separate process; the MIT workers only talk to it over stdio.
"""Kokoro TTS host speaking the framed stdio protocol v1 (see hosts/README.md).

    python -I -X utf8 kokoro_host.py --model-dir models/tts/kokoro --voice if_sara:0.75,pf_dora:0.25
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
from pathlib import Path

import numpy as np
import onnxruntime as rt
from kokoro_onnx import Kokoro

PROTOCOL = 1
_SENTENCE = re.compile(r"(?<=[.!?…])\s+")
_CLAUSE = re.compile(r"(?<=[,;:])\s+")  # "1,5" has no space after the comma, so it is not split


def chunk_text(text: str, first_chunk_chars: int, min_clause_chars: int = 15) -> list[str]:
    """One chunk per sentence. When the first sentence is long, it is split at its first clause
    boundary: the time to the first sound grows with the length of the first chunk."""
    sentences = [s.strip() for s in _SENTENCE.split(text.strip()) if s.strip()]
    if not sentences or not first_chunk_chars or len(sentences[0]) <= first_chunk_chars:
        return sentences
    clauses = _CLAUSE.split(sentences[0])
    head = clauses.pop(0)
    while clauses and len(head) < min_clause_chars:  # "Certo, ..." is too short to stand alone
        head = f"{head} {clauses.pop(0)}"
    if not clauses:
        return sentences
    return [head, " ".join(clauses), *sentences[1:]]


def parse_voice(spec: str) -> list[tuple[str, float]]:
    parts = []
    for item in spec.split(","):
        name, _, weight = item.strip().partition(":")
        parts.append((name, float(weight or 1)))
    total = sum(w for _, w in parts)
    if total <= 0:
        raise ValueError(f"voice weights must be positive: {spec}")
    return [(name, w / total) for name, w in parts]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-dir", type=Path, required=True)
    parser.add_argument("--model", default="kokoro-v1.0.onnx")
    parser.add_argument("--voices", default="voices-v1.0.bin")
    parser.add_argument("--voice", required=True, help="name or weighted mix, e.g. if_sara:0.75,pf_dora:0.25")
    parser.add_argument("--speed", type=float, default=1.0)
    parser.add_argument("--lang", default="pt-br")
    parser.add_argument("--threads", type=int, default=0, help="onnxruntime intra-op threads (0 = default)")
    parser.add_argument("--first-chunk-chars", type=int, default=60,
                        help="split a longer first sentence at its first clause (0 = sentences only)")
    args = parser.parse_args()

    out = sys.stdout.buffer
    sys.stdout = sys.stderr  # nothing but protocol frames may reach stdout

    def send(header: dict, payload: bytes = b"") -> None:
        out.write(json.dumps({"v": PROTOCOL, **header}).encode("utf-8") + b"\n")
        if payload:
            out.write(payload)
        out.flush()

    t0 = time.perf_counter()
    options = rt.SessionOptions()
    if args.threads:
        options.intra_op_num_threads = args.threads
    session = rt.InferenceSession(str(args.model_dir / args.model), sess_options=options,
                                  providers=["CPUExecutionProvider"])
    kokoro = Kokoro.from_session(session, str(args.model_dir / args.voices))
    style = sum(w * kokoro.get_voice_style(name) for name, w in parse_voice(args.voice))
    kokoro.create("Olá.", voice=style, speed=args.speed, lang=args.lang)  # warm-up
    send({"type": "ready", "sample_rate": 24000, "load_seconds": round(time.perf_counter() - t0, 3)})

    for raw in sys.stdin.buffer:
        if not raw.strip():
            continue
        request_id = None
        try:
            request = json.loads(raw)
            if request.get("op") == "shutdown":
                break
            request_id = str(request["id"])
            sentences = chunk_text(str(request["text"]), args.first_chunk_chars)
            for seq, sentence in enumerate(sentences):
                samples, _ = kokoro.create(sentence, voice=style, speed=args.speed, lang=args.lang)
                pcm = (np.clip(samples, -1.0, 1.0) * 32767).astype("<i2").tobytes()
                send({"type": "audio", "id": request_id, "seq": seq, "bytes": len(pcm)}, pcm)
            send({"type": "end", "id": request_id, "chunks": len(sentences)})
        except Exception as error:  # report and keep serving; the client decides what to do
            send({"type": "error", "id": request_id, "message": f"{type(error).__name__}: {error}"})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
