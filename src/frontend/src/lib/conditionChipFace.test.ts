import { describe, expect, it } from "vitest";
import { conditionChipFace } from "./conditionChipFace";

describe("conditionChipFace", () => {
  it("puts a sign on genre and keyword rules and keeps the words for the spoken name", () => {
    expect(conditionChipFace({ key: "inc:crime", label: "has crime" })).toEqual({
      sign: "include",
      text: "crime",
      spoken: "Has crime",
    });
    expect(conditionChipFace({ key: "exc:documentary", label: "no documentary" })).toEqual({
      sign: "exclude",
      text: "documentary",
      spoken: "No documentary",
    });
    expect(conditionChipFace({ key: "kw:gore", label: "no “gore”" })).toEqual({
      sign: "exclude",
      text: "“gore”",
      spoken: "No “gore”",
    });
  });

  it("leaves every other rule unsigned and verbatim", () => {
    for (const chip of [
      { key: "crowd", label: "crowd ≥ 6.0" },
      { key: "predicted", label: "for you ≥ 6.0" },
      { key: "years", label: "2000 onwards" },
      { key: "languages", label: "English only" },
      { key: "maturity", label: "up to R" },
    ]) {
      const face = conditionChipFace(chip);
      expect(face.sign).toBeNull();
      expect(face.text).toBe(chip.label);
    }
  });
});
