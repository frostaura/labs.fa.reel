import type { NarrativePayload } from "../store/feedTypes";

/** Why the taste narrative is owed a rewrite, as the panel tells it. */
export type NarrativeStaleReason = "taste-moved" | "outdated-prompt";

/**
 * The reason to show, or null when the narrative is current.
 *
 * The server's `staleReason` is trusted when it sends one. An older server sends only `isStale`, which
 * then meant one thing — the taste had moved — so that is what a bare `isStale` still reads as.
 */
export function narrativeStaleReason(
  narrative: Pick<NarrativePayload, "isStale" | "staleReason">,
): NarrativeStaleReason | null {
  if (narrative.staleReason === "taste-moved" || narrative.staleReason === "outdated-prompt") {
    return narrative.staleReason;
  }
  return narrative.isStale ? "taste-moved" : null;
}

/**
 * The provenance suffix for each reason. An outdated-prompt narrative must not claim the taste moved
 * (it did not), and must not sit unflagged beside the charts it may now contradict — the crowd stance
 * above all — so it says the whole text is not current.
 */
export function narrativeStaleNote(reason: NarrativeStaleReason | null): string {
  switch (reason) {
    case "taste-moved":
      return " · your taste has moved since";
    case "outdated-prompt":
      return " · written by an earlier version of Reel, so it may not match the charts below";
    default:
      return "";
  }
}
