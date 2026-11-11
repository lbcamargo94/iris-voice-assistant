"""POC-001C: echo worker for the transport benchmark (stdio, named pipe, HTTP loopback). Standard library only.

Started by ``tools/IrisVoiceAssistant.TransportBench``; each message is echoed back unchanged.

- ``stdio``: length-prefixed messages (4-byte little-endian size + payload) on stdin/stdout.
- ``pipe --name N``: the same framing over the Windows named pipe ``\\\\.\\pipe\\N`` created by the host.
- ``http``: ``POST /echo`` on 127.0.0.1 with a random port, printed as ``PORT <n>`` on stdout; requests must
  carry the ``X-Iris-Token`` header given with ``--token`` (any local process could reach the port otherwise).
"""

from __future__ import annotations

import argparse
import struct
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import BinaryIO

MAX_MESSAGE = 1 << 20  # refuse anything above 1 MiB: payload size is validated (ARCHITECTURE.md §6)


def read_exact(stream: BinaryIO, size: int) -> bytes | None:
    chunks, remaining = [], size
    while remaining:
        chunk = stream.read(remaining)
        if not chunk:
            return None
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def serve_framed(reader: BinaryIO, writer: BinaryIO) -> None:
    while (header := read_exact(reader, 4)) is not None:
        (size,) = struct.unpack("<I", header)
        if size > MAX_MESSAGE:
            raise ValueError(f"message of {size} bytes exceeds the limit")
        payload = read_exact(reader, size)
        if payload is None:
            return
        writer.write(header + payload)
        writer.flush()


def serve_http(token: str) -> None:
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"  # keep-alive

        def do_POST(self) -> None:  # noqa: N802 (http.server API)
            size = int(self.headers.get("Content-Length", "0"))
            if self.headers.get("X-Iris-Token") != token or size > MAX_MESSAGE:
                self.send_error(403)
                return
            body = self.rfile.read(size)
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *args: object) -> None:  # no request logging
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    sys.stdout.write(f"PORT {server.server_address[1]}\n")
    sys.stdout.flush()
    server.serve_forever()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("transport", choices=["stdio", "pipe", "http"])
    parser.add_argument("--name")
    parser.add_argument("--token", default="")
    args = parser.parse_args()
    if args.transport == "stdio":
        serve_framed(sys.stdin.buffer, sys.stdout.buffer)
    elif args.transport == "pipe":
        with open(rf"\\.\pipe\{args.name}", "r+b", buffering=0) as pipe:
            serve_framed(pipe, pipe)
    else:
        serve_http(args.token)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
