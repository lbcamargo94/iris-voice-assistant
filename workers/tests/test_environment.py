import sys

import pytest

from iris_workers.environment import GpuInfo, collect, parse_nvidia_smi_csv


def test_parses_single_gpu_line() -> None:
    gpus = parse_nvidia_smi_csv("NVIDIA GeForce RTX 4060 Ti, 8188, 616.92\n")

    assert gpus == [GpuInfo(name="NVIDIA GeForce RTX 4060 Ti", memory_total_mib=8188, driver_version="616.92")]


def test_ignores_blank_lines_and_parses_multiple_gpus() -> None:
    gpus = parse_nvidia_smi_csv("GPU A, 4096, 1.0\n\nGPU B, 8192, 2.0\n")

    assert [g.name for g in gpus] == ["GPU A", "GPU B"]


def test_rejects_malformed_line() -> None:
    with pytest.raises(ValueError):
        parse_nvidia_smi_csv("only-one-field")


def test_collect_reports_running_python() -> None:
    report = collect().to_dict()

    assert report["python_version"] == sys.version.split()[0]
    assert isinstance(report["gpus"], list)


def test_workers_run_on_python_312() -> None:
    assert sys.version_info[:2] == (3, 12)
