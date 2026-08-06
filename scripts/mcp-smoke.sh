#!/usr/bin/env bash
# Smoke-test the MCP Streamable HTTP endpoint (stateless mode).
set -euo pipefail
BASE="http://localhost:5171/mcp"
ACCEPT="Accept: application/json, text/event-stream"
CT="Content-Type: application/json"

echo "=== tools/list ==="
curl -s -X POST "$BASE" -H "$CT" -H "$ACCEPT" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' \
  | tr '\n' ' ' | sed 's/data: //g'
echo; echo

echo "=== tools/call search_docs ==="
curl -s -X POST "$BASE" -H "$CT" -H "$ACCEPT" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search_docs","arguments":{"query":"how to create an MCP server with stdio transport","topK":3}}}' \
  | tr '\n' ' ' | sed 's/data: //g'
echo; echo

echo "=== tools/call list_repositories ==="
curl -s -X POST "$BASE" -H "$CT" -H "$ACCEPT" \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_repositories","arguments":{}}}' \
  | tr '\n' ' ' | sed 's/data: //g'
echo
