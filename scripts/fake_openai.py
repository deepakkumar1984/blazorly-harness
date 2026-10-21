#!/usr/bin/env python3
"""A deterministic OpenAI-compatible stand-in for the scripted eval tasks.

`eval/tasks/interrupt-*` and `eval/tasks/recover-tool-failure` pin `provider: "scripted"` so a
score measures the harness contract (interruption durability, tool-failure recovery) rather than
whatever a live model felt like doing. This server is the C# `FakeOpenAiServer` used by the test
suite, in Python, so `blazorly eval` can run those tasks without a live provider:

    python3 scripts/fake_openai.py            # listens on 127.0.0.1:8931, prints the base URL

Then point a provider route at it (Settings → custom provider, or ~/.blazorly/settings.json):

    {"provider": "scripted", "model": "test", "baseUrl": "http://127.0.0.1:8931/v1",
     "customProviders": [{"name": "scripted", "baseUrl": "http://127.0.0.1:8931/v1",
                          "apiKey": "test-key", "models": ["test"]}]}

Two flows, selected by the prompt text so one server serves every scripted task:

* default — one assistant step with `bash` + `todo_write`, then a summary once tool results return.
* a prompt containing RECOVER_AFTER_TOOL_FAILURE — a `read` of a file that does not exist (a durable
  tool error), then a `write` recovery in the same turn, then the summary.
"""

from __future__ import annotations

import argparse
import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

FAILURE_MARKER = "RECOVER_AFTER_TOOL_FAILURE"

USAGE = {
    "prompt_tokens": 80,
    "completion_tokens": 30,
    "total_tokens": 110,
    "prompt_tokens_details": {"cached_tokens": 0},
    "completion_tokens_details": {"reasoning_tokens": 0},
}


def sse(payload: dict | str) -> str:
    return f"data: {payload if isinstance(payload, str) else json.dumps(payload)}\n\n"


def delta(content: dict | None = None, finish: str | None = None, usage: dict | None = None) -> dict:
    choice: dict = {"delta": content or {}}
    if finish is not None:
        choice["finish_reason"] = finish
    event: dict = {"choices": [choice]}
    if usage is not None:
        event["usage"] = usage
    return event


def tool_call(call_id: str, name: str, arguments: dict) -> dict:
    return delta({
        "tool_calls": [{
            "index": 0,
            "id": call_id,
            "type": "function",
            "function": {"name": name, "arguments": json.dumps(arguments)},
        }],
    })


def tool_call_stream() -> list[str]:
    """The canonical two-tool first step (bash + todo_write), mirroring the C# fake server."""
    return [
        sse(delta({
            "tool_calls": [
                {
                    "index": 0,
                    "id": "call_bash",
                    "type": "function",
                    "function": {
                        "name": "bash",
                        "arguments": json.dumps({
                            "command": 'sleep 2.5 && echo "hello from blazorly harness" && date',
                            "description": "Greet and print the date",
                        }),
                    },
                },
                {
                    "index": 1,
                    "id": "call_todo",
                    "type": "function",
                    "function": {
                        "name": "todo_write",
                        "arguments": json.dumps({
                            "todos": [
                                {"content": "Run the scripted greeting", "status": "completed"},
                                {"content": "Summarize the result", "status": "in_progress"},
                            ],
                        }),
                    },
                },
            ],
        })),
        sse(delta(finish="tool_calls", usage=USAGE)),
        sse("[DONE]"),
    ]


def failing_tool_call_stream() -> list[str]:
    """Step 1 of the recovery flow: a read that errors, so the failure is durable in the log."""
    return [
        sse(tool_call("call_missing", "read", {"file_path": "missing-input.txt"})),
        sse(delta(finish="tool_calls", usage=USAGE)),
        sse("[DONE]"),
    ]


def recovery_tool_call_stream() -> list[str]:
    """Step 2 of the recovery flow: the successful write that recovers the same turn."""
    return [
        sse(tool_call("call_recover", "write", {
            "file_path": "recovery.txt",
            "content": "recovered after the failed read\n",
        })),
        sse(delta(finish="tool_calls", usage=USAGE)),
        sse("[DONE]"),
    ]


def summary_stream() -> list[str]:
    return [
        sse(delta({"content": "The scripted run completed: I executed `bash` and updated the todo list."})),
        sse(delta(finish="stop", usage=USAGE)),
        sse("[DONE]"),
    ]


def count_tool_results(body: str) -> int:
    return body.count('"role":"tool"') + body.count('"role": "tool"')


def stream_for(body: str) -> list[str]:
    results = count_tool_results(body)
    if FAILURE_MARKER in body:
        if results == 0:
            return failing_tool_call_stream()
        if results == 1:
            return recovery_tool_call_stream()
        return summary_stream()
    return summary_stream() if results > 0 else tool_call_stream()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, format: str, *args) -> None:  # noqa: A002 - stdlib signature
        if self.server.verbose:  # type: ignore[attr-defined]
            sys.stderr.write(f"[fake-openai] {format % args}\n")

    def do_GET(self) -> None:  # noqa: N802 - stdlib naming
        if self.path.endswith("/models"):
            body = json.dumps({"object": "list", "data": [{"id": "test"}]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        self.send_error(404)

    def do_POST(self) -> None:  # noqa: N802 - stdlib naming
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length).decode("utf-8", "replace") if length else ""
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()
        try:
            for event in stream_for(body):
                chunk = event.encode()
                self.wfile.write(f"{len(chunk):X}\r\n".encode() + chunk + b"\r\n")
                self.wfile.flush()
            self.wfile.write(b"0\r\n\r\n")
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass  # the client hung up (an interrupt task kills the child on purpose)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8931)
    parser.add_argument("--verbose", action="store_true", help="log each request")
    args = parser.parse_args()

    server = ThreadingHTTPServer((args.host, args.port), Handler)
    server.verbose = args.verbose  # type: ignore[attr-defined]
    server.daemon_threads = True
    base_url = f"http://{args.host}:{args.port}/v1"
    print(f"fake openai-compatible server listening; baseUrl = {base_url}", flush=True)
    print(f"provider: scripted · model: test · failure flow marker: {FAILURE_MARKER}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        threading.Thread(target=server.shutdown, daemon=True).start()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
