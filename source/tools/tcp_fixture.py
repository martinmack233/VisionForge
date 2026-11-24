"""Bounded loopback TCP receiver/optional responder for VisionStudio. Standard library only."""
import argparse
import json
import socket
import struct
import time
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--mode", choices=["delimiter", "length"], default="delimiter")
    parser.add_argument("--encoding", choices=["utf-8", "ascii", "utf-16-le"], default="utf-8")
    parser.add_argument("--delimiter", default="\\n")
    parser.add_argument("--max-bytes", type=int, default=65536)
    parser.add_argument("--timeout", type=float, default=10)
    parser.add_argument("--count", type=int, default=1)
    parser.add_argument("--linger", type=float, default=0.5, help="Observe extra frames after expected messages")
    parser.add_argument("--reply")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if not (1 <= args.max_bytes <= 1048576 and 1 <= args.count <= 10000 and 0 < args.timeout <= 120 and 0 <= args.linger <= 10):
        parser.error("Invalid bounded test limits")
    delimiter = args.delimiter.replace("\\r", "\r").replace("\\n", "\n").replace("\\t", "\t").encode(args.encoding)
    if not 1 <= len(delimiter) <= 16:
        parser.error("Delimiter must encode to 1..16 bytes")
    messages, error = [], None
    pending = bytearray()

    def receive_frame(connection):
        if args.mode == "length":
            while len(pending) < 4:
                chunk = connection.recv(4 - len(pending))
                if not chunk:
                    raise EOFError("Peer closed during prefix")
                pending.extend(chunk)
            length = struct.unpack(">I", pending[:4])[0]
            del pending[:4]
            if length > args.max_bytes:
                raise ValueError("Message exceeds maximum size")
            while len(pending) < length:
                chunk = connection.recv(length - len(pending))
                if not chunk:
                    raise EOFError("Peer closed during frame")
                pending.extend(chunk)
            body = bytes(pending[:length]); del pending[:length]
            return body.decode(args.encoding, errors="strict")
        while True:
            start = 0
            while True:
                index = pending.find(delimiter, start)
                if index < 0:
                    break
                if args.encoding != "utf-16-le" or index % 2 == 0:
                    if index > args.max_bytes:
                        raise ValueError("Message exceeds maximum size")
                    body = bytes(pending[:index]); del pending[:index + len(delimiter)]
                    return body.decode(args.encoding, errors="strict")
                start = index + 1
            if len(pending) > args.max_bytes + len(delimiter) - 1:
                raise ValueError("Missing delimiter or oversize message")
            chunk = connection.recv(4096)
            if not chunk:
                raise EOFError("Peer closed before a frame")
            pending.extend(chunk)

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.bind((args.host, args.port)); server.listen(1); server.settimeout(args.timeout)
        port = server.getsockname()[1]
        print(json.dumps({"ready": True, "host": args.host, "port": port}), flush=True)
        try:
            connection, _ = server.accept()
            with connection:
                connection.settimeout(args.timeout)
                for _ in range(args.count):
                    message = receive_frame(connection); messages.append(message)
                    print(json.dumps({"received": message}, ensure_ascii=False), flush=True)
                    if args.reply is not None:
                        body = args.reply.encode(args.encoding)
                        if len(body) > args.max_bytes:
                            raise ValueError("Reply exceeds maximum size")
                        connection.sendall(struct.pack(">I", len(body)) + body if args.mode == "length" else body + delimiter)
                until = time.monotonic() + args.linger
                while time.monotonic() < until:
                    connection.settimeout(max(0.001, until - time.monotonic()))
                    try:
                        messages.append(receive_frame(connection))
                    except (TimeoutError, EOFError):
                        break
        except Exception as exception:
            error = type(exception).__name__ + ": " + str(exception)
    result = {"passed": error is None and len(messages) == args.count, "port": port, "messages": messages, "error": error}
    output = Path(args.output); output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False), flush=True)
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
