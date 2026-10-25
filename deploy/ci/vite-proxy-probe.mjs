// vite-proxy-probe.mjs [config file] — run from src/frontend (make ci-vite-proxy).
//
// The dev proxy must not turn a LAN caller into "this machine" (WS-8 review, 2026-10-24). The API's
// LoopbackGuard trusts a loopback peer only when every hop a proxy recorded is loopback too, and it
// cannot see through a proxy that records nothing: Vite's /api proxy reaches the API from
// 127.0.0.1, and without `xfwd` it forwards no X-Forwarded-For, so a LAN host that reached Vite got
// a Founder session from POST /api/auth/dev/link. Two properties close that, and this gate fails
// while either is missing:
//
//   1. With no VITE_HOST opt-in, the dev server listens on loopback only.
//   2. Every proxy entry records its client (`xfwd: true`): a request through it arrives upstream
//      with X-Forwarded-For naming the real caller.
//
// The probe starts the repository's own Vite config (programmatically, on a free port) in front of
// a local echo server, and inspects what the echo server receives. No network beyond loopback, no
// browser, no API.
import { createServer as createHttpServer, request } from "node:http";
import { createRequire } from "node:module";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const configFile = resolve(process.argv[2] ?? "vite.config.ts");
const failures = [];

const echo = createHttpServer((req, res) => {
  echo.last = { url: req.url, peer: req.socket.remoteAddress, headers: req.headers };
  res.setHeader("Content-Type", "application/json");
  res.end("{}");
});
await new Promise((done) => echo.listen(0, "127.0.0.1", done));
const echoPort = echo.address().port;

// Resolve Vite from the frontend's own node_modules, not from this script's directory.
const require = createRequire(resolve("package.json"));
const { createServer } = await import(pathToFileURL(require.resolve("vite")).href);

// A free port, chosen here: the config file names 5173, which a running stack may already hold.
const probe = createHttpServer();
await new Promise((done) => probe.listen(0, "127.0.0.1", done));
const vitePort = probe.address().port;
await new Promise((done) => probe.close(done));

delete process.env.VITE_HOST;
process.env.VITE_API_TARGET = `http://127.0.0.1:${echoPort}`;

const vite = await createServer({
  configFile,
  logLevel: "silent",
  clearScreen: false,
  server: { port: vitePort, strictPort: true, hmr: false },
});
await vite.listen();

try {
  const address = vite.httpServer.address();
  const listening = typeof address === "object" && address ? address.address : String(address);
  if (!["127.0.0.1", "::1", "::ffff:127.0.0.1"].includes(listening)) {
    failures.push(
      `the dev server listens on ${listening} with no VITE_HOST opt-in; it must default to 127.0.0.1 ` +
        `(server.host: process.env.VITE_HOST ?? "127.0.0.1").`
    );
  }

  const port = address.port;
  const proxied = Object.keys(vite.config.server.proxy ?? {});
  if (proxied.length === 0) {
    failures.push("the dev server proxies nothing; expected /api and /sse.");
  }

  for (const prefix of proxied) {
    echo.last = undefined;
    const path = `${prefix.replace(/\/$/, "")}/auth/dev/link`;
    await new Promise((done, fail) => {
      const req = request(
        { host: listening === "0.0.0.0" || listening === "::" ? "127.0.0.1" : listening, port, path, method: "POST", headers: { "Content-Type": "application/json" } },
        (res) => {
          res.resume();
          res.on("end", done);
        }
      );
      req.on("error", fail);
      req.end("{}");
    });

    const seen = echo.last;
    if (!seen) {
      failures.push(`${prefix}: the request never reached the upstream.`);
      continue;
    }

    const forwardedFor = seen.headers["x-forwarded-for"];
    if (!forwardedFor) {
      failures.push(
        `${prefix}: the upstream saw peer ${seen.peer} and no X-Forwarded-For. Without it the API cannot tell ` +
          `a LAN caller from this machine; set xfwd: true on this proxy entry.`
      );
    } else if (!/(^|,)\s*(127\.0\.0\.1|::1|::ffff:127\.0\.0\.1)\s*$/.test(forwardedFor)) {
      failures.push(`${prefix}: X-Forwarded-For "${forwardedFor}" does not end with this probe's loopback address.`);
    }
  }
} finally {
  await vite.close();
  echo.close();
}

if (failures.length > 0) {
  console.error(`vite-proxy-probe: ${configFile}`);
  for (const failure of failures) {
    console.error(`  - ${failure}`);
  }
  process.exit(1);
}

console.log(`vite-proxy-probe: ${configFile}: loopback listen by default; every proxy entry records its client.`);
