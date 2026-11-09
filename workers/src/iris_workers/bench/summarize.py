"""Renders benchmark JSON reports as a Markdown table (one row per run and meeting).

    uv run python -m iris_workers.bench.summarize ../benchmarks/results/stt
"""

from __future__ import annotations

import json
import sys
from pathlib import Path


def _fmt(value: object) -> str:
    if value is None:
        return "—"
    if isinstance(value, float):
        return f"{value:.3f}".rstrip("0").rstrip(".") if value < 10 else f"{value:.1f}"
    return str(value)


def stt_rows(report: dict) -> list[list[str]]:
    rows = []
    resources = report.get("resources", {})
    vram = resources.get("vram_used_mib_max")
    base = resources.get("vram_used_mib_baseline")
    vram_delta = None if vram is None or base is None else round(vram - base)
    for meeting in report["meetings"]:
        offline = meeting.get("offline", {})
        streaming = meeting.get("streaming", {})
        commit = streaming.get("commit_latency_seconds", {})
        seen = streaming.get("first_seen_latency_seconds", {})
        rows.append(
            [
                report["model"]["name"],
                report["config"]["compute_type"],
                str(report["config"]["beam_size"]),
                meeting["meeting"],
                _fmt(offline.get("wer")),
                _fmt(streaming.get("wer")),
                _fmt(offline.get("rtf")),
                _fmt(streaming.get("rtf")),
                f"{_fmt(seen.get('p50'))} / {_fmt(seen.get('p95'))}",
                f"{_fmt(commit.get('p50'))} / {_fmt(commit.get('p95'))}",
                _fmt(streaming.get("lag_seconds_max")),
                _fmt(streaming.get("forced_trims")),
                _fmt(vram_delta),
                _fmt(resources.get("cpu_percent_avg")),
            ]
        )
    return rows


HEADER = [
    "Modelo",
    "Compute",
    "Beam",
    "Reunião",
    "WER offline",
    "WER streaming",
    "RTF offline",
    "RTF streaming",
    "1ª exibição p50/p95 (s)",
    "Confirmação p50/p95 (s)",
    "Atraso máx. (s)",
    "Cortes forçados",
    "VRAM Δ (MiB)",
    "CPU média (%)",
]


def render(folder: Path) -> str:
    rows: list[list[str]] = []
    for file in sorted(folder.glob("*.json")):
        report = json.loads(file.read_text(encoding="utf-8"))
        if report.get("benchmark") == "stt":
            rows.extend(stt_rows(report))
    lines = ["| " + " | ".join(HEADER) + " |", "|" + " --- |" * len(HEADER)]
    lines += ["| " + " | ".join(row) + " |" for row in rows]
    return "\n".join(lines)


if __name__ == "__main__":
    print(render(Path(sys.argv[1])))
