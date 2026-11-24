"""Version 1 RuntimeHost client: explicit load/run/status/result/cancel/close; no automatic retries.

Python standard library only. Run after explicitly starting VisionPlatform.RuntimeHost.
"""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import socket
import struct
import sys
import time
import uuid

MAXIMUM_MESSAGE_BYTES = 1048576


class HostClient:
    def __init__(self, address="127.0.0.1", port=8766, timeout=10):
        self.socket = socket.create_connection((address, port), timeout=timeout)
        self.socket.settimeout(timeout)

    def _read_exactly(self, length):
        chunks = bytearray()
        while len(chunks) < length:
            chunk = self.socket.recv(length - len(chunks))
            if not chunk:
                raise EOFError("RuntimeHost disconnected before completing a frame.")
            chunks.extend(chunk)
        return bytes(chunks)

    def command(self, operation, payload=None, request_id=None):
        request = dict(ProtocolVersion=1, RequestId=request_id or uuid.uuid4().hex, Operation=operation)
        if payload is not None:
            request["Payload"] = payload
        data = json.dumps(request, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
        if len(data) > MAXIMUM_MESSAGE_BYTES:
            raise ValueError("Request exceeds the 1 MiB protocol budget.")
        self.socket.sendall(struct.pack("!I", len(data)) + data)
        length = struct.unpack("!I", self._read_exactly(4))[0]
        if length > MAXIMUM_MESSAGE_BYTES:
            raise ValueError("Response exceeds the 1 MiB protocol budget.")
        response = json.loads(self._read_exactly(length).decode("utf-8"))
        if response.get("ProtocolVersion") != 1 or response.get("RequestId") != request["RequestId"]:
            raise ValueError("Response version/correlation ID differs from the request.")
        return response

    def close(self):
        self.socket.close()


def require(response):
    if not response.get("Success"):
        raise RuntimeError(f"{response.get('ErrorCode', 'UNKNOWN')}: {response.get('Message', '')}")
    return response.get("Payload")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8766)
    parser.add_argument("--project", required=True, help="Explicit project path readable on the host machine.")
    parser.add_argument("--target", help="Execute to this existing node and its ancestors.")
    parser.add_argument("--timeout", type=int, default=60000, help="Cooperative engine timeout in milliseconds.")
    parser.add_argument("--cancel-after-ms", type=int, help="Exercise cancellation after this delay.")
    args = parser.parse_args()
    if not 1 <= args.port <= 65535 or not 1 <= args.timeout <= 3600000:
        parser.error("Port/timeout is outside its supported range.")
    if args.cancel_after_ms is not None and args.cancel_after_ms < 0:
        parser.error("cancel-after-ms must be non-negative.")
    client = HostClient(args.host, args.port)
    run_id = None
    try:
        require(client.command("load", dict(ProjectPath=str(Path(args.project).resolve()))))
        options = dict(TimeoutMilliseconds=args.timeout)
        if args.target:
            options["TargetNodeId"] = args.target
        run_id = require(client.command("run", options))["RunId"]
        started = time.monotonic()
        cancel_sent = False
        while True:
            if args.cancel_after_ms is not None and not cancel_sent and (time.monotonic() - started) * 1000 >= args.cancel_after_ms:
                require(client.command("cancel", dict(RunId=run_id)))
                cancel_sent = True
            status = require(client.command("status", dict(RunId=run_id)))
            if status["State"] != "Running":
                break
            if time.monotonic() - started > args.timeout / 1000 + 60:
                raise TimeoutError("Run is still completing a non-preemptible native call; no completed result is claimed.")
            time.sleep(.05)
        response = client.command("result", dict(RunId=run_id))
        print(json.dumps(response, ensure_ascii=False, indent=2, allow_nan=False))
        return 0 if response.get("Success") and response.get("Payload", {}).get("Success") else 2
    except KeyboardInterrupt:
        if run_id:
            try:
                client.command("cancel", dict(RunId=run_id))
            except (OSError, ValueError, EOFError):
                pass
        return 130
    finally:
        try:
            client.command("close")
        except (OSError, ValueError, EOFError):
            pass
        client.close()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, EOFError) as error:
        print("RuntimeHost client error: " + str(error), file=sys.stderr)
        raise SystemExit(1)
