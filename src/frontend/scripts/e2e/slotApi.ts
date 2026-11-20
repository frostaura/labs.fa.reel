import { spawn } from "node:child_process";
import { closeSync, openSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

/**
 * Boots the real API on a trust-v1 lane slot with NO provider credentials (IREV1-SYS-07).
 *
 * The lane env file is shared by every lane and carries a working OpenRouter key. With that key in
 * the API's environment, a UI check that merely opens /home spends: the feed paginator reranks the
 * first page on the model (purpose `rerank-page`), whatever the background job switches say. §7
 * gives the UI lanes 0 paid calls, so a slot API started for a real-API check must be incapable of
 * reaching a paid or third-party provider, not merely asked not to.
 *
 * What this launcher does to the environment it hands the API:
 * - removes every provider credential (the same list `ReelApiFactory` blanks for tests), whether it
 *   came from the env file or from the caller's own shell;
 * - points the model base URLs at a closed loopback port, so even a credential added later by
 *   another route cannot leave the machine (the adapters treat the refused connection as a failed
 *   call and degrade);
 * - pins the database to a lane slot on 127.0.0.1:5434 and turns the job runner off.
 * It then refuses to start unless `assertKeyless` passes on the final environment. No value from
 * the env file is ever printed; refusals name variables only.
 *
 * Usage (from src/frontend):
 *   node scripts/e2e/slotApi.ts --env-file ~/src/labs.fa.reel-ws/lane.env --db reel_ws9 --port 5189 \
 *     --log ../../.tmp/api.log --pid ../../.tmp/api.pid
 * The API runs detached in its own process group (`dotnet run --no-build`), so build first, and stop
 * it with `kill -- -$(cat <pid>)` (the group: `dotnet run` and the API process it starts).
 */

/** Credentials that let a process spend money or touch a third party. Mirrors ReelApiFactory. */
export const PROVIDER_CREDENTIALS = [
  "OPENROUTER_API_KEY",
  "OPENAI_API_KEY",
  "EMBEDDINGS_API_KEY",
  "TMDB_READ_ACCESS_TOKEN",
  "TRAKT_CLIENT_ID",
  "TRAKT_CLIENT_SECRET",
  "TRAKT_ACCESS_TOKEN",
  "TRAKT_REFRESH_TOKEN",
] as const;

/** Base URLs of the model providers (DependencyInjection.Llm). Both must stay on loopback. */
export const MODEL_BASE_URLS = ["OPENROUTER_BASE_URL", "EMBEDDINGS_BASE_URL"] as const;

/** Port 9 (discard) on loopback: nothing listens, so a call is refused before it leaves the host. */
export const DEAD_MODEL_BASE_URL = "http://127.0.0.1:9/api/v1";

const SLOT_HOST = "127.0.0.1";
const SLOT_PORT = 5434;
const SLOT_DB = /^reel_[a-z0-9_]+$/;

export type Env = Record<string, string | undefined>;

/** Parses a dotenv file (KEY=VALUE, `export KEY=VALUE`, quoted values, # comments). */
export function parseDotenv(text: string): Env {
  const out: Env = {};
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    const match = /^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$/.exec(line);
    if (!match) continue;
    let value = match[2];
    const quote = value[0];
    if ((quote === '"' || quote === "'") && value.length >= 2 && value.endsWith(quote)) {
      value = value.slice(1, -1);
    } else {
      // An unquoted value ends at an inline comment.
      value = value.replace(/\s+#.*$/, "");
    }
    out[match[1]] = value;
  }
  return out;
}

function isLoopback(url: string): boolean {
  try {
    const host = new URL(url).hostname;
    return host === "127.0.0.1" || host === "localhost" || host === "[::1]";
  } catch {
    return false;
  }
}

/** Names of the variables that would let this environment reach a paid or third-party provider. */
export function credentialLeaks(env: Env): string[] {
  const leaks: string[] = PROVIDER_CREDENTIALS.filter((name) => (env[name] ?? "").trim().length > 0);
  for (const name of MODEL_BASE_URLS) {
    const url = env[name];
    // An unset base URL falls back to the public provider in DependencyInjection.Llm.
    if (url === undefined || !isLoopback(url)) leaks.push(name);
  }
  return leaks;
}

/** Throws, naming variables only, unless the environment is incapable of a provider call. */
export function assertKeyless(env: Env): void {
  const leaks = credentialLeaks(env);
  if (leaks.length > 0) {
    throw new Error(`slotApi: refusing to start: the API environment can still reach a provider through ${leaks.join(", ")}`);
  }
}

export interface SlotOptions {
  db: string;
}

/**
 * The environment the slot API runs with: the caller's environment, overlaid with the env file,
 * with every provider credential removed, the model base URLs pinned to a dead loopback port and
 * the database pinned to the named slot.
 */
export function keylessSlotEnv(inherited: Env, fileVars: Env, { db }: SlotOptions): Env {
  if (!SLOT_DB.test(db)) throw new Error(`slotApi: --db must name a lane slot database (reel_<lane>), got "${db}"`);
  const env: Env = { ...inherited, ...fileVars };
  for (const name of PROVIDER_CREDENTIALS) delete env[name];
  for (const name of MODEL_BASE_URLS) env[name] = DEAD_MODEL_BASE_URL;
  env.ConnectionStrings__Postgres = `Host=${SLOT_HOST};Port=${SLOT_PORT};Username=reel;Password=reel_eval;Database=${db}`;
  env.Database__AutoMigrateOnStartup = "true";
  env.Background__JobRunner = "false";
  env.MSBuildEnableWorkloadResolver = "false";
  return env;
}

interface Args {
  envFile: string;
  db: string;
  port: number;
  log: string;
  pid: string;
}

export function parseArgs(argv: string[]): Args {
  const value = (flag: string): string | undefined => {
    const i = argv.indexOf(flag);
    return i >= 0 ? argv[i + 1] : undefined;
  };
  const envFile = value("--env-file");
  const db = value("--db");
  const port = Number(value("--port"));
  const log = value("--log");
  const pid = value("--pid");
  if (!envFile || !db || !Number.isInteger(port) || port <= 0 || !log || !pid) {
    throw new Error("usage: slotApi.ts --env-file <file> --db <reel_lane> --port <n> --log <file> --pid <file>");
  }
  return { envFile, db, port, log, pid };
}

function main(): void {
  const args = parseArgs(process.argv.slice(2));
  const env = keylessSlotEnv(process.env, parseDotenv(readFileSync(args.envFile, "utf8")), { db: args.db });
  assertKeyless(env);
  const here = path.dirname(fileURLToPath(import.meta.url));
  const repo = path.resolve(here, "../../../..");
  const dotnet = path.join(process.env.HOME ?? "", ".dotnet/dotnet");
  const out = openSync(args.log, "w");
  const child = spawn(
    dotnet,
    ["run", "--no-build", "--no-launch-profile", "--urls", `http://127.0.0.1:${args.port}`, "--project", "src/backend/FrostAura.Reel.Api"],
    { cwd: repo, env, detached: true, stdio: ["ignore", out, out] }
  );
  closeSync(out);
  writeFileSync(args.pid, String(child.pid));
  child.unref();
  console.log(`slotApi: API starting keyless on http://127.0.0.1:${args.port} (db ${args.db}, pid ${child.pid})`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    main();
  } catch (error) {
    console.error((error as Error).message);
    process.exit(2);
  }
}
