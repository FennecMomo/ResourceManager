"""Check the packaged ResourceManager stdio MCP tool catalog without a GUI."""

import json
import os
import queue
import subprocess
import sys
import threading


def main() -> None:
    executable = sys.argv[1]
    environment = os.environ.copy()
    if len(sys.argv) > 3:
        environment["RESOURCEMANAGER_CLIENT_PID"] = sys.argv[3]
    process = subprocess.Popen(
        [executable, "--mcp"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        encoding="utf-8",
        env=environment,
    )
    responses: queue.Queue[dict] = queue.Queue()

    def read() -> None:
        assert process.stdout is not None
        for line in process.stdout:
            responses.put(json.loads(line))

    threading.Thread(target=read, daemon=True).start()

    def request(message: dict) -> None:
        assert process.stdin is not None
        process.stdin.write(json.dumps(message, separators=(",", ":")) + "\n")
        process.stdin.flush()

    try:
        request({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-03-26", "capabilities": {},
            "clientInfo": {"name": "mcp-smoke", "version": "1"}}})
        initialized = responses.get(timeout=10)
        assert initialized.get("id") == 1 and "result" in initialized, initialized
        request({"jsonrpc": "2.0", "method": "notifications/initialized"})
        request({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
        listed = responses.get(timeout=10)
        assert listed.get("id") == 2 and "result" in listed, listed
        names = {tool["name"] for tool in listed["result"]["tools"]}
        assert len(names) == 38, f"expected 38 MCP tools, found {len(names)}"
        for name in ("resource_manager_download_resource",
                     "resource_manager_send_private_resource",
                     "resource_manager_upload_to_server",
                     "resource_manager_check_updates"):
            assert name in names, name
        print(f"PASS: MCP stdio advertised {len(names)} unique tools")
        if len(sys.argv) > 2:
            request({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {
                "name": "resource_manager_status", "arguments": {}}})
            called = responses.get(timeout=20)
            assert called.get("id") == 3 and "result" in called, called
            status = json.loads(called["result"]["content"][0]["text"])
            assert status["version"] == sys.argv[2], status
            print(f"PASS: MCP status reached running client version {status['version']}")
    finally:
        if process.stdin:
            process.stdin.close()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=5)


if __name__ == "__main__":
    main()
