import { describe, expect, it } from "vitest";
import {
  DEFAULT_TIME_ZONE,
  filterTimeZones,
  suggestedTimeZone,
  timeZoneLabel,
  timeZoneName,
  timeZoneOffset,
  timeZoneOptions,
  timeZoneState,
} from "./timeZones";

/** The account time zone's rules (R1-17); the card that renders them is TimeZoneCard.test.tsx. */
describe("timeZoneState", () => {
  it("tells a chosen zone from one nobody chose", () => {
    expect(timeZoneState({ timeZone: "Africa/Johannesburg", effectiveTimeZone: "Africa/Johannesburg" })).toEqual({
      chosen: "Africa/Johannesburg",
      inForce: "Africa/Johannesburg",
    });
    expect(timeZoneState({ timeZone: null, effectiveTimeZone: "UTC" })).toEqual({ chosen: null, inForce: "UTC" });
  });

  it("an explicit UTC is a choice, not the default", () => {
    expect(timeZoneState({ timeZone: "UTC", effectiveTimeZone: "UTC" }).chosen).toBe("UTC");
  });

  it("reads the in-force zone from the server, even when the stored one is unresolvable there", () => {
    // A stored id this host cannot resolve is counted in UTC; the card must say so, not echo the id.
    expect(timeZoneState({ timeZone: "Mars/Olympus_Mons", effectiveTimeZone: "UTC" }).inForce).toBe("UTC");
  });

  it("an older server with neither field is counted in UTC, which is what it does", () => {
    expect(timeZoneState({})).toEqual({ chosen: null, inForce: DEFAULT_TIME_ZONE });
    expect(timeZoneState(undefined)).toEqual({ chosen: null, inForce: DEFAULT_TIME_ZONE });
  });
});

describe("suggestedTimeZone", () => {
  it("offers this device's zone only when it differs from the one in force", () => {
    expect(suggestedTimeZone("UTC", "Africa/Johannesburg")).toBe("Africa/Johannesburg");
    expect(suggestedTimeZone("Africa/Johannesburg", "Africa/Johannesburg")).toBeNull();
    expect(suggestedTimeZone("Africa/Johannesburg", "Europe/London")).toBe("Europe/London");
    expect(suggestedTimeZone("UTC", null)).toBeNull();
  });
});

describe("timeZoneOptions", () => {
  it("leads with UTC, lists it once, and is sorted", () => {
    const options = timeZoneOptions();
    expect(options[0]).toBe("UTC");
    expect(options.filter((z) => z === "UTC")).toHaveLength(1);
    const rest = options.slice(1);
    expect([...rest].sort((a, b) => a.localeCompare(b))).toEqual(rest);
    expect(options).toContain("Africa/Johannesburg");
  });

  it("adds the device's and the stored zone when the runtime does not list them, once each", () => {
    const options = timeZoneOptions(["Etc/GMT-14", "Etc/GMT-14", null, undefined, ""]);
    expect(options.filter((z) => z === "Etc/GMT-14")).toHaveLength(1);
    expect(options).not.toContain("");
  });
});

describe("names, offsets and search", () => {
  it("names a zone by its segments, with spaces for underscores", () => {
    expect(timeZoneName("America/Argentina/Buenos_Aires")).toBe("America / Argentina / Buenos Aires");
    expect(timeZoneName("UTC")).toBe("UTC");
  });

  it("shows the offset in force on the day asked about, so daylight saving moves it", () => {
    expect(timeZoneOffset("Africa/Johannesburg", new Date("2026-01-15T12:00:00Z"))).toBe("GMT+2");
    expect(timeZoneOffset("Europe/London", new Date("2026-01-15T12:00:00Z"))).toBe("GMT");
    expect(timeZoneOffset("Europe/London", new Date("2026-07-15T12:00:00Z"))).toBe("GMT+1");
    expect(timeZoneLabel("Africa/Johannesburg", new Date("2026-01-15T12:00:00Z"))).toBe("Africa / Johannesburg (GMT+2)");
    expect(timeZoneLabel("UTC")).toBe("UTC");
  });

  it("an id the runtime cannot format is named without an offset rather than throwing", () => {
    expect(timeZoneOffset("Not/AZone")).toBeNull();
    expect(timeZoneLabel("Not/AZone")).toBe("Not / AZone");
  });

  it("finds a zone by city start, then by any word, best first", () => {
    const options = ["UTC", "Africa/Johannesburg", "America/New_York", "America/Argentina/Buenos_Aires", "Europe/London"];
    expect(filterTimeZones(options, "joh")).toEqual(["Africa/Johannesburg"]);
    expect(filterTimeZones(options, "new york")).toEqual(["America/New_York"]);
    expect(filterTimeZones(options, "New_York")).toEqual(["America/New_York"]);
    expect(filterTimeZones(options, "buenos")).toEqual(["America/Argentina/Buenos_Aires"]);
    expect(filterTimeZones(options, "america")[0]).toBe("America/New_York");
    expect(filterTimeZones(options, "utc")).toEqual(["UTC"]);
    expect(filterTimeZones(options, "  ")).toEqual(options);
    expect(filterTimeZones(options, "atlantis")).toEqual([]);
  });
});
