import { describe, expect, it } from "vitest";
import { shouldQueryGlobal } from "./globalTypeahead";

describe("shouldQueryGlobal", () => {
  it("fires when the local shortlist is sparse and the term is substantial", () => {
    expect(shouldQueryGlobal("jeanne dielman", 0)).toBe(true);
    expect(shouldQueryGlobal("jeanne dielman", 2)).toBe(true);
  });

  it("stays quiet when the local shortlist already answers", () => {
    expect(shouldQueryGlobal("jeanne dielman", 3)).toBe(false);
    expect(shouldQueryGlobal("jeanne dielman", 8)).toBe(false);
  });

  it("requires at least 3 meaningful characters", () => {
    expect(shouldQueryGlobal("je", 0)).toBe(false);
    expect(shouldQueryGlobal("  je  ", 0)).toBe(false);
    expect(shouldQueryGlobal("jea", 0)).toBe(true);
  });

  it("trims before measuring the term", () => {
    expect(shouldQueryGlobal("  a  ", 0)).toBe(false);
    expect(shouldQueryGlobal("  spy  ", 0)).toBe(true);
  });
});
