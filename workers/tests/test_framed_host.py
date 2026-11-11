import sys

import pytest

from iris_workers.tts.framed_host import FramedTtsHost, TtsHostError

# Fake host for protocol v1: one audio frame per sentence (2 bytes per character).
# "ERRO" -> error frame; "CRASH" -> exits; "LIXO" -> writes a non-protocol line to stdout.
FAKE_HOST = r"""
import json, sys
out = sys.stdout.buffer
def send(header, payload=b""):
    out.write(json.dumps({"v": 1, **header}).encode() + b"\n" + payload); out.flush()
send({"type": "ready", "sample_rate": 100, "load_seconds": 0.01})
for raw in sys.stdin.buffer:
    request = json.loads(raw)
    if request.get("op") == "shutdown":
        break
    text = request["text"]
    if text == "ERRO":
        send({"type": "error", "id": request["id"], "message": "voz falhou"}); continue
    if text == "CRASH":
        sys.exit(5)
    if text == "LIXO":
        out.write(b"not json\n"); out.flush(); continue
    sentences = [s for s in text.split(". ") if s]
    for seq, sentence in enumerate(sentences):
        pcm = b"\x01\x00" * len(sentence)
        send({"type": "audio", "id": request["id"], "seq": seq, "bytes": len(pcm)}, pcm)
    send({"type": "end", "id": request["id"], "chunks": len(sentences)})
"""


@pytest.fixture
def host():
    with FramedTtsHost([sys.executable, "-I", "-c", FAKE_HOST], ready_timeout_seconds=10, timeout_seconds=5) as h:
        yield h


def test_ready_frame_sets_sample_rate(host):
    assert host.sample_rate == 100
    assert host.load_seconds == pytest.approx(0.01)


def test_collects_every_sentence_until_end_frame(host):
    result = host.synthesize("Primeira frase. Segunda, com acentuação. Terceira")
    expected = len("Primeira frase") + len("Segunda, com acentuação") + len("Terceira")
    assert len(result.pcm) == 2 * expected
    assert 0 < result.first_audio_seconds <= result.last_audio_seconds
    assert result.audio_seconds == pytest.approx(expected / 100)


def test_requests_are_independent(host):
    assert len(host.synthesize("um texto longo aqui").pcm) == 2 * 19
    assert len(host.synthesize("curto").pcm) == 2 * 5


def test_newlines_cannot_split_a_request(host):
    assert len(host.synthesize("linha um\nlinha dois").pcm) == 2 * len("linha um linha dois")


def test_error_frame_raises_and_host_keeps_serving(host):
    with pytest.raises(TtsHostError, match="voz falhou"):
        host.synthesize("ERRO")
    assert len(host.synthesize("ok").pcm) == 4


def test_crash_is_reported(host):
    with pytest.raises(TtsHostError, match="stopped"):
        host.synthesize("CRASH")


def test_non_protocol_output_is_a_protocol_error(host):
    with pytest.raises(TtsHostError, match="stopped"):
        host.synthesize("LIXO")
    assert "protocol error" in host.stderr_tail()


def test_host_that_never_gets_ready_fails_fast():
    with pytest.raises(TtsHostError):
        FramedTtsHost([sys.executable, "-I", "-c", "import sys; sys.exit(2)"], ready_timeout_seconds=5)
