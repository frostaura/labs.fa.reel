import { describe, expect, it } from "vitest";
import { evidenceChip, evidenceChips } from "./evidenceChips";

describe("evidenceChip", () => {
  it("a director is named with the capacity they share", () => {
    expect(evidenceChip({ kind: "person", label: "Denis Villeneuve", role: "director" })).toEqual({
      text: "Denis Villeneuve · dir.",
      description: "Shared director: Denis Villeneuve",
    });
  });

  it("a writer and a cast member", () => {
    expect(evidenceChip({ kind: "person", label: "Taylor Sheridan", role: "writer" })!.text).toBe("Taylor Sheridan · writer");
    expect(evidenceChip({ kind: "person", label: "Emily Blunt", role: "cast" })).toEqual({
      text: "Emily Blunt",
      description: "Shared cast member: Emily Blunt",
    });
  });

  it("a person with no role still says it is a person", () => {
    expect(evidenceChip({ kind: "person", label: "Roger Deakins", role: null })!.description).toBe("Shared person: Roger Deakins");
  });

  it("keywords and themes say which they are", () => {
    expect(evidenceChip({ kind: "keyword", label: "drug cartel" })!.description).toBe("Shared story keyword: drug cartel");
    expect(evidenceChip({ kind: "theme", label: "moral compromise" })!.description).toBe("Shared theme: moral compromise");
  });

  it("an unknown kind shows its label rather than vanishing or inventing a category", () => {
    expect(evidenceChip({ kind: "studio", label: "A24" })).toEqual({ text: "A24", description: "Shared: A24" });
  });

  it("a blank label is not evidence", () => {
    expect(evidenceChip({ kind: "keyword", label: "  " })).toBeNull();
  });
});

describe("evidenceChips", () => {
  it("keeps the server's order and caps at three", () => {
    const chips = evidenceChips([
      { kind: "person", label: "A", role: "director" },
      { kind: "keyword", label: "B" },
      { kind: "theme", label: "C" },
      { kind: "theme", label: "D" },
    ]);
    expect(chips.map((c) => c.text)).toEqual(["A · dir.", "B", "C"]);
  });

  it("drops blanks before capping", () => {
    expect(evidenceChips([{ kind: "keyword", label: "" }, { kind: "theme", label: "heist" }]).map((c) => c.text)).toEqual(["heist"]);
  });

  it("null, undefined and empty are no chips", () => {
    expect(evidenceChips(null)).toEqual([]);
    expect(evidenceChips(undefined)).toEqual([]);
    expect(evidenceChips([])).toEqual([]);
  });
});
