import { describe, expect, it } from "vitest";
import { CONDITIONS_SCOPE_COPY } from "./conditionsCopy";

describe("CONDITIONS_SCOPE_COPY", () => {
  it("never calls the conditions airtight or claims they govern all search (R1-27)", () => {
    expect(CONDITIONS_SCOPE_COPY).not.toMatch(/all search/i);
    expect(CONDITIONS_SCOPE_COPY).not.toMatch(/airtight/i);
  });

  // WS-3 task 10 (FD-5): both typeahead legs hide excluded titles and count them, so the sentence
  // that said search "can show titles your conditions exclude" is no longer true.
  it("no longer says top-bar search can show titles the conditions exclude (WS-3 CR-4)", () => {
    expect(CONDITIONS_SCOPE_COPY).not.toMatch(/can show titles your conditions exclude/i);
    expect(CONDITIONS_SCOPE_COPY).not.toMatch(/only partly/i);
  });

  it("names top-bar search as governed, and says hidden matches are counted rather than shown", () => {
    expect(CONDITIONS_SCOPE_COPY).toContain("the hero, every row, Ask Reel and top-bar search");
    expect(CONDITIONS_SCOPE_COPY).toContain("counted, not shown");
  });

  it("names the two things the lookup deliberately does differently", () => {
    expect(CONDITIONS_SCOPE_COPY).toContain("titles you've seen");
    expect(CONDITIONS_SCOPE_COPY).toContain("ignores the “for you” minimum");
  });

  it("ends cleanly, so the dialog can append its own sentence after it", () => {
    expect(CONDITIONS_SCOPE_COPY.trim()).toBe(CONDITIONS_SCOPE_COPY);
    expect(CONDITIONS_SCOPE_COPY.endsWith(".")).toBe(true);
  });
});
