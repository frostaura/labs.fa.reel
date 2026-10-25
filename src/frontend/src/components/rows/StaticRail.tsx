import type { ReactNode } from "react";
import type { FeedCard } from "../../store/feedTypes";
import RecRow from "./RecRow";
import { useRegisterOnPage } from "./railPage";

/**
 * A rail with a fixed set of cards (the weekly digest) that still counts toward the page's
 * de-duplication: its titles are on screen, so no paging rail may serve them again (F+3).
 */
export default function StaticRail({ title, items }: { title: ReactNode; items: FeedCard[] }) {
  useRegisterOnPage(items.map((c) => c.titleId));
  return <RecRow title={title} items={items} />;
}
