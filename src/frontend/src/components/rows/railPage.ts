import { createContext, useContext, useEffect } from "react";
import type { RailPageRegistry } from "./railPaging";

/** Carries one page view's {@link RailPageRegistry} to every rail beneath {@link RailPageProvider}. */
export const RailPageContext = createContext<RailPageRegistry | null>(null);

/** The page's registry, or null on a surface with no page-wide de-duplication (the title modal). */
export function useRailPage(): RailPageRegistry | null {
  return useContext(RailPageContext);
}

/**
 * Puts a rail that does not page (the weekly digest) on the page's on-screen set, so the rails that
 * DO page never serve one of its titles again (F+3). Paging rails register through
 * `useInfiniteRow`; a rail that skips both is invisible to the de-duplication.
 */
export function useRegisterOnPage(titleIds: readonly string[]): void {
  const page = useRailPage();
  // Keyed on the ids' content, not the array's identity: callers derive the list during render.
  const key = titleIds.join(",");
  useEffect(() => {
    if (page && key !== "") page.register(key.split(","));
  }, [page, key]);
}
