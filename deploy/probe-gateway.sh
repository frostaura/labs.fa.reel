#!/usr/bin/env bash
#
# probe-gateway.sh — prove what the PORTAINER gateway descriptor actually does to a request,
# without going anywhere near the real stack.
#
# Why this exists
# ---------------
# `deploy/portainer-stack.yml` inlines its nginx config as a compose `configs:` block, because a
# Portainer stack cannot mount a file out of a repository it never clones. That inline copy is the
# one descriptor nobody runs locally, and on 2026-09-10 it was found to be rewriting `/api/…` to
# `/…` before the request reached the backend — a `proxy_pass` whose URI ends in a slash replaces
# the matched location prefix. Every API call on the deployed host would have 404'd. Reading the
# file did not reveal it; running it did.
#
# So: this script extracts the gateway config from the Portainer descriptor VERBATIM, stands it up
# against a stub upstream that echoes back the path it was actually handed, and prints both the
# status code and that path.
#
# What it deliberately does NOT do
# --------------------------------
# It never touches the repository's compose project. A second `backend` container against the live
# database silently claims Train/BuildFeed jobs off the shared `SyncJobs` queue (FOR UPDATE SKIP
# LOCKED) and writes stale artifacts — this has happened. Everything here runs in a throwaway
# compose project named `askreel-deploy-probe`, from a generated file in a temp directory, on host
# ports 8188/8189 only, with three `nginx:alpine` containers and no database at all. It tears the
# project down on exit, including on failure.
#
# Usage:   deploy/probe-gateway.sh [path/to/portainer-stack.yml]
# Exit:    0 when the upstream saw a path starting with /api, 1 otherwise.

set -euo pipefail

PROJECT="askreel-deploy-probe"
GATEWAY_PORT=8188
UPSTREAM_PORT=8189

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DESCRIPTOR="${1:-$REPO_ROOT/deploy/portainer-stack.yml}"

[ -f "$DESCRIPTOR" ] || { echo "No such descriptor: $DESCRIPTOR" >&2; exit 2; }

for port in "$GATEWAY_PORT" "$UPSTREAM_PORT"; do
  if lsof -nP -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
    echo "Port $port is already in use — refusing to start, since the probe would then be reading someone else's server." >&2
    exit 2
  fi
done

WORK="$(mktemp -d "${TMPDIR:-/tmp}/askreel-gateway-probe.XXXXXX")"

teardown() {
  docker compose -f "$WORK/docker-compose.yml" -p "$PROJECT" down -v --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap teardown EXIT

# --- 1. Lift the nginx config out of the descriptor, verbatim ------------------------------------
# The block lives under `configs: <name>: content: |`. Compose escapes nginx's runtime variables as
# `$$` so its own interpolation leaves them alone; compose would un-escape them itself, and so does
# this, because the config is bind-mounted as a plain file rather than shipped as a compose config.
python3 - "$DESCRIPTOR" > "$WORK/gateway.conf" <<'PY'
import re, sys, pathlib

lines = pathlib.Path(sys.argv[1]).read_text().splitlines()
start = next((i for i, l in enumerate(lines) if re.match(r"^\s*content:\s*\|\s*$", l)), None)
if start is None:
    sys.exit("No `content: |` block found in the descriptor — has the gateway config moved?")

indent = len(lines[start]) - len(lines[start].lstrip())
body = []
for line in lines[start + 1:]:
    if line.strip() and (len(line) - len(line.lstrip())) <= indent:
        break
    body.append(line[indent + 2:] if line.strip() else "")

print("\n".join(body).replace("$$", "$"))
PY

grep -q "listen 80" "$WORK/gateway.conf" || { echo "Extracted config does not look like the gateway's:" >&2; cat "$WORK/gateway.conf" >&2; exit 2; }

# --- 2. A stub upstream that reports the path it was handed ---------------------------------------
# `$request_uri` is the ORIGINAL path plus query string as this server received it — which is
# exactly the thing under test. It goes in the response body so curl alone proves the point, and in
# the access log so `docker compose logs` shows the request line too.
cat > "$WORK/stub-backend.conf" <<'CONF'
events {}
http {
  log_format probe 'UPSTREAM SAW: "$request_method $request_uri $server_protocol" host=$host';
  access_log /dev/stdout probe;
  server {
    listen 5000;
    default_type application/json;
    location / { return 200 '{"stub":"backend","upstreamSaw":"$request_uri"}\n'; }
  }
}
CONF

cat > "$WORK/stub-frontend.conf" <<'CONF'
events {}
http {
  access_log off;
  server {
    listen 80;
    default_type text/plain;
    location / { return 200 'stub frontend\n'; }
  }
}
CONF

# Service names must match the ones the descriptor's `proxy_pass` targets: nginx resolves a literal
# upstream host once, at config load, so the stubs also have to be listening before the gateway
# starts.
cat > "$WORK/docker-compose.yml" <<COMPOSE
services:
  backend:
    image: nginx:alpine
    volumes:
      - $WORK/stub-backend.conf:/etc/nginx/nginx.conf:ro
    ports:
      - "$UPSTREAM_PORT:5000"
  frontend:
    image: nginx:alpine
    volumes:
      - $WORK/stub-frontend.conf:/etc/nginx/nginx.conf:ro
  gateway:
    image: nginx:alpine
    volumes:
      - $WORK/gateway.conf:/etc/nginx/nginx.conf:ro
    ports:
      - "$GATEWAY_PORT:80"
    depends_on:
      - backend
      - frontend
COMPOSE

compose() { docker compose -f "$WORK/docker-compose.yml" -p "$PROJECT" "$@"; }

echo "== probing $(basename "$DESCRIPTOR") in project $PROJECT on :$GATEWAY_PORT (upstream :$UPSTREAM_PORT) =="
compose up -d --quiet-pull >/dev/null

for _ in $(seq 1 40); do
  [ "$(curl -s -o /dev/null -w '%{http_code}' "localhost:$UPSTREAM_PORT/ping" || true)" != "000" ] && break
  sleep 0.5
done

# --- 3. The measurements --------------------------------------------------------------------------
direct="$(curl -s "localhost:$UPSTREAM_PORT/api/health" || true)"
echo "baseline  (straight to the stub)         : $direct"

code="$(curl -s -o /dev/null -w '%{http_code}' "localhost:$GATEWAY_PORT/api/health" || true)"
body="$(curl -s "localhost:$GATEWAY_PORT/api/health" || true)"
echo "GET /api/health through the gateway     : HTTP $code"
echo "what the upstream was handed            : $body"

seen="$(printf '%s' "$body" | sed -n 's/.*"upstreamSaw":"\([^"]*\)".*/\1/p')"

# The streaming locations are worth one line each: a missing `location /sse/` is a 404 from the
# frontend catch-all, not from the backend, and that is visible in the stub's answer.
sse_code="$(curl -s -o /dev/null -w '%{http_code}' "localhost:$GATEWAY_PORT/sse/pipeline" || true)"
sse_body="$(curl -s "localhost:$GATEWAY_PORT/sse/pipeline" || true)"
echo "GET /sse/pipeline through the gateway   : HTTP $sse_code -> $sse_body"

health_body="$(curl -s "localhost:$GATEWAY_PORT/health/ready" || true)"
echo "GET /health/ready through the gateway   : $health_body"

echo "--- upstream access log ---"
compose logs backend 2>/dev/null | grep "UPSTREAM SAW" || echo "(no upstream requests logged)"
echo "---------------------------"

if [ "$code" = "200" ] && [ "${seen#/api}" != "$seen" ]; then
  echo "PASS: /api is preserved end to end (upstream saw '$seen')."
  exit 0
fi

echo "FAIL: the gateway did not hand '/api/health' through intact (status $code, upstream saw '${seen:-nothing}')."
exit 1
