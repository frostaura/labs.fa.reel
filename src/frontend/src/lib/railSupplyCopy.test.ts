import { describe, expect, it } from "vitest";
import { railSupplyLine } from "./railSupplyCopy";

// HREV1-RET-07: short and empty rails vanished, or thinned, with no word why. Live, a Thriller
// rail under "Shows" returned 0 cards with 251 titles held out by conditions, Romance 0 with 5,386,
// and "More like this" was empty for 27 of 50 anchors with no supply at all.
describe("railSupplyLine", () => {
  it("names what the conditions hide on an empty rail, with the way to see why", () => {
    expect(railSupplyLine({ hiddenByConditions: 251, alreadySeen: 48, clearingFloor: 44 }, { empty: true })).toEqual({
      text: "Nothing left here — your conditions hide 251 titles · you've seen 48",
      seeWhy: "See why",
    });
    expect(railSupplyLine({ hiddenByConditions: 5386, alreadySeen: 0, clearingFloor: 0 }, { empty: true })!.text).toBe(
      "Nothing left here — your conditions hide 5,386 titles"
    );
  });

  it("says what a short rail is short of, without restating the cards it does show", () => {
    expect(railSupplyLine({ hiddenByConditions: 1, alreadySeen: 0, clearingFloor: 3 }, { empty: false })).toEqual({
      text: "Your conditions hide 1 more title",
      seeWhy: "See why",
    });
    expect(railSupplyLine({ hiddenByConditions: 12, alreadySeen: 3, clearingFloor: 3 }, { empty: false })!.text).toBe(
      "Your conditions hide 12 more titles · you've seen 3"
    );
  });

  it("offers no 'see why' when no condition is what's missing", () => {
    expect(railSupplyLine({ hiddenByConditions: 0, alreadySeen: 31, clearingFloor: 0 }, { empty: true })).toEqual({
      text: "Nothing left here — you've seen 31",
      seeWhy: null,
    });
    // A short rail no condition thinned has nothing to explain.
    expect(railSupplyLine({ hiddenByConditions: 0, alreadySeen: 31, clearingFloor: 2 }, { empty: false })).toBeNull();
  });

  it("with no count from the server, an empty rail still says it is empty — and claims no cause", () => {
    expect(railSupplyLine(null, { empty: true })).toEqual({
      text: "Nothing left here that you haven't seen and your conditions allow",
      seeWhy: "Your conditions",
    });
    expect(railSupplyLine(undefined, { empty: false })).toBeNull();
  });

  it("an empty rail with nothing seen and nothing hidden says only that it is empty", () => {
    expect(railSupplyLine({ hiddenByConditions: 0, alreadySeen: 0, clearingFloor: 0 }, { empty: true })).toEqual({
      text: "Nothing left here right now",
      seeWhy: null,
    });
  });

  it("blames the device's view, not the conditions, when the cards it did load are hidden by the browse toggles", () => {
    expect(railSupplyLine({ hiddenByConditions: 251, alreadySeen: 0, clearingFloor: 0 }, { empty: true, hiddenByView: 4 })).toEqual({
      text: "Nothing here matches the Movies/TV and availability view set on this device",
      seeWhy: null,
    });
  });
});
