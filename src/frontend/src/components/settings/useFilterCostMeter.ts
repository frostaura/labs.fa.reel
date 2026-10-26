import { useEffect, useRef } from "react";
import { useLazyFilterCostQuery, type FiltersPayload } from "../../store/api";

/**
 * How long the editor waits before costing a draft. Long enough that a chip-tapping burst is one
 * request, short enough that the number feels attached to the tap that changed it.
 */
export const COST_DEBOUNCE_MS = 350;

/**
 * The cost meter for a draft rule set (H-6), with stale requests ABORTED rather than left to finish.
 *
 * A keyed query hook only switches which result it shows when the draft changes: the request for
 * the previous draft keeps running to completion on the server and over the wire. With the meter at
 * 7–53 s on live, a few taps stacked several full catalogue passes behind each other. Here, any
 * change to the draft (or a lock, or leaving the editor) aborts the request in flight at once; the
 * new draft is costed after the debounce. A draft already costed is served from the cache.
 *
 * Failures are deliberately swallowed by the caller: the meter is an aid, and an editor that refused
 * to render because a count failed would be a worse screen than one that shows no counts.
 */
export function useFilterCostMeter(draft: FiltersPayload | null, skip: boolean) {
  const [trigger, result] = useLazyFilterCostQuery();
  const inflight = useRef<{ abort: () => void } | null>(null);
  // Serialised so a fresh object identity per keystroke neither re-arms the timer for ever nor
  // aborts a request for an unchanged draft.
  const draftKey = draft ? JSON.stringify(draft) : "";

  useEffect(() => {
    inflight.current?.abort();
    inflight.current = null;
    if (skip || draftKey === "") return;
    const handle = window.setTimeout(() => {
      inflight.current = trigger(JSON.parse(draftKey) as FiltersPayload, true);
    }, COST_DEBOUNCE_MS);
    return () => window.clearTimeout(handle);
  }, [draftKey, skip, trigger]);

  // Leaving the editor abandons the last request too.
  useEffect(
    () => () => {
      inflight.current?.abort();
    },
    [],
  );

  return result.data;
}
