import { describe, expect, it } from "vitest";
import type { FeedCard } from "../store/feedTypes";
import { moodScore, sortForMood, textureScore, TONIGHT_MOODS } from "./tonightMood";

function card(
  name: string,
  texture: FeedCard["texture"],
  mood: string | null = null,
): FeedCard {
  return {
    titleId: name,
    mediaType: "Movie",
    tmdbId: 1,
    name,
    year: 2020,
    runtimeMinutes: 100,
    posterPath: null,
    backdropPath: null,
    genres: ["drama"],
    predictedRating: 7,
    crowdRating: null,
    crowdVotes: null,
    whyThis: "why",
    isReleased: true,
    texture,
    mood,
  };
}

const LIGHT = card("Light", { darkness: 0.1, pacing: 0.6, complexity: 0.2, humor: 0.9 });
const HEAVY = card("Heavy", { darkness: 0.9, pacing: 0.3, complexity: 0.8, humor: 0.0 });
const CEREBRAL = card("Cerebral", { darkness: 0.5, pacing: 0.2, complexity: 0.95, humor: 0.1 });
const UNKNOWN = card("Unknown", null);

describe("tonight-mood re-sort", () => {
  it("ranks the obviously-light title first for 'light'", () => {
    const sorted = sortForMood([HEAVY, CEREBRAL, LIGHT], "light");
    expect(sorted[0].name).toBe("Light");
  });

  it("ranks the bleakest title first for 'heavy'", () => {
    const sorted = sortForMood([LIGHT, CEREBRAL, HEAVY], "heavy");
    expect(sorted[0].name).toBe("Heavy");
  });

  it("ranks the most demanding title first for 'cerebral'", () => {
    const sorted = sortForMood([LIGHT, HEAVY, CEREBRAL], "cerebral");
    expect(sorted[0].name).toBe("Cerebral");
  });

  it("prefers the undemanding and unbleak for 'switch off'", () => {
    // Nothing switches your brain off less reliably than something quietly devastating, so
    // darkness counts against this mood even though complexity is the headline term.
    const sorted = sortForMood([HEAVY, CEREBRAL, LIGHT], "switch-off");
    expect(sorted[0].name).toBe("Light");
    expect(sorted[sorted.length - 1].name).toBe("Cerebral");
  });

  it("re-sorts and never filters — every card survives", () => {
    const input = [LIGHT, HEAVY, CEREBRAL, UNKNOWN];
    for (const mood of TONIGHT_MOODS) {
      const sorted = sortForMood(input, mood.id);
      expect(sorted).toHaveLength(input.length);
      expect(sorted.map((c) => c.name).sort()).toEqual(input.map((c) => c.name).sort());
    }
  });

  it("returns the same array identity with no mood, so React sees no change", () => {
    const input = [LIGHT, HEAVY];
    expect(sortForMood(input, null)).toBe(input);
  });

  it("sinks unmeasured cards below measured ones rather than ranking them", () => {
    // A card we have not read is not mildly every mood — it is unmeasured, and inventing a
    // middling score for it would shuffle it into the middle of every single chip.
    const sorted = sortForMood([UNKNOWN, HEAVY], "heavy");
    expect(sorted.map((c) => c.name)).toEqual(["Heavy", "Unknown"]);
    expect(textureScore(UNKNOWN, "heavy")).toBeNull();
  });

  it("keeps unmeasured cards in their original relative order", () => {
    const a = card("A", null);
    const b = card("B", null);
    expect(sortForMood([a, b], "light").map((c) => c.name)).toEqual(["A", "B"]);
    expect(sortForMood([b, a], "light").map((c) => c.name)).toEqual(["B", "A"]);
  });

  it("is stable for ties, so equal cards do not shuffle between renders", () => {
    const first = card("First", { darkness: 0.5, pacing: 0.5, complexity: 0.5, humor: 0.5 });
    const second = card("Second", { darkness: 0.5, pacing: 0.5, complexity: 0.5, humor: 0.5 });
    expect(sortForMood([first, second], "light").map((c) => c.name)).toEqual(["First", "Second"]);
  });

  it("lets a matching lens mood outrank a marginally better texture", () => {
    // The lens mood is a judgement about the whole title; the scalars are a proxy for one. Where
    // they disagree slightly, the judgement wins.
    const scalarsOnly = card("Scalars", { darkness: 0.15, pacing: 0.6, complexity: 0.25, humor: 0.85 });
    const lensBacked = card("Lens", { darkness: 0.2, pacing: 0.6, complexity: 0.3, humor: 0.8 }, "light");

    expect(sortForMood([scalarsOnly, lensBacked], "light")[0].name).toBe("Lens");
  });

  it("scores a lens mood on its own when the title has no texture", () => {
    const lensOnly = card("LensOnly", null, "cerebral");
    expect(moodScore(lensOnly, "cerebral")).toBeGreaterThan(0);
    expect(moodScore(lensOnly, "light")).toBeNull();
  });

  it("treats 'cosy' as light and 'background' as switch-off", () => {
    expect(moodScore(card("Cosy", null, "cosy"), "light")).toBeGreaterThan(0);
    expect(moodScore(card("Bg", null, "background"), "switch-off")).toBeGreaterThan(0);
    expect(moodScore(card("Bg", null, "background"), "heavy")).toBeNull();
  });

  it("offers exactly the four chips the hero renders", () => {
    expect(TONIGHT_MOODS.map((m) => m.id)).toEqual(["light", "heavy", "switch-off", "cerebral"]);
  });
});
