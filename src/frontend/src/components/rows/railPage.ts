import { createContext, useContext } from "react";
import type { RailPageRegistry } from "./railPaging";

/** Carries one page view's {@link RailPageRegistry} to every rail beneath {@link RailPageProvider}. */
export const RailPageContext = createContext<RailPageRegistry | null>(null);

/** The page's registry, or null on a surface with no page-wide de-duplication (the title modal). */
export function useRailPage(): RailPageRegistry | null {
  return useContext(RailPageContext);
}
