#!/usr/bin/env bash
#
# probe-gateway.sh — prove what the PRODUCTION gateway actually does to a request, without going
# anywhere near the real stack.
#
# Why this exists
# ---------------
# Production runs the labs.fa.reel-gateway image: nginx/Dockerfile with nginx/nginx.conf baked in as an
# envsubst template. On 2026-09-10 the production gateway config of the day (then an inline copy in
# a second production file, since removed) was found to be rewriting `/api/…` to `/…` before the
# request reached the backend — a `proxy_pass` whose URI ends in a slash replaces the matched
# location prefix. Every API call on the deployed host would have 404'd. Reading the file did not
# reveal it; running it did. So this runs the IMAGE — template rendering, ENV defaults and all.
#
# So: this script builds nginx/Dockerfile exactly as the publish jobs do (context ./nginx), stands
# it up against a stub upstream that echoes back the path it was actually handed, and prints both
# the status code and that path. Pass an image reference to probe a published image instead
# (e.g. frostaura/labs.fa.reel-gateway:sha-abc1234) — it is then pulled, not built.
#
# What it deliberately does NOT do
# --------------------------------
# It never touches the repository's compose project. A second `backend` container against the live
# database silently claims Train/BuildFeed jobs off the shared `SyncJobs` queue (FOR UPDATE SKIP
# LOCKED) and writes stale artifacts — this has happened. Everything here runs in a throwaway
# compose project named `askreel-deploy-probe`, from a generated file in a temp directory, on host
# ports 8188/8189 only, with two `nginx:alpine` stubs, the gateway image and no database at all. It
# tears the project down on exit, including on failure.
#
# Usage:   deploy/probe-gateway.sh [gateway image reference]
# Exit:    0 when the upstream saw a path starting with /api, 1 otherwise.

set -euo pipefail

PROJECT="askreel-deploy-probe"
GATEWAY_PORT=8188
UPSTREAM_PORT=8189

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE="${1:-}"
BUILT=""

for port in "$GATEWAY_PORT" "$UPSTREAM_PORT"; do
  if lsof -nP -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
    echo "Port $port is already in use — refusing to start, since the probe would then be reading someone else's server." >&2
    exit 2
  fi
done

WORK="$(mktemp -d "${TMPDIR:-/tmp}/askreel-gateway-probe.XXXXXX")"

teardown() {
  docker compose -f "$WORK/docker-compose.yml" -p "$PROJECT" down -v --remove-orphans >/dev/null 2>&1 || true
  [ -n "$BUILT" ] && docker image rm "$BUILT" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap teardown EXIT

# --- 1. The gateway image, built exactly as CI publishes it ---------------------------------------
if [ -z "$IMAGE" ]; then
  IMAGE="labs.fa.reel-gateway:probe-$$"
  docker build -q -t "$IMAGE" -f "$REPO_ROOT/nginx/Dockerfile" "$REPO_ROOT/nginx" >/dev/null
  BUILT="$IMAGE"
fi

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

# Service names must match the ones the config's `proxy_pass` targets (`backend`, `frontend`).
# The gateway resolves them per request through Docker's DNS, so start order does not matter.
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
    image: $IMAGE
    ports:
      - "$GATEWAY_PORT:80"
    depends_on:
      - backend
      - frontend
COMPOSE

compose() { docker compose -f "$WORK/docker-compose.yml" -p "$PROJECT" "$@"; }

echo "== probing $IMAGE in project $PROJECT on :$GATEWAY_PORT (upstream :$UPSTREAM_PORT) =="
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

ask_code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "localhost:$GATEWAY_PORT/api/search/ask" || true)"
ask_body="$(curl -s -X POST "localhost:$GATEWAY_PORT/api/search/ask" || true)"
echo "POST /api/search/ask through the gateway: HTTP $ask_code -> $ask_body"

map_code="$(curl -s -o /dev/null -w '%{http_code}' "localhost:$GATEWAY_PORT/assets/index.js.map" || true)"
echo "GET /assets/index.js.map                : HTTP $map_code (must be 404)"

headers="$(curl -s -D - -o /dev/null "localhost:$GATEWAY_PORT/" || true)"
server_header="$(printf '%s' "$headers" | tr -d '\r' | sed -n 's/^[Ss]erver: //p')"
nosniff="$(printf '%s' "$headers" | tr -d '\r' | grep -ci '^x-content-type-options: nosniff' || true)"
echo "Server header                           : ${server_header:-none} (no version expected)"
echo "X-Content-Type-Options: nosniff         : $nosniff"

echo "--- upstream access log ---"
compose logs backend 2>/dev/null | grep "UPSTREAM SAW" || echo "(no upstream requests logged)"
echo "---------------------------"

ask_seen="$(printf '%s' "$ask_body" | sed -n 's/.*"upstreamSaw":"\([^"]*\)".*/\1/p')"
if [ "$code" = "200" ] && [ "${seen#/api}" != "$seen" ] && [ "$ask_seen" = "/api/search/ask" ] \
  && [ "$map_code" = "404" ] && [ "$server_header" = "nginx" ] && [ "$nosniff" = "1" ]; then
  echo "PASS: /api and /api/search/ask are preserved end to end, source maps are refused and the headers are set."
  exit 0
fi

echo "FAIL: the gateway did not hand '/api/health' through intact (status $code, upstream saw '${seen:-nothing}')."
exit 1
