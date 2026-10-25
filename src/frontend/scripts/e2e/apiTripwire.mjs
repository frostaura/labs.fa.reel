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

const server = http.createServer((req, res) => {
  const { pathname } = new URL(req.url ?? "/", "http://tripwire.invalid");
  if (pathname === "/__tripwire/health") {
    res.writeHead(200, { "Content-Type": "text/plain" }).end("tripwire");
    return;
  }
  appendFileSync(logFile, `${JSON.stringify({ at: Date.now(), method: req.method, path: pathname })}\n`);
  req.resume();
  res.writeHead(502, { "Content-Type": "text/plain" }).end();
});

server.listen(port, "127.0.0.1");
// Exit at once: Vite's proxy holds keep-alive sockets open, and a graceful close would wait on them.
for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, () => process.exit(0));
