"""Loads AMI Meeting Corpus test material (CC BY 4.0) for benchmarks.

Expected layout (downloaded explicitly; see benchmarks/corpus/ami.manifest.json):

    <root>/audio/<meeting>.Mix-Headset.wav        16 kHz mono PCM16 headset mix
    <root>/annotations/words/<meeting>.<spk>.words.xml   NXT word annotations
"""

from __future__ import annotations

import wave
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from iris_workers.speech.local_agreement import TimedWord

_NITE_ID = "{http://nite.sourceforge.net/}id"


@dataclass(frozen=True)
class AmiReference:
    meeting: str
    words: list[TimedWord]
    """Spoken words of all speakers ordered by start time (punctuation tokens excluded)."""
    skipped_untimed: int


def parse_words_xml(path: Path) -> tuple[list[TimedWord], int]:
    """Parses one speaker's NXT words file. Returns (timed words, count of untimed words skipped)."""
    words: list[TimedWord] = []
    skipped = 0
    for element in ET.parse(path).getroot().iter("w"):
        if element.get("punc") == "true" or not (element.text or "").strip():
            continue
        start, end = element.get("starttime"), element.get("endtime")
        if start is None or end is None:
            skipped += 1
            continue
        words.append(TimedWord(float(start), float(end), element.text.strip()))
    return words, skipped


def load_reference(root: Path, meeting: str) -> AmiReference:
    files = sorted((root / "annotations" / "words").glob(f"{meeting}.*.words.xml"))
    if not files:
        raise FileNotFoundError(f"No AMI word annotations for {meeting} under {root}")
    words: list[TimedWord] = []
    skipped = 0
    for file in files:
        speaker_words, speaker_skipped = parse_words_xml(file)
        words.extend(speaker_words)
        skipped += speaker_skipped
    words.sort(key=lambda w: (w.start, w.end))
    return AmiReference(meeting=meeting, words=words, skipped_untimed=skipped)


def load_audio(root: Path, meeting: str, expected_rate: int = 16_000) -> np.ndarray:
    """Returns float32 mono samples in [-1, 1]."""
    path = root / "audio" / f"{meeting}.Mix-Headset.wav"
    with wave.open(str(path), "rb") as wav:
        if wav.getframerate() != expected_rate or wav.getnchannels() != 1 or wav.getsampwidth() != 2:
            raise ValueError(
                f"{path.name}: expected {expected_rate} Hz mono PCM16, got "
                f"{wav.getframerate()} Hz, {wav.getnchannels()} ch, {wav.getsampwidth() * 8} bit"
            )
        pcm = np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2")
    return (pcm.astype(np.float32) / 32768.0).copy()
