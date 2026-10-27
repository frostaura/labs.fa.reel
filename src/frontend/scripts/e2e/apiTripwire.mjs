#!/usr/bin/env node
/**
 * The e2e suite's only backend (trust-v1 R1-21). Playwright starts it beside the Vite server and
 * points Vite's /api and /sse proxy at it, so a request a spec forgot to mock can never reach a
 * real API — the founder's dev API on :5054 included (§7: UI tests make no live calls).
 *
 * Every request is answered exactly as Vite answers a proxy target that refuses the connection
 * (502, text/plain, empty body), so app behaviour under an unmocked call is unchanged; the
 * difference is that the call is now recorded. Each hit is appended as one JSON line to the log
 * file named in argv[3]; the unmocked-API reporter attributes them to tests and fails the run.
 * Only the method and pathname are kept — never the query string, headers or body.
 *
 * Server-layer mocks (PH-12). A spec that must answer requests no Playwright route can see — a
 * service worker's own fetches in WebKit, which `context.route` never intercepts — runs its own
 * mock server and hands /api to it: `PUT /__tripwire/forward` with `{"target":"http://127.0.0.1:N"}`
 * forwards every request there, unrecorded, because the spec answers it; `DELETE /__tripwire/forward`
 * goes back to recording. Only a loopback http target is accepted, so a spec cannot point the
 * suite at a real API through this door. If the target is gone, the request is answered with the
 * tripwire's own 502 and recorded, as an unmocked call.
 *
 * Usage: node scripts/e2e/apiTripwire.mjs <port> <log file>
 */
import { appendFileSync, writeFileSync } from "node:fs";
import http from "node:http";

const port = Number(process.argv[2]);
const logFile = process.argv[3];
if (!Number.isInteger(port) || port <= 0 || !logFile) {
  console.error("apiTripwire: usage: apiTripwire.mjs <port> <log file>");
  process.exit(2);
}

// A fresh run starts from an empty log; a stale one would blame this run's tests.
writeFileSync(logFile, "");

/** Where /api goes while a spec's own mock server answers it; null = record and refuse. */
let forwardTo = null;

/** A loopback http origin, or null: anything else could reach a real API. */
function loopbackTarget(raw) {
  try {
    const url = new URL(raw);
    if (url.protocol !== "http:" || !["127.0.0.1", "[::1]", "localhost"].includes(url.hostname)) return null;
    return url.origin;
  } catch {
    return null;
  }
}

function record(req, res, pathname) {
  appendFileSync(logFile, `${JSON.stringify({ at: Date.now(), method: req.method, path: pathname })}\n`);
  req.resume();
  if (!res.headersSent) res.writeHead(502, { "Content-Type": "text/plain" });
  res.end();
}

function control(req, res) {
  if (req.method === "DELETE") {
    forwardTo = null;
    res.writeHead(204).end();
    return;
  }
  let body = "";
  req.setEncoding("utf8");
  req.on("data", (chunk) => (body += chunk));
  req.on("end", () => {
    let target = null;
    try {
      target = loopbackTarget(JSON.parse(body).target);
    } catch {
      target = null;
    }
    if (req.method !== "PUT" || !target) {
      res.writeHead(400, { "Content-Type": "text/plain" }).end("PUT {\"target\":\"http://127.0.0.1:<port>\"}");
      return;
    }
    forwardTo = target;
    res.writeHead(204).end();
  });
}

function forward(req, res, pathname) {
  const upstream = http.request(
    `${forwardTo}${req.url}`,
    { method: req.method, headers: req.headers },
    (answer) => {
      res.writeHead(answer.statusCode ?? 502, answer.headers);
      answer.pipe(res);
    }
  );
  // Refused: the spec's mock server is gone (closed early, or a late call after its test ended), so
  // nothing answered this call — record it. Any other failure (a reset, a hang-up) comes from a live
  // mock server that took the call and dropped it on purpose, which is how a spec simulates the
  // network going down (PH-12): its answer, not an unmocked call, so it is refused unrecorded.
  upstream.on("error", (error) => {
    if (error?.code === "ECONNREFUSED") {
      record(req, res, pathname);
      return;
    }
    req.resume();
    if (!res.headersSent) res.writeHead(502, { "Content-Type": "text/plain" });
    res.end();
  });
  req.pipe(upstream);
}

const server = http.createServer((req, res) => {
  const { pathname } = new URL(req.url ?? "/", "http://tripwire.invalid");
  if (pathname === "/__tripwire/health") {
    res.writeHead(200, { "Content-Type": "text/plain" }).end("tripwire");
    return;
  }
  if (pathname === "/__tripwire/forward") {
    control(req, res);
    return;
  }
  if (forwardTo) {
    forward(req, res, pathname);
    return;
  }
  record(req, res, pathname);
});

server.listen(port, "127.0.0.1");
// Exit at once: Vite's proxy holds keep-alive sockets open, and a graceful close would wait on them.
for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, () => process.exit(0));
