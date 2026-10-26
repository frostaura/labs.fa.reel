import { describe, expect, it } from "vitest";
import { CONDITIONS_SCOPE_COPY } from "./conditionsCopy";

describe("CONDITIONS_SCOPE_COPY", () => {
  it("no longer claims the conditions govern all search (R1-27)", () => {
    expect(CONDITIONS_SCOPE_COPY).not.toMatch(/all search/i);
    expect(CONDITIONS_SCOPE_COPY).not.toMatch(/airtight/i);
  });

  // IR1S-06: the global lookup applies none of the conditions, so no sentence may claim search
  // applies them with only the minimum excepted.
  it("does not claim search applies the conditions bar one exception (IR1S-06)", () => {
    expect(CONDITIONS_SCOPE_COPY).not.toMatch(/search applies them/i);
    expect(CONDITIONS_SCOPE_COPY).not.toMatch(/except/i);
  });

  it("says the top-bar lookup can show titles the conditions exclude (IR1S-06)", () => {
    expect(CONDITIONS_SCOPE_COPY).toContain("Top-bar search");
    expect(CONDITIONS_SCOPE_COPY).toContain("can show titles your conditions exclude");
  });

  it("still says where they do apply", () => {
    expect(CONDITIONS_SCOPE_COPY).toContain("the hero, every row and Ask Reel");
  });

  it("ends cleanly, so the dialog can append its own sentence after it", () => {
    expect(CONDITIONS_SCOPE_COPY.trim()).toBe(CONDITIONS_SCOPE_COPY);
    expect(CONDITIONS_SCOPE_COPY.endsWith(".")).toBe(true);
  });
});
