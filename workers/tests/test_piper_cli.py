import sys
import wave
from pathlib import Path

import pytest

from iris_workers.tts.piper_cli import (
    PiperError,
    PiperProcess,
    build_command,
    pcm_seconds,
    sanitize_text,
    write_wav,
)

# Stand-in for `python -m piper --output-raw`: per stdin line, 2 bytes of PCM per character,
# flushed in two sentence-like chunks. "FAIL" makes it exit.
FAKE_PIPER = r"""
import sys, time
for line in sys.stdin.buffer:
    text = line.decode("utf-8").strip()
    if text == "FAIL":
        sys.stderr.write("voice error\n"); sys.stderr.flush(); sys.exit(3)
    data = b"\x01\x00" * len(text)
    half = len(data) // 2 & ~1
    for part in (data[:half], data[half:]):
        sys.stdout.buffer.write(part); sys.stdout.buffer.flush(); time.sleep(0.02)
"""


@pytest.fixture
def fake_piper():
    process = PiperProcess([sys.executable, "-I", "-c", FAKE_PIPER], sample_rate=100,
                           quiet_seconds=0.2, timeout_seconds=5)
    yield process
    process.close()


def test_sanitize_collapses_lines_and_blocks_raw_phonemes():
    assert sanitize_text("  Primeira linha.\nSegunda\r\n linha. ") == "Primeira linha. Segunda linha."
    assert sanitize_text("o [[ bˈætmæn ]] chegou") == "o bˈætmæn chegou"


def test_build_command_is_isolated_utf8_raw_output():
    command = build_command(Path("py.exe"), Path("voice.onnx"), length_scale=1.1)
    assert command[:5] == ["py.exe", "-I", "-X", "utf8", "-m"]
    assert "--output-raw" in command
    assert command[command.index("--length-scale") + 1] == "1.1"


def test_pcm_seconds_and_wav(tmp_path):
    assert pcm_seconds(44100, 22050) == 1.0
    path = tmp_path / "out.wav"
    write_wav(path, b"\x00\x00" * 22050, 22050)
    with wave.open(str(path)) as wav:
        assert (wav.getnchannels(), wav.getsampwidth(), wav.getframerate(), wav.getnframes()) == (1, 2, 22050, 22050)


def test_synthesize_collects_all_chunks_of_an_utterance(fake_piper):
    text = "Ação às dez, próxima terça-feira."
    result = fake_piper.synthesize(text)
    assert len(result.pcm) == 2 * len(text)  # UTF-8 accents arrived intact
    assert 0 < result.first_audio_seconds <= result.last_audio_seconds
    assert result.audio_seconds == pytest.approx(len(text) / 100)


def test_utterances_do_not_leak_into_each_other(fake_piper):
    assert len(fake_piper.synthesize("um texto bem longo aqui").pcm) == 2 * 23
    assert len(fake_piper.synthesize("curto").pcm) == 2 * 5


def test_empty_text_is_rejected(fake_piper):
    with pytest.raises(ValueError):
        fake_piper.synthesize(" \n ")


def test_process_failure_is_reported_with_stderr(fake_piper):
    with pytest.raises(PiperError, match="voice error|exited"):
        fake_piper.synthesize("FAIL")  # detected on exit, not after the 5 s timeout
    with pytest.raises(PiperError, match="exited"):
        fake_piper.synthesize("depois da falha")
