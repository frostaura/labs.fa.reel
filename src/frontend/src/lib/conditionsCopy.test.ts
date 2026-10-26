import { describe, expect, it } from "vitest";
import { CONDITIONS_SCOPE_COPY } from "./conditionsCopy";

describe("CONDITIONS_SCOPE_COPY", () => {
  it("no longer claims the conditions govern all search (R1-27)", () => {
    expect(CONDITIONS_SCOPE_COPY).not.toMatch(/all search/i);
    expect(CONDITIONS_SCOPE_COPY).not.toMatch(/airtight/i);
  });

  it("names the one condition search does not apply", () => {
    expect(CONDITIONS_SCOPE_COPY).toContain("except the “for you” minimum");
  });

  it("still says where they do apply", () => {
    expect(CONDITIONS_SCOPE_COPY).toMatch(/hero and every row/);
  });
});
