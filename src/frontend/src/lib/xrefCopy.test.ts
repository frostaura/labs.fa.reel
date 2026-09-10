import { describe, expect, it } from "vitest";
import {
  ruleOutClause,
  xrefBacktestCaveat,
  xrefBacktestSentence,
  xrefBandCopy,
  xrefCoverageNote,
  xrefOnlineNote,
  xrefVerdictLabel,
} from "./xrefCopy";
import type {
  CrossReferenceBacktest,
  CrossReferenceStatus,
  CrossReferenceVerdictOutcome,
} from "../store/api";

const status = (overrides: Partial<CrossReferenceStatus> = {}): CrossReferenceStatus => ({
  running: false,
  judged: 2242,
  total: 2242,
  kept: 210,
  ruledOut: 640,
  unsure: 1392,
  noSignal: 402,
  gating: "demote-only",
  ...overrides,
});

/** The founder's own prompt-v2 run, 2026-09-11 — five split origins pooled. */
const backtest = (overrides: Partial<CrossReferenceBacktest> = {}): CrossReferenceBacktest => ({
  promptVersion: 2,
  evaluatedAt: "2026-09-11T02:14:00Z",
  judged: 1194,
  auc: 0.655,
  meanLaterRatingKeep: 6.83,
  meanLaterRatingOut: 6.16,
  keepLovedRate: 0.332,
  keepDislikedRate: 0.081,
  falseRuleOutRate: 0.162,
  falseRuleOutLow: 0.123,
  falseRuleOutHigh: 0.21,
  trueRuleOutRate: 0.228,
  unsureRate: 0.46,
  gating: "demote-only",
  caveat: "",
  current: true,
  ...overrides,
});

const outcome = (overrides: Partial<CrossReferenceVerdictOutcome> = {}): CrossReferenceVerdictOutcome => ({
  verdict: "ProvisionalKeep",
  n: 34,
  meanLaterRating: 7.1,
  lovedRate: 0.35,
  dislikedRate: 0.09,
  answerable: true,
  ...overrides,
});

describe("the band while a pass is running", () => {
  it("counts through the pool rather than reporting a percentage", () => {
    // A pass over the whole eligible pool can legitimately run for ten minutes, and a bar at 29%
    // reads as a stall for nine of them. The count says how far through it actually is.
    expect(xrefBandCopy(status({ running: true, judged: 640 }))).toBe(
      "Deep pass running · 640 of 2,242 cross-referenced"
    );
  });

  it("groups every number the way the server does, never the browser's locale", () => {
    // The same rule the backend formats under: these strings are compared and asserted as exact
    // text, so a comma decimal separator would be a wrong answer rather than a rendering.
    const copy = xrefBandCopy(status({ running: true, judged: 1840, total: 12500 }))!;
    expect(copy).toContain("1,840 of 12,500");
    expect(copy).not.toContain("1.840");
  });

  it("says so from the first batch, before anything has been judged", () => {
    expect(xrefBandCopy(status({ running: true, judged: 0 }))).toBe(
      "Deep pass running · 0 of 2,242 cross-referenced"
    );
  });
});

describe("the band after a pass", () => {
  it("reports what it found and refuses to imply a demotion", () => {
    expect(xrefBandCopy(status())).toBe(
      "Cross-referenced 2,242 titles against your history · 210 kept, nudged up a little · " +
        "rule-outs change nothing yet — see the track record"
    );
  });

  it("drops the keep clause entirely when nothing was kept", () => {
    // "0 kept" reads as the product padding, which is the impression the honest-from-day-one
    // posture exists to avoid.
    const copy = xrefBandCopy(status({ kept: 0 }))!;
    expect(copy).not.toContain("kept");
    expect(copy).toContain("Cross-referenced 2,242 titles against your history");
  });

  it("treats zero rule-outs as an ordinary answer, not an error", () => {
    // The rule-out clause is about what rule-outs DO, and that is as true at zero as at 640.
    const copy = xrefBandCopy(status({ ruledOut: 0 }))!;
    expect(copy).toContain("rule-outs change nothing yet");
  });

  it("says nothing at all when nothing has been judged and nothing is judging", () => {
    expect(xrefBandCopy(status({ judged: 0, kept: 0, ruledOut: 0 }))).toBeNull();
    expect(xrefBandCopy(null)).toBeNull();
    expect(xrefBandCopy(undefined)).toBeNull();
  });

  it("singularises a one-title pass", () => {
    expect(xrefBandCopy(status({ judged: 1, kept: 1 }))).toContain("1 title against your history");
  });
});

describe("the rule-out clause", () => {
  it("is driven by the server's gating decision and never by a default", () => {
    expect(ruleOutClause("demote-only")).toBe("rule-outs change nothing yet — see the track record");
  });

  it("separates 'measured and did not earn it' from 'never measured'", () => {
    // Collapsing the two would let the band imply a measurement that never happened.
    expect(ruleOutClause("unmeasured")).toContain("nothing has measured them against your history yet");
    expect(ruleOutClause("unmeasured")).not.toBe(ruleOutClause("demote-only"));
  });

  it("still promises no demotion once the backtest clears the bar", () => {
    // The measurement earning a demotion is not the same as the ranking layer applying one; the
    // band has to describe the code rather than the permission.
    const copy = ruleOutClause("gating");
    expect(copy).toContain("still don't change your picks");
    expect(copy).not.toContain("hidden");
  });

  it("falls back to the honest clause on a gating decision it does not recognise", () => {
    expect(ruleOutClause("something-new")).toBe("rule-outs change nothing yet — see the track record");
  });
});

describe("the coverage note", () => {
  it("names the titles the pass could not reason about at all", () => {
    expect(xrefCoverageNote(status())).toBe(
      "402 titles had nothing to reason from yet — no plot summary and no read-through."
    );
  });

  it("is silent when there are none", () => {
    expect(xrefCoverageNote(status({ noSignal: 0 }))).toBeNull();
  });
});

describe("the backtest sentence", () => {
  it("is the founder's own numbers, and names the cost of a rule-out", () => {
    expect(xrefBacktestSentence(backtest())).toBe(
      "On your own history, titles it kept went on to average 6.83; titles it ruled out averaged 6.16 — " +
        "so a keep now nudges a title up a little; a rule-out changes nothing yet, because it also " +
        "caught 16% of the titles you went on to love."
    );
  });

  it("does not claim a keep helps when the keeps did not land higher", () => {
    const copy = xrefBacktestSentence(backtest({ meanLaterRatingKeep: 6.0, meanLaterRatingOut: 6.4 }));
    expect(copy).toContain("so nothing it says moves a pick");
    expect(copy).not.toContain("nudges a title up");
  });

  it("drops the comparison rather than inventing one when a mean is missing", () => {
    // A run from before the discrimination metrics existed measured no mean; it did not measure a
    // mean of zero.
    const copy = xrefBacktestSentence(backtest({ meanLaterRatingKeep: null, meanLaterRatingOut: null }));
    expect(copy).toContain("it judged 1,194 titles you had already rated");
    expect(copy).not.toContain("average null");
    expect(copy).not.toContain("NaN");
  });

  it("carries the interval, the comparison rate and the unsure share in the caveat", () => {
    const caveat = xrefBacktestCaveat(backtest());
    expect(caveat).toContain("prompt v2");
    expect(caveat).toContain("16.2%");
    expect(caveat).toContain("95% confidence 12.3–21.0%");
    expect(caveat).toContain("22.8%");
    expect(caveat).toContain("46%");
  });
});

describe("the online half", () => {
  it("refuses to answer below the server's threshold, and says how far off it is", () => {
    expect(xrefOnlineNote(outcome({ n: 4, answerable: false }))).toBe(
      "not yet answerable — 4 outcomes so far"
    );
    expect(xrefOnlineNote(outcome({ n: 1, answerable: false }))).toBe(
      "not yet answerable — 1 outcome so far"
    );
    expect(xrefOnlineNote(outcome({ n: 0, answerable: false }))).toBe(
      "not yet answerable — 0 outcomes so far"
    );
  });

  it("reads out the count, the mean and the loved share once it can", () => {
    expect(xrefOnlineNote(outcome())).toBe("34 rated since · averaging 7.10 · 35% of them 8 or higher");
  });

  it("drops a clause whose number is missing rather than rendering a zero", () => {
    const note = xrefOnlineNote(outcome({ meanLaterRating: null, lovedRate: null }));
    expect(note).toBe("34 rated since");
  });

  it("names each standing in the second person, and never renders a blank", () => {
    expect(xrefVerdictLabel("ConfirmedKeep")).toBe("kept twice");
    expect(xrefVerdictLabel("ProvisionalKeep")).toBe("kept");
    expect(xrefVerdictLabel("Out")).toBe("ruled out");
    expect(xrefVerdictLabel("Unsure")).toBe("not sure");
    // A standing this build has never heard of renders as itself rather than disappearing.
    expect(xrefVerdictLabel("SomethingLater")).toBe("SomethingLater");
  });
});
