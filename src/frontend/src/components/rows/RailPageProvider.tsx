import { useState, type ReactNode } from "react";
import { RailPageRegistry } from "./railPaging";
import { RailPageContext } from "./railPage";

/**
 * One page view's registry of titles on screen, shared by every rail beneath it. The caller keys
 * this provider on whatever re-seeds the rails (Home: the adopted snapshot, the conditions version
 * and the medium), so a fresh registry — and a fresh page token — arrives with fresh rows.
 *
 * `seedTitleIds` are registered when the registry is created, before any rail's effect runs.
 * Effects run in tree order, so without this the hero's first top-up left before the seeded rows
 * below it had registered, and its page-wide exclusions missed every one of their titles (review
 * D3). The rails still register what they hold as they page; registering is idempotent.
 */
export default function RailPageProvider({
  seedTitleIds,
  children,
}: {
  seedTitleIds?: readonly string[];
  children: ReactNode;
}) {
  const [registry] = useState(() => {
    const created = new RailPageRegistry();
    if (seedTitleIds) created.register(seedTitleIds);
    return created;
  });
  return <RailPageContext.Provider value={registry}>{children}</RailPageContext.Provider>;
}
