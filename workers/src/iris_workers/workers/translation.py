"""Fast translation worker (POC-001D): Opus-MT tc-big EN→PT-BR (>>pob<<) + technical-term glossary (ADR-010).

    python -I -m iris_workers.workers.translation [--device cpu] [--threads 4]

Sends ``hello`` at once, loads the converted model (SHA-256 checked against translation.manifest.json) and
announces ``status`` ready/failed. Request ``translate`` ``{text}`` → ``{text, seconds, protected_terms,
glossary_fallbacks}``; ``status``. Defaults follow the POC-001A step 6 decision: CPU int8, 4 threads.
"""

from __future__ import annotations

import argparse
import json
import threading
from pathlib import Path
from typing import Any

from iris_workers.protocol import WorkerServer
from iris_workers.workers.common import REPO_ROOT, load_in_background, log, verify_sha256

VERSION = "0.1.0"
MANIFEST = REPO_ROOT / "benchmarks" / "models" / "translation.manifest.json"
MODEL_DIR = "opus-mt-tc-big-en-pt-ct2-f16"
SOURCE_SPM = Path("opus-mt-tc-big-en-pt-marian") / "source.spm"


class NotReady(Exception):
    pass


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--models-dir", type=Path, default=REPO_ROOT / "models" / "translation")
    args = parser.parse_args(argv)

    ready = threading.Event()
    holder: dict[str, Any] = {}

    def translate(params: dict[str, Any], cancel: threading.Event) -> Any:
        if not ready.is_set():
            raise NotReady("translation model still loading")
        text = str(params.get("text", ""))
        if not text.strip():
            return {"text": "", "seconds": 0.0, "protected_terms": 0, "glossary_fallbacks": 0}
        result = holder["translator"].translate(text)
        return {"text": result.text, "seconds": round(result.seconds, 4),
                "protected_terms": result.protected_terms, "glossary_fallbacks": result.glossary_fallbacks}

    def status(params: dict[str, Any], cancel: threading.Event) -> Any:
        return {"ready": ready.is_set(), "device": args.device, "threads": args.threads}

    server = WorkerServer("translation", VERSION, {"translate": translate, "status": status})

    def load() -> dict[str, Any]:
        from iris_workers.cuda_runtime import register_cuda_dll_directories

        register_cuda_dll_directories()
        from iris_workers.translation.ctranslate2_marian import MarianTranslator
        from iris_workers.translation.glossary import TermProtector

        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        conversion = next(c for c in manifest["conversions"] if c["to"] == MODEL_DIR)
        model_dir = args.models_dir / MODEL_DIR
        verify_sha256(model_dir / "model.bin", conversion["modelBinSha256"])
        cpu = args.device == "cpu"
        translator = MarianTranslator(model_dir, args.models_dir / SOURCE_SPM, protector=TermProtector(),
                                      target_token=">>pob<<", device=args.device,
                                      compute_type="int8" if cpu else "int8_float16", beam_size=4,
                                      intra_threads=args.threads if cpu else 0)
        translator.translate("Warm-up sentence.")
        holder["translator"] = translator
        log(f"translation model ready on {args.device}")
        return {"device": args.device, "threads": args.threads}

    load_in_background(server, load, ready)
    return server.serve()


if __name__ == "__main__":
    raise SystemExit(main())
