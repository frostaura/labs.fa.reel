import { describe, expect, it } from "vitest";
import { REGION_CODES, filterRegions, regionOptions } from "./regions";

describe("regions", () => {
  it("lists every assigned ISO 3166-1 code, once — the set the server accepts", () => {
    expect(REGION_CODES).toHaveLength(249);
    expect(new Set(REGION_CODES).size).toBe(249);
    expect(REGION_CODES).toContain("ZA");
    expect(REGION_CODES).toContain("US");
  });

  it("names regions in the given locale and sorts by name", () => {
    const options = regionOptions("en");
    expect(options.find((o) => o.code === "ZA")?.name).toBe("South Africa");
    const names = options.map((o) => o.name);
    expect([...names].sort((a, b) => a.localeCompare(b, "en"))).toEqual(names);
  });

  it("finds a region by code, by the start of its name, or by a word inside it — best first", () => {
    const options = regionOptions("en");
    expect(filterRegions(options, "za")[0].code).toBe("ZA");
    expect(filterRegions(options, "south")[0].name).toMatch(/^South/);
    expect(filterRegions(options, "africa").map((o) => o.code)).toContain("ZA");
    expect(filterRegions(options, "zzzz")).toEqual([]);
    expect(filterRegions(options, "  ")).toHaveLength(249);
  });
});
