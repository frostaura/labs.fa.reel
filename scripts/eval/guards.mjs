// Process guards shared by the live eval tools (askreel.mjs, surfaces.mjs, run-api.sh via the CLI below).
//
// 1. The model key. §4.12 and §7 rule 1 give the eval stack its OWN OpenRouter key so eval spend can never
//    trip the founder's cap. A live run therefore names the env file its API was started from, and is
//    refused when that file's OPENROUTER_API_KEY is the founder's key (compared by SHA-256; no value is
//    ever printed or stored) unless the founder's sign-off is passed and recorded in the recording.
// 2. Build slots. §5.0 rule 1 and §7 rule 5: no build may run during a latency run. A latency run takes
//    every machine build slot (~/src/fa.reel-ws/.locks/slot1..3, the same locks bin/build-slot uses) and
//    holds them until it ends; the recording says so, and the scorer gates latency only when it does.
//
//   node scripts/eval/guards.mjs key <env-file>     exit 0 separate key · 3 founder key · 4 no key
import { readFileSync, existsSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { createHash } from "node:crypto";
import { homedir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

// Reads ONE key's value from a dotenv file without echoing anything; returns its SHA-256 or null.
export function envKeyHash(file, name = "OPENROUTER_API_KEY") {
  if (!file || !existsSync(file)) return null;
  for (const raw of readFileSync(file, "utf8").split("\n")) {
    const line = raw.trim().replace(/^export\s+/, "");
    if (!line.startsWith(`${name}=`)) continue;
    const value = line.slice(name.length + 1).trim().replace(/^(["'])(.*)\1$/, "$2");
    return value ? createHash("sha256").update(value).digest("hex") : null;
  }
  return null;
}

// The founder's key, by hash: EVAL_FOUNDER_KEY_SHA256 when the integrator supplies it, otherwise read
// (by key name only) from the live clone's .env.
export function founderKeyHash(env = process.env) {
  if (env.EVAL_FOUNDER_KEY_SHA256) return env.EVAL_FOUNDER_KEY_SHA256.toLowerCase();
  return envKeyHash(env.EVAL_FOUNDER_ENV ?? join(homedir(), "src", "fa.reel", ".env"));
}

// "separate" | "founder" | "none" | "unknown" (the founder's hash could not be established).
export function classifyKey(apiEnvFile, env = process.env) {
  const mine = envKeyHash(apiEnvFile);
  if (!mine) return "none";
  const founder = founderKeyHash(env);
  if (!founder) return "unknown";
  return mine === founder ? "founder" : "separate";
}

// The recorded description of the key a live run paid with, or a refusal message.
export function modelKeyFor(apiEnvFile, signoff, env = process.env) {
  if (!apiEnvFile) return { refusal: "--api-env <file> is required: the env file the API was started from, so the run can prove which OpenRouter key it pays with" };
  const cls = classifyKey(apiEnvFile, env);
  if (cls === "founder" && !signoff) return { refusal: "the API's OPENROUTER_API_KEY is the founder's own key (§4.12 / §7 rule 1). Use the eval key, or pass --founder-key-signoff \"<who, when>\" with the founder's explicit sign-off" };
  if (cls === "unknown" && !signoff) return { refusal: "cannot establish the founder's key hash (set EVAL_FOUNDER_KEY_SHA256), so the run cannot prove it is not paying with it" };
  if (cls === "none") return { recorded: "no OpenRouter key (model calls cannot succeed)" };
  if (cls === "founder") return { recorded: `FOUNDER'S KEY, signed off: ${signoff}` };
  if (cls === "unknown") return { recorded: `unverified key, signed off: ${signoff}` };
  return { recorded: "separate key (not the founder's)" };
}

export const LOCKS_DIR = process.env.EVAL_LOCKS_DIR ?? join(homedir(), "src", "fa.reel-ws", ".locks");
const pidAlive = (pid) => { try { process.kill(pid, 0); return true; } catch { return false; } };

// Takes every build slot, waiting for builds in flight to finish; returns { release, heldFrom, waitedMs }.
export async function holdAllBuildSlots({ slots = 3, dir = LOCKS_DIR, log = () => {}, pollMs = 3000 } = {}) {
  mkdirSync(dir, { recursive: true });
  const held = new Set();
  const t0 = Date.now();
  const release = () => { for (const i of held) rmSync(join(dir, `slot${i}`), { recursive: true, force: true }); held.clear(); };
  process.on("exit", release);
  for (const sig of ["SIGINT", "SIGTERM"]) process.on(sig, () => { release(); process.exit(130); });
  let announced = 0;
  while (held.size < slots) {
    for (let i = 1; i <= slots; i++) {
      if (held.has(i)) continue;
      const d = join(dir, `slot${i}`);
      try { mkdirSync(d); writeFileSync(join(d, "pid"), String(process.pid)); held.add(i); continue; } catch { /* taken */ }
      // A slot whose holder died is free (the same rule bin/build-slot applies).
      let pid = null; try { pid = Number(readFileSync(join(d, "pid"), "utf8").trim()); } catch { /* being created */ }
      if (pid && !pidAlive(pid)) rmSync(d, { recursive: true, force: true });
    }
    if (held.size < slots) {
      if (Date.now() - announced > 60000) { log(`waiting for build slots (${held.size}/${slots} held)`); announced = Date.now(); }
      await new Promise((r) => setTimeout(r, pollMs));
    }
  }
  return { release, heldFrom: new Date().toISOString(), waitedMs: Date.now() - t0 };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [, , cmd, file] = process.argv;
  if (cmd !== "key" || !file) { console.error("usage: guards.mjs key <env-file>"); process.exit(64); }
  const cls = classifyKey(file);
  console.log(`openrouter key in ${file}: ${cls}`);
  process.exit({ separate: 0, founder: 3, none: 4, unknown: 5 }[cls]);
}
