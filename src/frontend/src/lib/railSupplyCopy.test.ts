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

  it("on a tier with no paged rails, an empty rail names no cause at all — the tier, not the conditions, is why", () => {
    // Free's POST /feed/more is always { items: [], exhausted: true } with no supply: "your
    // conditions" would be a false cause there, so the rail says nothing and vanishes.
    expect(railSupplyLine(null, { empty: true, tierCapped: true })).toBeNull();
    expect(railSupplyLine(undefined, { empty: true, tierCapped: true })).toBeNull();
    // The same answer to an account that can page keeps its line.
    expect(railSupplyLine(null, { empty: true, tierCapped: false })!.seeWhy).toBe("Your conditions");
    // The device's own view is still a true cause on any tier.
    expect(railSupplyLine(null, { empty: true, hiddenByView: 2, tierCapped: true })!.text).toBe(
      "Nothing here matches the Movies/TV and availability view set on this device"
    );
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

  // HREV5-RET-01: POST /feed/more {row: similar, anchor: Master and Commander} came back empty with
  // supply null and anchoredSupply {gateHeldBack: 146, belowBar: 78}. The rail said "your conditions
  // allow" and linked to them, when the shared-evidence gate refused 146 titles inside them.
  describe("an anchored rail the gate or the bar emptied", () => {
    const anchorName = "Master and Commander: The Far Side of the World";

    it("names the gate, not the conditions, and offers no conditions link", () => {
      expect(railSupplyLine(null, { empty: true, anchored: { gateHeldBack: 146, belowBar: 78 }, anchorName, bar: 6 })).toEqual({
        text: `Nothing else shares a person, keyword or theme with ${anchorName} closely enough (146 near matches) · 78 more are under your 6.0 bar`,
        seeWhy: null,
      });
    });

    it("varies with the counts: one near match, nothing under the bar, no bar known, no anchor name", () => {
      expect(railSupplyLine(null, { empty: true, anchored: { gateHeldBack: 1, belowBar: 0 }, anchorName: "Contact" })!.text).toBe(
        "Nothing else shares a person, keyword or theme with Contact closely enough (1 near match)"
      );
      expect(railSupplyLine(null, { empty: true, anchored: { gateHeldBack: 1200, belowBar: 1 }, anchorName: "  " })!.text).toBe(
        "Nothing else shares a person, keyword or theme with this title closely enough (1,200 near matches) · 1 more is under your for-you bar"
      );
      expect(railSupplyLine(null, { empty: true, anchored: { gateHeldBack: 3, belowBar: 2 }, anchorName: "El Camino", bar: 7.5 })!.text).toBe(
        "Nothing else shares a person, keyword or theme with El Camino closely enough (3 near matches) · 2 more are under your 7.5 bar"
      );
    });

    it("a rail only the bar emptied says so, and links to the editor that holds the bar", () => {
      expect(railSupplyLine(null, { empty: true, anchored: { gateHeldBack: 0, belowBar: 78 }, anchorName: "Flubber", bar: 6 })).toEqual({
        text: "Nothing left like Flubber above your 6.0 bar — 78 close matches are under it",
        seeWhy: "Your bar",
      });
      expect(railSupplyLine(null, { empty: true, anchored: { gateHeldBack: 0, belowBar: 1 }, anchorName: "Flubber" })!.text).toBe(
        "Nothing left like Flubber above your for-you bar — 1 close match is under it"
      );
    });

    it("keeps the generic line when the window lost nothing to either, and says nothing on a rail that still has cards", () => {
      expect(railSupplyLine(null, { empty: true, anchored: { gateHeldBack: 0, belowBar: 0 }, anchorName: "Contact" })!.seeWhy).toBe(
        "Your conditions"
      );
      expect(railSupplyLine(null, { empty: false, anchored: { gateHeldBack: 146, belowBar: 78 }, anchorName: "Contact" })).toBeNull();
    });

    it("still yields to the device's view and to a tier that pages nothing", () => {
      expect(
        railSupplyLine(null, { empty: true, hiddenByView: 2, anchored: { gateHeldBack: 146, belowBar: 78 }, anchorName: "Contact" })!.text
      ).toBe("Nothing here matches the Movies/TV and availability view set on this device");
      expect(railSupplyLine(null, { empty: true, tierCapped: true, anchored: { gateHeldBack: 146, belowBar: 78 } })).toBeNull();
    });
  });
});
