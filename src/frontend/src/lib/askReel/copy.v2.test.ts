import { describe, expect, it } from "vitest";
import { earlierAnswerSummary, narrate, nearMissHeading, stoppedMessage } from "./copy";
import { EDITOR, relaxationLabel, ruleTarget } from "./relaxation";

describe("narrate — the narration line follows the phase frames", () => {
  it("prints a v2 label verbatim", () => {
    expect(narrate({ stage: "judging", found: 0, label: "Checking 6 against your request" })).toBe("Checking 6 against your request");
  });
  it("narrates a v1 stage from its counts, singular where it is one", () => {
    expect(narrate({ stage: "discovered", found: 1 })).toBe("Found 1 title — matching them to your taste…");
  });
  it("names the v2 stages that arrive without a label", () => {
    expect(narrate({ stage: "judging", found: 0 })).toBe("Checking each one against your request…");
    expect(narrate(null)).toBe("Thinking…");
  });
});

describe("relaxation chips open the editor on their rule", () => {
  it("lands a genre rule on its chip and a language rule on the allow-list", () => {
    expect(ruleTarget({ kind: "ExcludeGenre", value: "Comedy" })).toBe('[data-testid="genre-comedy"]');
    expect(ruleTarget({ kind: "Language", value: "en" })).toBe('[data-testid="lang-all"]');
    expect(ruleTarget({ kind: "MinPredictedRating", value: "6.0" })).toBe('[data-testid="min-rating-slider"]');
  });
  it("lands an unknown kind on the editor itself, never nowhere", () => {
    expect(ruleTarget({ kind: "SomethingNew", value: "x" })).toBe(EDITOR);
    expect(ruleTarget({ kind: "ExcludeKeyword", value: "anime" })).toBe(EDITOR);
  });
  it("says what turning it off unlocks", () => {
    expect(relaxationLabel({ label: "English only", wouldUnlock: 4 })).toBe("Without “English only”: +4");
  });
});

describe("v2 copy", () => {
  it("states a stop in the turn's own count", () => {
    expect(stoppedMessage(0)).toBe("Stopped before any picks arrived.");
    expect(stoppedMessage(1)).toMatch(/^Stopped — 1 pick arrived/);
  });
  it("names the topic in the near-miss heading when there is one", () => {
    expect(nearMissHeading(["ninja"])).toBe("Close, but not ninja");
    expect(nearMissHeading([])).toBe("Close, but not quite");
  });
  it("summarises an earlier answer before it is opened", () => {
    expect(earlierAnswerSummary(8, 0)).toBe("Earlier answer · 8 picks");
    expect(earlierAnswerSummary(0, 2)).toBe("Earlier answer · No picks · 2 in its trays");
  });
});
