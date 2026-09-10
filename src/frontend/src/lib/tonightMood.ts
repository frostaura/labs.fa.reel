import type { FeedCard } from "../store/feedTypes";

/**
 * The tonight-mood chips.
 *
 * The pipeline has already paid a model to judge seven scalars and a closed-vocabulary mood for
 * every enriched title, and until now almost nothing read them. These four chips are what that
 * spend buys the person looking at the screen: a way of asking "not tonight — something lighter"
 * without changing a single standing rule.
 *
 * Two deliberate constraints:
 *
 *  1. **It re-sorts, it never filters.** Tonight's picks are a contract — the same page all
 *     evening unless the user asks for new ones — so a chip reorders what is already there and
 *     nothing disappears. A mood is a way of reading the shelf, not a reason to deal a new one.
 *  2. **It is entirely client-side.** The card already carries the lens mood and the scalars, so a
 *     chip costs one array sort and no request, no rebuild, and no LLM call. Tapping four chips in
 *     a row is free.
 *
 * Missing data is scored as "no opinion" rather than as neutral: an unenriched title keeps its
 * model rank instead of being shuffled into the middle of every mood, because a card we know
 * nothing about is not mildly every mood, it is unmeasured.
 */
export type TonightMood = "light" | "heavy" | "switch-off" | "cerebral";

export const TONIGHT_MOODS: { id: TonightMood; label: string }[] = [
  { id: "light", label: "Something light" },
  { id: "heavy", label: "Something heavy" },
  { id: "switch-off", label: "Switch my brain off" },
  { id: "cerebral", label: "Something cerebral" },
];

/** The lens moods each chip counts as a direct hit for. The strongest signal when it is present. */
const LENS_MOODS: Record<TonightMood, string[]> = {
  light: ["light", "cosy"],
  heavy: ["heavy", "tense"],
  "switch-off": ["light", "background"],
  cerebral: ["cerebral"],
};

/** How much a matching lens mood is worth, against a scalar score that maxes out at 1. */
const LENS_MOOD_WEIGHT = 0.6;

/**
 * How well a card answers a mood, from the scalars alone, in 0–1. Returns null when the title has
 * no texture — the caller keeps its model rank rather than inventing a middling score for it.
 */
export function textureScore(card: FeedCard, mood: TonightMood): number | null {
  const t = card.texture;
  if (!t) return null;
  switch (mood) {
    // Light: not dark, funny, not demanding.
    case "light":
      return (1 - t.darkness) * 0.5 + t.humor * 0.3 + (1 - t.complexity) * 0.2;
    // Heavy: dark and emotionally weighty; complexity is not the point here, darkness is.
    case "heavy":
      return t.darkness * 0.7 + t.complexity * 0.3;
    // Switch off: undemanding above all, and it must not be bleak — nothing switches your brain
    // off less reliably than something quietly devastating.
    case "switch-off":
      return (1 - t.complexity) * 0.5 + (1 - t.darkness) * 0.3 + t.pacing * 0.2;
    // Cerebral: demanding, and slower rather than frenetic.
    case "cerebral":
      return t.complexity * 0.7 + (1 - t.pacing) * 0.3;
  }
}

/**
 * The full mood score for a card: the scalar score plus a bonus when the lens's own mood word
 * agrees. Null when nothing is known — the caller preserves model order for those.
 */
export function moodScore(card: FeedCard, mood: TonightMood): number | null {
  const texture = textureScore(card, mood);
  const lensHit = card.mood != null && LENS_MOODS[mood].includes(card.mood);
  if (texture === null) {
    // A lens mood on an unenriched title is still a real judgement about it, so it counts on its
    // own rather than being discarded for want of scalars.
    return lensHit ? LENS_MOOD_WEIGHT : null;
  }
  return texture + (lensHit ? LENS_MOOD_WEIGHT : 0);
}

/**
 * The cards re-sorted for a mood. Stable: cards that answer the mood rise in mood order, cards
 * that cannot be measured hold their original relative order below them, and passing no mood
 * returns the input untouched (same array identity, so React sees no change).
 */
export function sortForMood(cards: FeedCard[], mood: TonightMood | null): FeedCard[] {
  if (!mood) return cards;

  const scored = cards.map((card, index) => ({ card, index, score: moodScore(card, mood) }));
  return scored
    .slice()
    .sort((a, b) => {
      if (a.score === null && b.score === null) return a.index - b.index;
      // Unmeasured cards sink below measured ones rather than being ranked against them — but they
      // are never removed, because "we have not read this one yet" is not a verdict about it.
      if (a.score === null) return 1;
      if (b.score === null) return -1;
      if (b.score !== a.score) return b.score - a.score;
      return a.index - b.index;
    })
    .map((entry) => entry.card);
}
