import io
import struct
import subprocess
import sys

import pytest

from iris_workers.protocol import (
    AUDIO_HEADER_BYTES,
    MAX_MESSAGE,
    AudioFrame,
    ProtocolError,
    decode,
    encode_audio,
    encode_control,
    read_message,
)


def test_control_round_trip_and_framing():
    data = encode_control({"type": "ping", "seq": 7})
    (length,) = struct.unpack("<I", data[:4])
    assert length == len(data) - 4 and data[4] == 0x01
    assert read_message(io.BytesIO(data)) == {"type": "ping", "seq": 7}


def test_audio_round_trip_keeps_header_and_samples():
    samples = struct.pack("<3f", 0.5, -0.25, 1.0)
    frame = AudioFrame(stream=1, sequence=42, position=13_440, samples=samples, markers=8)
    data = encode_audio(frame)
    assert len(data) == 4 + AUDIO_HEADER_BYTES + len(samples)
    assert read_message(io.BytesIO(data)) == frame


def test_wire_format_matches_the_dotnet_host_byte_for_byte():
    # Same frame as WorkerProtocolTests.Wire_format_matches_the_python_worker_byte_for_byte (C#).
    frame = AudioFrame(stream=1, sequence=42, position=13_440, samples=struct.pack("<f", 0.5), markers=8)
    expected = "1C000000" "02" "01" "0800" "2A00000000000000" "8034000000000000" "01000000" "0000003F"
    assert encode_audio(frame).hex().upper() == expected


def test_end_of_stream_is_clean_only_between_messages():
    assert read_message(io.BytesIO(b"")) is None
    with pytest.raises(ProtocolError):
        read_message(io.BytesIO(encode_control({"type": "x"})[:-2]))


@pytest.mark.parametrize("payload, error", [
    (b"", "empty"),
    (b"\x07abc", "unknown message kind"),
    (b"\x01[1,2]", "string 'type'"),
    (b"\x01{bad json", "invalid control JSON"),
    (b"\x02" + b"\x00" * 5, "truncated audio header"),
])
def test_malformed_messages_are_protocol_errors(payload, error):
    with pytest.raises(ProtocolError, match=error):
        decode(payload)


def test_audio_sample_count_must_match_the_payload():
    data = bytearray(encode_audio(AudioFrame(1, 0, 0, b"\x00" * 8)))
    data[4 + 20:4 + 24] = struct.pack("<I", 3)  # claims 3 samples, carries 2
    with pytest.raises(ProtocolError, match="declares 3 samples"):
        decode(bytes(data[4:]))


def test_size_limit_applies_both_ways():
    with pytest.raises(ProtocolError, match="outside"):
        read_message(io.BytesIO(struct.pack("<I", MAX_MESSAGE + 1)))
    with pytest.raises(ProtocolError, match="exceeds"):
        encode_control({"type": "big", "data": "x" * MAX_MESSAGE})


def test_contract_worker_speaks_the_protocol_over_stdio():
    process = subprocess.Popen([sys.executable, "-I", "-m", "iris_workers.testing.contract_worker"],
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        hello = read_message(process.stdout)
        assert hello["type"] == "hello" and hello["protocol"] == 1 and "echo" in hello["capabilities"]

        def call(message):
            process.stdin.write(encode_control(message))
            process.stdin.flush()

        call({"type": "request", "id": 1, "method": "print"})        # stray print goes to stderr
        call({"type": "request", "id": 2, "method": "sleep", "params": {"seconds": 30}})
        call({"type": "ping", "seq": 5})                               # answered while a request is busy
        replies = [read_message(process.stdout), read_message(process.stdout)]  # pong may overtake the result
        assert {"type": "result", "id": 1, "data": "printed"} in replies
        assert {"type": "pong", "seq": 5} in replies
        call({"type": "cancel", "id": 2})
        assert read_message(process.stdout) == {"type": "cancelled", "id": 2}
        call({"type": "request", "id": 3, "method": "nope"})
        assert read_message(process.stdout)["code"] == "unknown_method"
        process.stdin.write(encode_audio(AudioFrame(2, 9, 320, b"\x00" * 1280)))
        call({"type": "request", "id": 4, "method": "audio_stats"})
        stats = read_message(process.stdout)["data"]
        assert (stats["frames"], stats["last_sequence"], stats["last_position"], stats["samples"]) == (1, 9, 320, 320)
    finally:
        process.stdin.close()  # end of stdin = shut down
        assert process.wait(timeout=10) == 0
    assert b"this goes to stderr" in process.stderr.read()
