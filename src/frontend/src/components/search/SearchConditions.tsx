import ConditionSummary from "../settings/ConditionSummary";
import PickConditionsButton from "../rec/PickConditionsButton";
import { useGetFiltersQuery } from "../../store/api";
import { summaryChips } from "../../lib/pickConditions";
import type { BrowseFilter } from "../../lib/browseFilter";

/**
 * Everything narrowing this thread's answers, on the page they narrow — readable in full, and
 * openable.
 *
 * `/search` rendered the feed's summary verbatim, capped at six chips, with the overflow counted as
 * a bare `<span>` reading "+6 more": `tabIndex: -1`, no `role`, `cursor: auto`, no handler. Half
 * the account's standing exclusions were named as existing and were unreachable from the page they
 * shape (F6-17, and F3-14 before it) — and on a surface whose whole job that month was explaining
 * why an answer looks nothing like the question.
 *
 * Two changes, both of them here rather than in the shared summary:
 *
 * 1. **No cap.** The cap exists for "tight surfaces", and its cost is a control that names things
 *    it will not show. `/search` has one summary and a page of room under it, so every condition is
 *    a chip and there is no overflow to make openable. The chips wrap; that is what they are for.
 * 2. **The editor is one tap away**, through the same dialog the feed opens — one implementation of
 *    these rules, mounted from the two places that are governed by them.
 *
 * The feed keeps its cap, and keeps the inert `+N more` with it: `ConditionSummary` is a foreign
 * component and the span is its to fix. Reported rather than forked.
 *
 * 3. **The one interactive control in the strip meets 44 px** — from here, not from inside
 *    `PickConditionsButton`. R5-05: the twelve chips are inert `DIV`s and the single button that
 *    opens the editor measured 139 × 34 at both 375 and 1440, landing under the bar the same pair
 *    of commits raised for the composer and the `Recent` row. The button is a foreign component
 *    shared with the feed header, so the height is applied to it HERE, scoped to this strip, and
 *    the global fix is owed upstream in `components/rec/PickConditionsButton.tsx`.
 */
export default function SearchConditions({
  className = "",
  browse,
  onBrowseChange,
}: {
  className?: string;
  browse?: BrowseFilter | null;
  onBrowseChange?: (next: BrowseFilter) => void;
}) {
  const { data: filters } = useGetFiltersQuery();
  // Waits for the real payload rather than rendering against the defaults — `normalizeFilters`
  // fills an absent one out to `languages: ["en"]`, so an in-flight query would briefly assert an
  // "English only" condition the account may not have. Same rule as `PickConditionsSummary`, and
  // the reason this is a sibling of it rather than a wrapper around it.
  if (!filters || summaryChips(filters, browse).length === 0) return null;
  return (
    <div
      className={`flex flex-wrap items-center gap-x-3 gap-y-2 [&_[data-testid=open-conditions]]:min-h-11 ${className}`}
      data-testid="askreel-conditions"
    >
      <ConditionSummary filters={filters} browse={browse} onBrowseChange={onBrowseChange} />
      <PickConditionsButton />
    </div>
  );
}
