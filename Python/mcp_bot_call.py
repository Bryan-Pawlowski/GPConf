"""Reusable stdio-free streamable-HTTP client for the GPConf Discord bot MCP.

Usage:
  python mcp_bot_call.py list-tools
  python mcp_bot_call.py call <toolName> <args.json>     # prints SSE-decoded result text
  python mcp_bot_call.py call <toolName>                 # {} args

Endpoint is POST / on 127.0.0.1:5177 (NOT /mcp). Long tool timeout for
generate_and_post (the local model can take ~300s).
"""
import http.client
import json
import sys

HOST = "127.0.0.1"
PORT = 5177


def post(session_id, body_str, timeout=660):
    conn = http.client.HTTPConnection(HOST, PORT, timeout=timeout)
    headers = {"Content-Type": "application/json",
               "Accept": "application/json,text/event-stream"}
    if session_id:
        headers["Mcp-Session-Id"] = session_id
    conn.request("POST", "/", body=body_str, headers=headers)
    resp = conn.getresponse()
    status = resp.status
    sid = resp.getheader("Mcp-Session-Id")
    body = resp.read().decode("utf-8", "replace")
    conn.close()
    return status, sid, body


def parse(body):
    msgs = []
    for line in body.strip().splitlines():
        if line.startswith("data: "):
            try:
                msgs.append(json.loads(line[6:]))
            except json.JSONDecodeError:
                pass
    if not msgs:
        try:
            msgs.append(json.loads(body))
        except json.JSONDecodeError:
            pass
    return msgs


def main():
    cmd = sys.argv[1]
    status, sid, body = post(None, json.dumps({
        "jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                   "clientInfo": {"name": "hermes-kickoff", "version": "1.0"}}}))
    if status != 200:
        print(f"init failed: {status} {body[:300]}", file=sys.stderr)
        return 1
    post(sid, json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}))

    if cmd == "list-tools":
        _, _, body = post(sid, json.dumps({"jsonrpc": "2.0", "id": 2,
                                           "method": "tools/list", "params": {}}))
        for m in parse(body):
            for t in m.get("result", {}).get("tools", []):
                print(t["name"])
        return 0

    if cmd == "call":
        name = sys.argv[2]
        args = {}
        if len(sys.argv) > 3:
            with open(sys.argv[3], encoding="utf-8-sig") as f:
                args = json.load(f)
        st, _, body = post(sid, json.dumps({
            "jsonrpc": "2.0", "id": 3, "method": "tools/call",
            "params": {"name": name, "arguments": args}}))
        out = []
        err = False
        for m in parse(body):
            if "error" in m:
                print(json.dumps(m["error"], indent=2))
                err = True
            for item in m.get("result", {}).get("content", []):
                if item.get("type") == "text":
                    out.append(item["text"])
            if m.get("result", {}).get("isError"):
                err = True
        print("\n".join(out) if out else f"(no text content; http {st}) body={body[:800]}")
        return 1 if err else 0

    print("unknown command", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
