"""Bounded local test peer for VisionStudio TCP framing; Python standard library only.

python tools/tcp-loopback-peer.py --port 9000 --count 1 --reply ACK
python tools/tcp-loopback-peer.py --port 9000 --mode length --count 1
"""
import argparse
import json
import socket
import struct


def read_exact(connection, count):
    data = bytearray()
    while len(data) < count:
        chunk = connection.recv(count - len(data))
        if not chunk:
            raise EOFError("Disconnected before a complete frame")
        data.extend(chunk)
    return bytes(data)


def receive(connection, options):
    if options.mode == "length":
        size = struct.unpack("!I", read_exact(connection, 4))[0]
        if size > options.maximum_bytes:
            raise ValueError("Message exceeds maximum bytes")
        return read_exact(connection, size).decode(options.encoding, errors="strict")
    delimiter = options.delimiter.replace("\\r", "\r").replace("\\n", "\n").replace("\\t", "\t").encode(options.encoding)
    data = bytearray()
    while True:
        data.extend(read_exact(connection, 1))
        if data.endswith(delimiter) and (options.encoding != "utf-16-le" or len(data) % 2 == 0):
            body = data[:-len(delimiter)]
            if len(body) > options.maximum_bytes:
                raise ValueError("Message exceeds maximum bytes")
            return body.decode(options.encoding, errors="strict")
        if len(data) > options.maximum_bytes + len(delimiter) - 1:
            raise ValueError("Message exceeds maximum bytes")


def frame(message, options):
    body = message.encode(options.encoding, errors="strict")
    if len(body) > options.maximum_bytes:
        raise ValueError("Reply exceeds maximum bytes")
    if options.mode == "length":
        return struct.pack("!I", len(body)) + body
    delimiter = options.delimiter.replace("\\r", "\r").replace("\\n", "\n").replace("\\t", "\t").encode(options.encoding)
    position = body.find(delimiter)
    while position >= 0:
        if options.encoding != "utf-16-le" or position % 2 == 0:
            raise ValueError("Reply contains delimiter")
        position = body.find(delimiter, position + 1)
    return body + delimiter


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=9000)
    parser.add_argument("--mode", choices=["delimiter", "length"], default="delimiter")
    parser.add_argument("--encoding", choices=["utf-8", "ascii", "utf-16-le"], default="utf-8")
    parser.add_argument("--delimiter", default="\\n")
    parser.add_argument("--maximum-bytes", type=int, default=65536)
    parser.add_argument("--timeout", type=float, default=10)
    parser.add_argument("--count", type=int, default=1)
    parser.add_argument("--reply")
    options = parser.parse_args()
    if not 0 <= options.port <= 65535 or not 1 <= options.maximum_bytes <= 1048576 or not 0 < options.timeout <= 600 or options.count < 1 or not options.delimiter:
        parser.error("Invalid port, size limit, timeout, count, or delimiter")
    delimiter = options.delimiter.replace("\\r", "\r").replace("\\n", "\n").replace("\\t", "\t").encode(options.encoding)
    if not 1 <= len(delimiter) <= 16:
        parser.error("Encoded delimiter must have 1 to 16 bytes")
    with socket.socket(socket.AF_INET6 if ":" in options.host else socket.AF_INET, socket.SOCK_STREAM) as server:
        server.settimeout(options.timeout)
        server.bind((options.host, options.port))
        server.listen(1)
        print(json.dumps({"listening": options.host, "port": server.getsockname()[1], "mode": options.mode}), flush=True)
        connection, _ = server.accept()
        with connection:
            connection.settimeout(options.timeout)
            for sequence in range(options.count):
                message = receive(connection, options)
                print(json.dumps({"sequence": sequence + 1, "message": message}, ensure_ascii=False), flush=True)
                if options.reply is not None:
                    connection.sendall(frame(options.reply, options))


if __name__ == "__main__":
    main()
