import { describe, expect, it } from "vitest";
import { earlierAnswerSummary, narrate, nearMissHeading, NONE_FOUND_HEADING, noneFoundAnnouncement, noneFoundHeading, stoppedMessage } from "./copy";
import { relaxationLabel } from "./relaxation";

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

describe("relaxation chips", () => {
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

describe("noneFoundAnnouncement — the status region says what the none-found card says (HREV4-PH-01)", () => {
  const none = { unchecked: 0, nearMiss: 0, heldBack: 0, floorLabel: null };

  it("keeps “nothing left” for an answer with nothing on screen", () => {
    expect(noneFoundAnnouncement(none)).toBe("Nothing left that matches — Reel’s reply says why.");
  });
  it("never says “nothing left” while unchecked titles are shown — the keyless walk’s numbers", () => {
    expect(noneFoundAnnouncement({ unchecked: 11, nearMiss: 0, heldBack: 13, floorLabel: "6.0" }))
      .toBe("Reel couldn’t check what it found — 11 unchecked titles shown, 13 held back by your 6.0 bar.");
  });
  it("names no number for a bar it does not know", () => {
    expect(noneFoundAnnouncement({ unchecked: 1, nearMiss: 0, heldBack: 2, floorLabel: null }))
      .toBe("Reel couldn’t check what it found — 1 unchecked title shown, 2 held back by your bar.");
  });
  it("leaves out the trays that are empty and counts those that are not", () => {
    expect(noneFoundAnnouncement({ unchecked: 3, nearMiss: 1, heldBack: 0, floorLabel: "6.0" }))
      .toBe("Reel couldn’t check what it found — 3 unchecked titles shown, 1 close but not quite.");
  });
  it("opens on the card's own heading in both branches", () => {
    for (const shown of [none, { ...none, unchecked: 2 }, { ...none, heldBack: 4, floorLabel: "6.0" }]) {
      expect(noneFoundAnnouncement(shown).startsWith(noneFoundHeading(shown))).toBe(true);
    }
    expect(noneFoundHeading({ unchecked: 1 })).toBe(NONE_FOUND_HEADING.unchecked);
    expect(noneFoundHeading({ unchecked: 0 })).toBe(NONE_FOUND_HEADING.empty);
  });
});
