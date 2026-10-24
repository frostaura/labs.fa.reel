import { describe, expect, it } from "vitest";
import { legalPagesFrom } from "./legalPages";

const page = () => Promise.resolve({ default: () => null });

describe("legalPagesFrom", () => {
  it("routes only the pages that exist in the build — no link to a page that is not there", () => {
    expect(legalPagesFrom({})).toEqual([]);
    expect(legalPagesFrom({ "../pages/Terms.tsx": page }).map((p) => p.path)).toEqual(["/terms"]);
  });

  it("orders privacy before terms", () => {
    const pages = legalPagesFrom({ "../pages/Terms.tsx": page, "../pages/Privacy.tsx": page });
    expect(pages.map(({ path, label }) => [path, label])).toEqual([
      ["/privacy", "Privacy"],
      ["/terms", "Terms"],
    ]);
  });
});
