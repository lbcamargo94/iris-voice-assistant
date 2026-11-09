"""Collects a reproducible description of the execution environment.

Benchmark reports (POC-001A) embed this so results can be compared across runs and machines.
Only hardware/software metadata is collected; never meeting content.
"""

from __future__ import annotations

import platform
import shutil
import subprocess
import sys
from dataclasses import asdict, dataclass, field


@dataclass(frozen=True)
class GpuInfo:
    name: str
    memory_total_mib: int
    driver_version: str


@dataclass(frozen=True)
class EnvironmentReport:
    python_version: str
    platform: str
    machine: str
    processor: str
    gpus: list[GpuInfo] = field(default_factory=list)

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


def parse_nvidia_smi_csv(output: str) -> list[GpuInfo]:
    """Parses `nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits`."""
    gpus: list[GpuInfo] = []
    for line in output.splitlines():
        line = line.strip()
        if not line:
            continue
        parts = [p.strip() for p in line.split(",")]
        if len(parts) != 3:
            raise ValueError(f"Unexpected nvidia-smi line: {line!r}")
        name, memory, driver = parts
        gpus.append(GpuInfo(name=name, memory_total_mib=int(memory), driver_version=driver))
    return gpus


def query_gpus(timeout_seconds: float = 10.0) -> list[GpuInfo]:
    """Returns NVIDIA GPUs, or an empty list when nvidia-smi is unavailable."""
    executable = shutil.which("nvidia-smi")
    if executable is None:
        return []
    try:
        result = subprocess.run(
            [
                executable,
                "--query-gpu=name,memory.total,driver_version",
                "--format=csv,noheader,nounits",
            ],
            capture_output=True,
            text=True,
            timeout=timeout_seconds,
            check=True,
        )
    except (subprocess.SubprocessError, OSError):
        return []
    return parse_nvidia_smi_csv(result.stdout)


def collect() -> EnvironmentReport:
    return EnvironmentReport(
        python_version=sys.version.split()[0],
        platform=platform.platform(),
        machine=platform.machine(),
        processor=platform.processor(),
        gpus=query_gpus(),
    )


if __name__ == "__main__":
    import json

    print(json.dumps(collect().to_dict(), indent=2))
