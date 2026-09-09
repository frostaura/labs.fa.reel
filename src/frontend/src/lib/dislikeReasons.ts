/**
 * The four dismissal reasons, in one place.
 *
 * `DislikeSignal` is reason-WEIGHTED: `genre` drives the genre demotion, `tone` the tone demotion,
 * `seen-enough` the plot-embedding demotion, and `not-for-me` only the diffuse centroid. So the
 * reason a dismissal carries is the difference between a signal the model can act on and one it
 * mostly cannot — which is why the card overlay, the highest-traffic dismissal surface in the app,
 * must offer the same four the detail modal does instead of hardcoding the flattest one.
 *
 * The `value` strings are the wire contract (`ReactionReason` on the server, kebab-case); the
 * labels are the copy the modal has always shown and are deliberately identical on both surfaces.
 */
export interface DislikeReason {
  value: "not-for-me" | "genre" | "seen-enough" | "tone";
  label: string;
}

export const DISLIKE_REASONS: readonly DislikeReason[] = [
  { value: "not-for-me", label: "Just not for me" },
  { value: "genre", label: "Not my genre" },
  { value: "seen-enough", label: "Seen enough like this" },
  { value: "tone", label: "Wrong tone" },
];

/** The reason a one-tap dismissal carries when the user does not pick one. */
export const DEFAULT_DISLIKE_REASON: DislikeReason["value"] = "not-for-me";
