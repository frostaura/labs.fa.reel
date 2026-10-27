import { existsSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import type { FullConfig, FullResult, Reporter, Suite, TestCase, TestResult } from "@playwright/test/reporter";
import { attribute, judge, parseHits, type TestStart, type UnmockedByTest } from "./unmockedApi";

/**
 * Fails the Playwright run when any test makes an /api or /sse call that no `page.route` mock
 * answered (trust-v1 R1-21). Such calls reach the tripwire (apiTripwire.mjs), never a real API;
 * this reporter reads the tripwire's log at the end of the run and names each offending test and
 * call.
 *
 * Known offenders live in unmocked-api-baseline.json: each entry is an open change request to the
 * spec's owner, and the list only shrinks. A test or call not in it fails the run.
 *
 * Timing-dependent offenders. Whether an unmocked call lands can depend on timing: a lazy page chunk
 * that loads before or after the test clicks away, a debounced client-error report, a request
 * still in flight when the page closes. So a debt can go unseen in one run (never a failure; see
 * onEnd) and a pre-existing one can surface for the first time in a later run. When the run fails:
 *   1. If your change touched the spec, or the call is to an endpoint your change added, mock it
 *      with page.route. The baseline is never the fix for your own code.
 *   2. Otherwise it is an old debt that timing hid. Confirm it with
 *      `npx playwright test <spec> -g "<title>" --repeat-each=5` (both projects), add exactly the
 *      calls the failure named to the baseline, and file a change request to the spec's owner.
 * Example: track-record's avatar-menu test opens /home without mocking Home. Home's nine calls go
 * out only when Home's lazy page wins the race against the test's click: 1 targeted run in 5, and
 * in no full, per-spec or per-test run of the suite. It is baselined from the run that caught it.
 *
 * To harvest every offender at once (after an app change that adds calls, say), run with
 * UNMOCKED_API_FOUND=<file>: the reporter writes everything it attributed, baselined or not, as JSON
 * in the baseline's own shape. Union it across several runs, both projects, and the three shapes
 * a run can take (the full suite, each spec alone, each test alone), since each orders the races
 * differently.
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

  onTestBegin(test: TestCase, result: TestResult): void {
    const key = testKey(test, this.rootDir);
    // The worker's own start time, not this process's clock on receipt: the begin message crosses a
    // process boundary, and on a loaded machine a test's first request (boot mocks, page.goto) can
    // reach the tripwire before the message reaches the reporter. Timed on receipt, those calls
    // were blamed on the previous test, which is one way the gate could go red at random.
    this.starts.push({ key, at: result.startTime.getTime() });
    this.ran.add(key);
  }

  // Async because Playwright's Reporter types a status override as a Promise; a plain object was
  // honoured only because the runner happens to await it (`npm run typecheck:e2e` caught it).
  async onEnd(result: FullResult): Promise<{ status: FullResult["status"] } | undefined> {
    if (!existsSync(this.logFile)) {
      console.log(`unmocked-api: the tripwire log ${this.logFile} does not exist, so no call can be vouched for.`);
      return { status: "failed" };
    }
    const baseline = JSON.parse(readFileSync(this.baselineFile, "utf8")) as UnmockedByTest;
    const { byTest, beforeAnyTest } = attribute(parseHits(readFileSync(this.logFile, "utf8")).filter((hit) => hit.at >= this.createdAt), this.starts);
    const { unexpected, unseen } = judge(byTest, baseline, this.ran);
    const foundFile = process.env.UNMOCKED_API_FOUND;
    if (foundFile) writeFileSync(foundFile, `${JSON.stringify(byTest, null, 2)}\n`);

    if (Object.keys(unseen).length) {
      // Never a failure: some unmocked calls depend on timing (a lazy chunk, a debounced error
      // report, a request still in flight when the test ends), so one run not seeing a debt does
      // not mean it is paid. Delete an entry only once its spec mocks the call.
      print("unmocked-api: baseline debts not seen this run (informational; delete one only after its spec mocks the call):", unseen);
    }
    const failed = Object.keys(unexpected).length > 0 || beforeAnyTest.length > 0;
    if (!failed) return undefined;

    if (beforeAnyTest.length) console.log(`unmocked-api: calls before any test began:\n    ${beforeAnyTest.join("\n    ")}`);
    print(
      "unmocked-api: these tests made /api or /sse calls with no mock. Mock each one with page.route " +
        "(see tests-e2e/helpers.ts); the run fails until they do. If the spec is not yours and your " +
        "change added no call, it is an old timing-dependent debt: see the header of " +
        "scripts/e2e/unmockedApiReporter.ts.",
      unexpected
    );
    return { status: result.status === "passed" ? "failed" : result.status };
  }
}
