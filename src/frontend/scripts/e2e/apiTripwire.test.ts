import { spawn, type ChildProcess } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import http from "node:http";
import { createServer, type AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import path from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

/**
 * The tripwire as a process: it records and refuses every /api call, and while a spec has handed
 * /api to its own loopback mock server it forwards instead, unrecorded (PH-12). Varied over the
 * three ways the hand-over can go wrong: a non-loopback target, a mock server that has gone away,
 * and a spec that hands /api back.
 */

const SCRIPT = path.resolve(__dirname, "apiTripwire.mjs");

function freePort(): Promise<number> {
  return new Promise((resolve) => {
    const probe = createServer().listen(0, "127.0.0.1", () => {
      const { port } = probe.address() as AddressInfo;
      probe.close(() => resolve(port));
    });
  });
}

function call(port: number, method: string, pathname: string, body?: string) {
  return new Promise<{ status: number; body: string; headers: http.IncomingHttpHeaders }>((resolve, reject) => {
    const req = http.request({ host: "127.0.0.1", port, method, path: pathname }, (res) => {
      let text = "";
      res.setEncoding("utf8");
      res.on("data", (c) => (text += c));
      res.on("end", () => resolve({ status: res.statusCode ?? 0, body: text, headers: res.headers }));
    });
    req.on("error", reject);
    if (body !== undefined) req.write(body);
    req.end();
  });
}

const recorded = (logFile: string) =>
  readFileSync(logFile, "utf8")
    .split("\n")
    .filter(Boolean)
    .map((line) => (JSON.parse(line) as { method: string; path: string }).path);

describe("apiTripwire", () => {
  let dir: string;
  let logFile: string;
  let port: number;
  let tripwire: ChildProcess;
  let mock: http.Server;
  let mockPort: number;
  let mockHits: string[];

  beforeEach(async () => {
    dir = mkdtempSync(path.join(tmpdir(), "tripwire-"));
    logFile = path.join(dir, "log.jsonl");
    port = await freePort();
    tripwire = spawn(process.execPath, [SCRIPT, String(port), logFile], { stdio: "ignore" });
    await expect.poll(() => call(port, "GET", "/__tripwire/health").then((r) => r.status, () => 0)).toBe(200);

    mockHits = [];
    mock = http.createServer((req, res) => {
      let body = "";
      req.setEncoding("utf8");
      req.on("data", (c) => (body += c));
      req.on("end", () => {
        mockHits.push(`${req.method} ${req.url} ${body}`);
        res.writeHead(201, { "Content-Type": "application/json", "X-Mock": "yes" }).end(JSON.stringify({ ok: true }));
      });
    });
    await new Promise<void>((resolve) => mock.listen(0, "127.0.0.1", resolve));
    mockPort = (mock.address() as AddressInfo).port;
  });

  afterEach(async () => {
    tripwire.kill("SIGTERM");
    await new Promise<void>((resolve) => mock.close(() => resolve()));
    rmSync(dir, { recursive: true, force: true });
  });

  it("records and refuses an /api call when no mock server holds /api", async () => {
    const res = await call(port, "GET", "/api/saved?x=1");
    expect(res.status).toBe(502);
    expect(recorded(logFile)).toEqual(["/api/saved"]);
  });

  it("forwards to a loopback mock server, with method, query and body, and records nothing", async () => {
    expect((await call(port, "PUT", "/__tripwire/forward", JSON.stringify({ target: `http://127.0.0.1:${mockPort}` }))).status).toBe(204);
    const res = await call(port, "POST", "/api/people/1/rating?src=card", '{"rating":8}');
    expect(res.status).toBe(201);
    expect(res.headers["x-mock"]).toBe("yes");
    expect(JSON.parse(res.body)).toEqual({ ok: true });
    expect(mockHits).toEqual(['POST /api/people/1/rating?src=card {"rating":8}']);
    expect(recorded(logFile)).toEqual([]);
  });

  it("refuses a target that is not loopback http, and keeps recording", async () => {
    for (const target of ["http://10.0.0.5:5054", "https://127.0.0.1:5054", "not a url"]) {
      expect((await call(port, "PUT", "/__tripwire/forward", JSON.stringify({ target }))).status).toBe(400);
    }
    expect((await call(port, "GET", "/api/feed")).status).toBe(502);
    expect(recorded(logFile)).toEqual(["/api/feed"]);
  });

  it("records the call as unmocked when the mock server has gone away", async () => {
    await call(port, "PUT", "/__tripwire/forward", JSON.stringify({ target: `http://127.0.0.1:${mockPort}` }));
    await new Promise<void>((resolve) => mock.close(() => resolve()));
    mock = http.createServer();
    await new Promise<void>((resolve) => mock.listen(0, "127.0.0.1", resolve));
    expect((await call(port, "GET", "/api/saved")).status).toBe(502);
    expect(recorded(logFile)).toEqual(["/api/saved"]);
  });

  it("a call the live mock server drops on purpose is the spec's outage: refused, never recorded", async () => {
    await new Promise<void>((resolve) => mock.close(() => resolve()));
    mock = http.createServer((req) => {
      req.resume();
      req.socket.destroy(); // how service-worker.preview.spec simulates the network going down
    });
    await new Promise<void>((resolve) => mock.listen(0, "127.0.0.1", resolve));
    const dropping = (mock.address() as AddressInfo).port;
    await call(port, "PUT", "/__tripwire/forward", JSON.stringify({ target: `http://127.0.0.1:${dropping}` }));
    expect((await call(port, "GET", "/api/saved")).status).toBe(502);
    expect((await call(port, "GET", "/api/sync/status")).status).toBe(502);
    expect(recorded(logFile)).toEqual([]);
  });

  it("goes back to recording once the spec hands /api back", async () => {
    await call(port, "PUT", "/__tripwire/forward", JSON.stringify({ target: `http://127.0.0.1:${mockPort}` }));
    expect((await call(port, "DELETE", "/__tripwire/forward")).status).toBe(204);
    expect((await call(port, "GET", "/api/saved")).status).toBe(502);
    expect(mockHits).toEqual([]);
    expect(recorded(logFile)).toEqual(["/api/saved"]);
  });
});
