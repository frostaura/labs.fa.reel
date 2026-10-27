#!/usr/bin/env bash
#
# probe-ask-stream.sh — prove that the PRODUCTION gateway relays an Ask Reel turn as it is written:
# every frame (the 5 s `phase` heartbeat included) reaches the client as soon as the API sends it,
# and nothing is gzipped (WS-8 task 9 acceptance, I-13).
#
# Why this exists
# ---------------
# A buffered or compressed stream does not fail; it arrives late and all at once. The founder sees
# a spinner for the whole turn and then every card at the end, and the heartbeat that exists to
# keep cloudflared from cutting a silent stream never reaches cloudflared. Reading nginx.conf does
# not show it (gzip is on for /api/, and the Ask location turns it off; proxy_buffering is on by
# default); timing a stream through it does.
#
# What it runs
# ------------
# The gateway config is lifted from docker-compose.prod.yml VERBATIM (the same extraction as
# deploy/probe-gateway.sh) and run by a NATIVE nginx on loopback, against a stub upstream that
# writes one frame, then stays silent for 5 s and writes a heartbeat, twice, then `done`. No Docker,
# no database, no API, no model call. Only what a native run needs is rewritten: listen address,
# log, pid and temp paths, mime.types, and the upstream hostnames and port (Docker DNS names →
# 127.0.0.1). The locations, gzip and buffering settings under test are untouched.
#
# The client asks for gzip, as a browser does, and timestamps every frame on arrival. A frame
# that arrives more than 1 s after the stub wrote it, or a Content-Encoding on the response, fails.
#
# Not covered: cloudflared. The same check through the tunnel needs the live host (G0).
#
# Usage:   deploy/probe-ask-stream.sh [path/to/docker-compose.prod.yml]
# Needs:   nginx and python3 on PATH; ports 8198 and 8199 free.
# Exit:    0 when every frame arrived on time and uncompressed, 1 otherwise, 2 on setup failure.

set -euo pipefail

GATEWAY_PORT=8198
UPSTREAM_PORT=8199

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DESCRIPTOR="${1:-$REPO_ROOT/docker-compose.prod.yml}"

[ -f "$DESCRIPTOR" ] || { echo "No such descriptor: $DESCRIPTOR" >&2; exit 2; }
command -v nginx > /dev/null || { echo "probe-ask-stream: needs nginx on PATH (brew install nginx)." >&2; exit 2; }

for port in "$GATEWAY_PORT" "$UPSTREAM_PORT"; do
  if lsof -nP -iTCP:"$port" -sTCP:LISTEN > /dev/null 2>&1; then
    echo "Port $port is already in use — refusing to start, since the probe would then be reading someone else's server." >&2
    exit 2
  fi
done

WORK="$(mktemp -d "${TMPDIR:-/tmp}/askreel-stream-probe.XXXXXX")"
STUB_PID=""

teardown() {
  [ -f "$WORK/nginx.pid" ] && kill "$(cat "$WORK/nginx.pid")" 2> /dev/null || true
  [ -n "$STUB_PID" ] && kill "$STUB_PID" 2> /dev/null || true
  rm -rf "$WORK"
}
trap teardown EXIT

MIME_TYPES="$(dirname "$(nginx -V 2>&1 | tr ' ' '\n' | sed -n 's/^--conf-path=//p')")/mime.types"
[ -f "$MIME_TYPES" ] || { echo "probe-ask-stream: cannot find nginx's mime.types (looked at $MIME_TYPES)." >&2; exit 2; }

# --- 1. The gateway config, verbatim, adapted only where a native run must differ ----------------
python3 - "$DESCRIPTOR" "$WORK" "$GATEWAY_PORT" "$UPSTREAM_PORT" "$MIME_TYPES" > "$WORK/gateway.conf" <<'PY'
import re, sys, pathlib

descriptor, work, gateway_port, upstream_port, mime_types = sys.argv[1:6]
lines = pathlib.Path(descriptor).read_text().splitlines()
named = next((i for i, l in enumerate(lines) if re.match(r"^\s*gateway_nginx:\s*$", l)), None)
start = None if named is None else next(
    (i for i in range(named + 1, len(lines)) if re.match(r"^\s*content:\s*\|\s*$", lines[i])), None)
if start is None:
    sys.exit("No `gateway_nginx: content: |` block found in the descriptor — has the gateway config moved?")

indent = len(lines[start]) - len(lines[start].lstrip())
body = []
for line in lines[start + 1:]:
    if line.strip() and (len(line) - len(line.lstrip())) <= indent:
        break
    body.append(line[indent + 2:] if line.strip() else "")
conf = "\n".join(body).replace("$$", "$")

def swap(old, new, count=1):
    global conf
    if conf.count(old) < count:
        sys.exit(f"The gateway config no longer contains {old!r}; update this probe's native rewrites.")
    conf = conf.replace(old, new)

swap("error_log /var/log/nginx/error.log warn;", f"error_log {work}/error.log warn;")
swap("pid /tmp/nginx.pid;", f"pid {work}/nginx.pid;")
swap("include /etc/nginx/mime.types;",
     f"include {mime_types};\n"
     f"    client_body_temp_path {work}/body; proxy_temp_path {work}/proxy;\n"
     f"    fastcgi_temp_path {work}/fastcgi; uwsgi_temp_path {work}/uwsgi; scgi_temp_path {work}/scgi;")
swap("access_log /var/log/nginx/access.log main;", f"access_log {work}/access.log main;")
swap("listen 80;", f"listen 127.0.0.1:{gateway_port};")
swap("set $backend_host backend;", "set $backend_host 127.0.0.1;")
swap("set $frontend_host frontend;", "set $frontend_host 127.0.0.1;")
swap("$backend_host:5000", f"$backend_host:{upstream_port}", count=4)
swap("$frontend_host:80", f"$frontend_host:{upstream_port}")
print(conf)
PY

grep -q "location = /api/search/ask" "$WORK/gateway.conf" || { echo "The gateway config has no Ask location." >&2; exit 2; }

# --- 2. A stub API that streams an Ask turn with the API's own timing -----------------------------
# One `phase` frame at once, then 5 s of silence before each heartbeat (SearchEndpoints'
# HeartbeatAfter), then `done`. Each frame carries the time it was written. 2 KB of padding per
# frame keeps every frame above gzip_min_length, so a gzip that applied would apply to it.
cat > "$WORK/stub.py" <<'PY'
import json, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PAD = "x" * 2048

class Stub(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"  # no length: the body ends when the connection closes

    def log_message(self, *args):
        pass

    def do_POST(self):
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        frames = [("phase", 0), ("phase", 5), ("phase", 5), ("done", 2)]
        for i, (event, wait) in enumerate(frames):
            time.sleep(wait)
            data = {"n": i, "sentAt": time.time(), "heartbeat": i in (1, 2), "pad": PAD}
            self.wfile.write(f"event: {event}\ndata: {json.dumps(data)}\n\n".encode())
            self.wfile.flush()

ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), Stub).serve_forever()
PY
python3 -I "$WORK/stub.py" "$UPSTREAM_PORT" &
STUB_PID=$!

mkdir -p "$WORK"/{body,proxy,fastcgi,uwsgi,scgi}
nginx -t -q -p "$WORK" -c "$WORK/gateway.conf" 2> "$WORK/nginx-t.log" \
  || { grep -v '"user" directive' "$WORK/nginx-t.log" >&2; exit 2; }
nginx -p "$WORK" -c "$WORK/gateway.conf" 2> /dev/null

for _ in $(seq 1 50); do
  lsof -nP -iTCP:"$GATEWAY_PORT" -sTCP:LISTEN > /dev/null 2>&1 && lsof -nP -iTCP:"$UPSTREAM_PORT" -sTCP:LISTEN > /dev/null 2>&1 && break
  sleep 0.1
done

# --- 3. Time every frame through the gateway ------------------------------------------------------
echo "== POST /api/search/ask through $(basename "$DESCRIPTOR")'s gateway on :$GATEWAY_PORT (stub API :$UPSTREAM_PORT) =="
python3 -I - "$GATEWAY_PORT" <<'PY'
import http.client, json, sys, time

conn = http.client.HTTPConnection("127.0.0.1", int(sys.argv[1]), timeout=30)
conn.request("POST", "/api/search/ask", body="{}", headers={
    "Content-Type": "application/json", "Accept": "text/event-stream", "Accept-Encoding": "gzip, br"})
response = conn.getresponse()
encoding = response.getheader("Content-Encoding")
print(f"status {response.status}, Content-Type {response.getheader('Content-Type')}, Content-Encoding {encoding or '(none)'}")

failures = []
if response.status != 200:
    failures.append(f"status {response.status}")
if encoding:
    failures.append(f"the stream was compressed ({encoding})")

buffer, frames, started = b"", [], time.time()
while True:
    chunk = response.read1(65536)
    if not chunk:
        break
    arrived = time.time()
    buffer += chunk
    while b"\n\n" in buffer and not encoding:
        raw, buffer = buffer.split(b"\n\n", 1)
        data = json.loads(raw.split(b"data: ", 1)[1])
        lag = arrived - data["sentAt"]
        frames.append(lag)
        label = "heartbeat" if data["heartbeat"] else raw.split(b"\n", 1)[0].decode().removeprefix("event: ")
        print(f"frame {data['n']} ({label}) at +{arrived - started:5.2f}s, {lag * 1000:6.0f} ms after the API wrote it")

if not encoding and len(frames) != 4:
    failures.append(f"{len(frames)} of 4 frames arrived")
late = [lag for lag in frames if lag > 1.0]
if late:
    failures.append(f"{len(late)} frame(s) held back by more than 1 s (buffered)")

if failures:
    print("FAIL: " + "; ".join(failures))
    sys.exit(1)
print("PASS: every Ask frame, both heartbeats included, reached the client within 1 s of being written, uncompressed.")
PY
