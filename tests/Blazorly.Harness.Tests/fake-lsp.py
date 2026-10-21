#!/usr/bin/env python3
"""Minimal fake LSP server for tests: Content-Length framed JSON-RPC over stdio.

Responds to initialize (any id) with empty capabilities, and to any
textDocument/definition request with a fixed location at line 9 (0-based).
All other requests get a null result; notifications are ignored. Loops until stdin EOF.
"""
import json
import sys


def read_message():
    headers = {}
    while True:
        line = sys.stdin.buffer.readline()
        if not line:
            return None
        if line == b"\r\n":
            break
        key, _, value = line.decode("ascii").partition(":")
        headers[key.strip().lower()] = value.strip()
    body = sys.stdin.buffer.read(int(headers.get("content-length", "0")))
    return json.loads(body)


def write_message(message):
    body = json.dumps(message).encode("utf-8")
    sys.stdout.buffer.write(f"Content-Length: {len(body)}\r\n\r\n".encode("ascii") + body)
    sys.stdout.buffer.flush()


while True:
    message = read_message()
    if message is None:
        break
    if "id" not in message:
        continue
    method = message.get("method")
    if method == "initialize":
        write_message({"jsonrpc": "2.0", "id": message["id"], "result": {"capabilities": {}}})
    elif method == "textDocument/definition":
        write_message({
            "jsonrpc": "2.0",
            "id": message["id"],
            "result": {"uri": "file:///tmp/x.cs", "range": {"start": {"line": 9, "character": 0}}},
        })
    else:
        write_message({"jsonrpc": "2.0", "id": message["id"], "result": None})
