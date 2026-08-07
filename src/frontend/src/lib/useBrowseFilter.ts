import { useCallback, useState } from "react";
import { readBrowseFilter, writeBrowseFilter, type BrowseFilter, type BrowseScope } from "./browseFilter";

/**
 * A browse filter that survives a reload.
 *
 * Seeded from storage in the useState INITIALISER, not an effect. An effect would paint the
 * default first and snap to the stored value on a second pass — the same defaults-then-snap flash
 * the settings panel was fixed for, except here it is worse: the first paint would render cards
 * the user has filtered out, and they would visibly vanish.
 *
 * The write rides in the setter rather than a `useEffect([filter])`, so nothing is persisted
 * except an actual user action. An effect would also re-write the default on first mount for
 * anyone who has never touched the control.
 */
export function useBrowseFilter(scope: BrowseScope): [BrowseFilter, (next: BrowseFilter) => void] {
  const [filter, setFilterState] = useState<BrowseFilter>(() => readBrowseFilter(scope));

  const setFilter = useCallback(
    (next: BrowseFilter) => {
      setFilterState(next);
      writeBrowseFilter(scope, next);
    },
    [scope]
  );

  return [filter, setFilter];
}
