import { useId, useState } from "react";
import { SlidersHorizontal } from "lucide-react";
import ConditionSummary from "../settings/ConditionSummary";
import PickConditionsButton from "../rec/PickConditionsButton";
import PillButton from "../askReel/PillButton";
import { useGetFiltersQuery } from "../../store/api";
import { summaryChips } from "../../lib/pickConditions";
import type { BrowseFilter } from "../../lib/browseFilter";
import { plural } from "../../lib/askReel/copy";
import { MD_BREAKPOINT_PX, useMinWidth } from "../../lib/useMinWidth";

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
  const { data: filters, isLoading } = useGetFiltersQuery();
  const wide = useMinWidth(MD_BREAKPOINT_PX);
  const [open, setOpen] = useState(false);
  const stripId = useId();
  // Waits for the real payload rather than rendering against the defaults — `normalizeFilters`
  // fills an absent one out to `languages: ["en"]`, so an in-flight query would briefly assert an
  // "English only" condition the account may not have. Same rule as `PickConditionsSummary`, and
  // the reason this is a sibling of it rather than a wrapper around it.
  const count = filters ? summaryChips(filters, browse).length : 0;
  // The row is held while the conditions load. The strip is the first thing under the title, and
  // arriving about a second after the shell it pushed the whole page down — 0.053 of layout shift
  // on every cold /search, more than the page's whole 0.05 budget, before anything had been asked.
  // One 44 px row is exactly the phone's "N conditions" button and the wide strip's first row; an
  // account with nothing set gives the row back when the answer says so, which is the rare case.
  if (!filters) {
    return isLoading ? <div aria-hidden className={`min-h-11 ${className}`} data-testid="askreel-conditions-pending" /> : null;
  }
  if (count === 0) return null;
  const strip = (
    <>
      <ConditionSummary filters={filters} browse={browse} onBrowseChange={onBrowseChange} />
      <PickConditionsButton />
    </>
  );
  return (
    <div
      className={`flex flex-wrap items-center gap-x-3 gap-y-2 [&_[data-testid=open-conditions]]:min-h-11 ${className}`}
      data-testid="askreel-conditions"
    >
      {/* 4. On a phone the strip collapses to ONE button that says how many (WS-2 task 3). Twelve
          chips wrap to four rows at 390 px, and the reply and the first row of posters — the
          answer — were pushed under the composer on the first screen. Nothing is hidden for good:
          one tap opens every chip and the editor, exactly as the wide strip shows them. */}
      {wide ? strip : (
        <>
          <PillButton
            tone="quiet"
            data-testid="askreel-conditions-toggle"
            aria-expanded={open}
            aria-controls={stripId}
            onClick={() => setOpen((v) => !v)}
          >
            <SlidersHorizontal className="h-3.5 w-3.5" aria-hidden />
            {`${count} ${plural(count, "condition")}`}
          </PillButton>
          {open && (
            <div id={stripId} className="flex w-full flex-wrap items-center gap-x-3 gap-y-2">
              {strip}
            </div>
          )}
        </>
      )}
    </div>
  );
}
