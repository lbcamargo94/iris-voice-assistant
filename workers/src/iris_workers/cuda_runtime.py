"""Expose the pip-installed NVIDIA DLLs (cuBLAS, cuDNN, NVRTC) to CTranslate2 on Windows.

The ``nvidia-*-cu12`` wheels ship their DLLs under ``site-packages/nvidia/<lib>/bin``, which is not on
the DLL search path. This avoids requiring a global CUDA Toolkit install (docs/AI-RUNTIME.md).
Call :func:`register_cuda_dll_directories` before importing ``ctranslate2``/``faster_whisper``.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

_NVIDIA_LIBS = ("cublas", "cudnn", "cuda_nvrtc")
_handles: list[object] = []


def find_cuda_dll_directories(site_packages: Path) -> list[Path]:
    """Return the existing ``nvidia/<lib>/bin`` directories under ``site_packages``."""
    nvidia_root = site_packages / "nvidia"
    return [d for lib in _NVIDIA_LIBS if (d := nvidia_root / lib / "bin").is_dir()]


def register_cuda_dll_directories() -> list[Path]:
    """Add the NVIDIA DLL directories to the process DLL search path (Windows only, idempotent)."""
    if sys.platform != "win32" or _handles:
        return []
    registered: list[Path] = []
    for entry in sys.path:
        path = Path(entry)
        if path.name != "site-packages":
            continue
        for directory in find_cuda_dll_directories(path):
            _handles.append(os.add_dll_directory(str(directory)))
            os.environ["PATH"] = f"{directory}{os.pathsep}{os.environ.get('PATH', '')}"
            registered.append(directory)
    return registered
