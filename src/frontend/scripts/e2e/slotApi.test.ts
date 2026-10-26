import { describe, expect, it } from "vitest";
import {
  assertKeyless,
  credentialLeaks,
  DEAD_MODEL_BASE_URL,
  keylessSlotEnv,
  parseArgs,
  parseDotenv,
  PROVIDER_CREDENTIALS,
} from "./slotApi";

// Shaped like the shared lane env (IREV1-SYS-07): a working model key, public base URLs, blank
// Trakt values. The values are fixtures, not credentials.
const LANE_ENV = `# trust-v1 lane env
TMDB_READ_ACCESS_TOKEN=tmdb-fixture
OPENROUTER_MODEL=openai/gpt-6-luna
OPENROUTER_API_KEY=sk-or-v1-fixture
OPENROUTER_BASE_URL=https://openrouter.ai/api/v1
EMBEDDINGS_MODEL=openai/text-embedding-3-small
EMBEDDINGS_BASE_URL=https://openrouter.ai/api/v1
TRAKT_CLIENT_ID=
TRAKT_ACCESS_TOKEN=
Background__DataRepairs=false
`;

describe("parseDotenv", () => {
  it("reads plain, exported, quoted and commented lines", () => {
    const env = parseDotenv(
      ['# header', 'A=1', 'export B=two', 'C="three # not a comment"', "D='four'", "E=five # trailing", "", "F="].join("\n")
    );
    expect(env).toEqual({ A: "1", B: "two", C: "three # not a comment", D: "four", E: "five", F: "" });
  });

  it("handles CRLF files and ignores lines that are not assignments", () => {
    expect(parseDotenv("A=1\r\nnot an assignment\r\nB=2\r\n")).toEqual({ A: "1", B: "2" });
  });
});

describe("keylessSlotEnv", () => {
  it("removes the model key the lane env carries and pins the model base URLs to a dead loopback port", () => {
    const env = keylessSlotEnv({}, parseDotenv(LANE_ENV), { db: "reel_ws9" });
    expect(env.OPENROUTER_API_KEY).toBeUndefined();
    expect(env.TMDB_READ_ACCESS_TOKEN).toBeUndefined();
    expect(env.OPENROUTER_BASE_URL).toBe(DEAD_MODEL_BASE_URL);
    expect(env.EMBEDDINGS_BASE_URL).toBe(DEAD_MODEL_BASE_URL);
    // Everything that is not a credential survives.
    expect(env.OPENROUTER_MODEL).toBe("openai/gpt-6-luna");
    expect(env.Background__DataRepairs).toBe("false");
    expect(credentialLeaks(env)).toEqual([]);
  });

  it("removes a key that arrives from the caller's shell, not only from the file", () => {
    // The 2026-10-26 boot sourced the lane env into the shell first; that path must be closed too.
    const env = keylessSlotEnv({ OPENROUTER_API_KEY: "sk-or-v1-shell", EMBEDDINGS_API_KEY: "e", OPENAI_API_KEY: "o" }, {}, { db: "reel_ws9" });
    for (const name of PROVIDER_CREDENTIALS) expect(env[name]).toBeUndefined();
  });

  it("keeps the caller's non-credential environment, which dotnet needs to run", () => {
    const env = keylessSlotEnv({ PATH: "/usr/bin", HOME: "/Users/x", OPENROUTER_MODEL: "shell-model" }, { OPENROUTER_MODEL: "file-model" }, { db: "reel_ws9" });
    expect(env.PATH).toBe("/usr/bin");
    expect(env.HOME).toBe("/Users/x");
    // The env file wins over the shell, as `set -a; . file` would.
    expect(env.OPENROUTER_MODEL).toBe("file-model");
  });

  it("pins the slot database and turns the job runner off whatever the inputs say", () => {
    const env = keylessSlotEnv(
      { ConnectionStrings__Postgres: "Host=db;Database=reel", Background__JobRunner: "true" },
      { Background__JobRunner: "true" },
      { db: "reel_ws10a" }
    );
    expect(env.ConnectionStrings__Postgres).toBe("Host=127.0.0.1;Port=5434;Username=reel;Password=reel_eval;Database=reel_ws10a");
    expect(env.Background__JobRunner).toBe("false");
  });

  it.each(["reel", "postgres", "reel_ws9;Host=10.0.0.5", "Reel_WS9", ""])("refuses %j as a slot database", (db) => {
    expect(() => keylessSlotEnv({}, {}, { db })).toThrow(/lane slot database/);
  });
});

describe("assertKeyless", () => {
  const clean = keylessSlotEnv({}, parseDotenv(LANE_ENV), { db: "reel_ws9" });

  it("passes the launcher's own environment", () => {
    expect(() => assertKeyless(clean)).not.toThrow();
  });

  it.each(PROVIDER_CREDENTIALS)("refuses an environment that still carries %s, naming it but not its value", (name) => {
    const env = { ...clean, [name]: "secret-fixture-value" };
    expect(() => assertKeyless(env)).toThrow(name);
    expect(() => assertKeyless(env)).not.toThrow(/secret-fixture-value/);
  });

  it("treats a whitespace-only credential as blank", () => {
    expect(credentialLeaks({ ...clean, OPENROUTER_API_KEY: "   " })).toEqual([]);
  });

  it.each([
    ["a public base URL", "https://openrouter.ai/api/v1"],
    ["an unset base URL (the adapters fall back to the public provider)", undefined],
    ["an unparseable base URL", "not a url"],
    ["a host that only starts like loopback", "http://127.0.0.1.attacker.test/v1"],
  ])("refuses %s even with no key present", (_label, url) => {
    expect(credentialLeaks({ ...clean, OPENROUTER_BASE_URL: url })).toEqual(["OPENROUTER_BASE_URL"]);
  });

  it("accepts every loopback spelling for the base URLs", () => {
    for (const url of ["http://localhost:9/v1", "http://[::1]:9/v1", "http://127.0.0.1:9/v1"]) {
      expect(credentialLeaks({ ...clean, EMBEDDINGS_BASE_URL: url })).toEqual([]);
    }
  });
});

describe("parseArgs", () => {
  it("reads every flag", () => {
    expect(parseArgs(["--env-file", "lane.env", "--db", "reel_ws9", "--port", "5189", "--log", "a.log", "--pid", "a.pid"])).toEqual({
      envFile: "lane.env",
      db: "reel_ws9",
      port: 5189,
      log: "a.log",
      pid: "a.pid",
    });
  });

  it.each([
    [["--db", "reel_ws9", "--port", "5189", "--log", "a", "--pid", "b"]],
    [["--env-file", "x", "--db", "reel_ws9", "--port", "abc", "--log", "a", "--pid", "b"]],
    [["--env-file", "x", "--db", "reel_ws9", "--port", "5189", "--log", "a"]],
  ])("refuses an incomplete command line %j", (argv) => {
    expect(() => parseArgs(argv)).toThrow(/usage/);
  });
});
