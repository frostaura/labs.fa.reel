import { useState, type ReactNode } from "react";
import { RailPageRegistry } from "./railPaging";
import { RailPageContext } from "./railPage";

/**
 * One page view's registry of titles on screen, shared by every rail beneath it. The caller keys
 * this provider on whatever re-seeds the rails (Home: the adopted snapshot, the conditions version
 * and the medium), so a fresh registry — and a fresh page token — arrives with fresh rows.
 */
export default function RailPageProvider({ children }: { children: ReactNode }) {
  const [registry] = useState(() => new RailPageRegistry());
  return <RailPageContext.Provider value={registry}>{children}</RailPageContext.Provider>;
}
