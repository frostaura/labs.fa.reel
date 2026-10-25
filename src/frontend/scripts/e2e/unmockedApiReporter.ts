import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import type { FullConfig, FullResult, Reporter, Suite, TestCase } from "@playwright/test/reporter";
import { attribute, judge, parseHits, type TestStart, type UnmockedByTest } from "./unmockedApi";

/**
 * Fails the Playwright run when any test makes an /api or /sse call that no `page.route` mock
 * answered (trust-v1 R1-21). Such calls reach the tripwire (apiTripwire.mjs), never a real API;
 * this reporter reads the tripwire's log at the end of the run and names each offending test and
 * call.
 *
 * Known offenders live in unmocked-api-baseline.json: each entry is an open change request to the
 * spec's owner, and the list only shrinks. A test or call not in it fails the run.
 */
function testKey(test: TestCase, rootDir: string): string {
  const file = path.relative(rootDir, test.location.file).split(path.sep).join("/");
  // titlePath(): ["", project, file, ...describes, title]; the project is dropped so one baseline
  // serves Chromium and WebKit.
  return [file, ...test.titlePath().slice(3)].join(" › ");
}

function print(heading: string, calls: UnmockedByTest): void {
  console.log(heading);
  for (const [key, list] of Object.entries(calls)) console.log(`  ${key}\n    ${list.join("\n    ")}`);
}

export default class UnmockedApiReporter implements Reporter {
  private readonly logFile: string;
  private rootDir = "";
  private baselineFile = "";
  private readonly starts: TestStart[] = [];
  private readonly ran = new Set<string>();

  /** Hits older than this run belong to another run's log (a run that never started its own). */
  private readonly createdAt = Date.now();

  constructor(options: { logFile: string }) {
    this.logFile = options.logFile;
  }

  printsToStdio(): boolean {
    return false;
  }

  onBegin(config: FullConfig, _suite: Suite): void {
    this.rootDir = config.rootDir;
    // Resolved from the config file (both configs sit in the frontend root), not import.meta, so
    // it holds however Playwright transpiles this module.
    this.baselineFile = path.join(path.dirname(config.configFile ?? ""), "scripts/e2e/unmocked-api-baseline.json");
  }

  onTestBegin(test: TestCase): void {
    const key = testKey(test, this.rootDir);
    this.starts.push({ key, at: Date.now() });
    this.ran.add(key);
  }

  onEnd(result: FullResult): { status: FullResult["status"] } | undefined {
    if (!existsSync(this.logFile)) {
      console.log(`unmocked-api: the tripwire log ${this.logFile} does not exist, so no call can be vouched for.`);
      return { status: "failed" };
    }
    const baseline = JSON.parse(readFileSync(this.baselineFile, "utf8")) as UnmockedByTest;
    const { byTest, beforeAnyTest } = attribute(parseHits(readFileSync(this.logFile, "utf8")).filter((hit) => hit.at >= this.createdAt), this.starts);
    const { unexpected, unseen } = judge(byTest, baseline, this.ran);

    if (Object.keys(unseen).length) {
      print("unmocked-api: baseline debts not seen this run (delete them from the baseline if they are fixed):", unseen);
    }
    const failed = Object.keys(unexpected).length > 0 || beforeAnyTest.length > 0;
    if (!failed) return undefined;

    if (beforeAnyTest.length) console.log(`unmocked-api: calls before any test began:\n    ${beforeAnyTest.join("\n    ")}`);
    print(
      "unmocked-api: these tests made /api or /sse calls with no mock. Mock each one with page.route " +
        "(see tests-e2e/helpers.ts); the run fails until they do:",
      unexpected
    );
    return { status: result.status === "passed" ? "failed" : result.status };
  }
}
