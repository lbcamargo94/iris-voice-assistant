"""Background sampling of process CPU/RAM and device VRAM during a benchmark run."""

from __future__ import annotations

import threading
from dataclasses import dataclass

import psutil

try:
    import pynvml
except ImportError:  # pragma: no cover - optional
    pynvml = None


@dataclass(frozen=True)
class ResourceSummary:
    cpu_percent_avg: float
    """Process CPU, normalized to the whole machine (100 = all logical cores busy)."""
    cpu_percent_max: float
    rss_mib_max: float
    vram_used_mib_baseline: float | None
    vram_used_mib_max: float | None
    """Device-wide used VRAM (WDDM does not expose reliable per-process values)."""
    samples: int


def vram_used_mib() -> float | None:
    if pynvml is None:
        return None
    try:
        pynvml.nvmlInit()
        handle = pynvml.nvmlDeviceGetHandleByIndex(0)
        return pynvml.nvmlDeviceGetMemoryInfo(handle).used / 2**20
    except pynvml.NVMLError:
        return None


class ResourceSampler:
    def __init__(self, interval_seconds: float = 0.25) -> None:
        self._interval = interval_seconds
        self._process = psutil.Process()
        self._cores = psutil.cpu_count() or 1
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, name="resource-sampler", daemon=True)
        self._cpu: list[float] = []
        self._rss: list[float] = []
        self._vram: list[float] = []
        self._baseline: float | None = None

    def __enter__(self) -> ResourceSampler:
        self._baseline = vram_used_mib()
        self._process.cpu_percent(None)
        self._thread.start()
        return self

    def __exit__(self, *_: object) -> None:
        self._stop.set()
        self._thread.join()

    def _run(self) -> None:
        while not self._stop.wait(self._interval):
            self._cpu.append(self._process.cpu_percent(None) / self._cores)
            self._rss.append(self._process.memory_info().rss / 2**20)
            if (vram := vram_used_mib()) is not None:
                self._vram.append(vram)

    def summary(self) -> ResourceSummary:
        return ResourceSummary(
            cpu_percent_avg=round(sum(self._cpu) / len(self._cpu), 1) if self._cpu else 0.0,
            cpu_percent_max=round(max(self._cpu, default=0.0), 1),
            rss_mib_max=round(max(self._rss, default=0.0), 1),
            vram_used_mib_baseline=None if self._baseline is None else round(self._baseline, 1),
            vram_used_mib_max=round(max(self._vram), 1) if self._vram else None,
            samples=len(self._cpu),
        )
