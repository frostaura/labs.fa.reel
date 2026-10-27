import { X } from "lucide-react";
import { summaryChips, type FiltersLike } from "../../lib/pickConditions";
import type { BrowseFilter } from "../../lib/browseFilter";
import { Button } from "../ui/button";
import { COARSE_TAP } from "../../lib/tapTarget";
import { CHIP_TONE as TONE, SUMMARY_MUTED_TEXT } from "./conditionTone";

/**
 * Everything currently narrowing the feed, as chips — the answer to "why does my feed look like
 * this", which a filter set buried on another screen can never give.
 *
 * Two things are encoded here without relying on colour, because this row renders on the home feed
 * with no legend beside it and because green-vs-red alone is not a distinction every reader can
 * make:
 *
 * 1. **Include vs exclude** is carried by the WORDS ("has crime" / "no documentary") as well as the
 *    tone.
 * 2. **Saved condition vs per-device view filter** is carried by a labelled group ("Conditions" /
 *    "Viewing on this device"), a dashed rather than solid chip border, and the presence of a
 *    dismiss control. Three independent signals, none of them hue. The two behave differently — one
 *    follows the account, the other stays in this browser — so a reader must never have to guess
 *    which kind they are looking at, or where to go to change it.
 */
export default function ConditionSummary({
  filters,
  browse,
  onBrowseChange,
  limit,
  className = "",
}: {
  filters: FiltersLike;
  /** The per-device browse toggles, when the surface has any. Omitted on Settings, which has none. */
  browse?: BrowseFilter | null;
  /** Makes the browse chips dismissible. Without it they still render, just without the ✕. */
  onBrowseChange?: (next: BrowseFilter) => void;
  /**
   * Caps the CONDITION chips on tight surfaces; the overflow is counted rather than silently
   * dropped. Browse chips are never capped — there are at most two, and hiding a dismissible chip
   * behind "+3 more" would hide the only way to clear it from here.
   */
  limit?: number;
  className?: string;
}) {
  const chips = summaryChips(filters, browse);

  if (chips.length === 0) {
    return (
      <p className={`fa-caption ${SUMMARY_MUTED_TEXT} ${className}`} data-testid="conditions-summary-empty">
        Nothing set — everything is fair game.
      </p>
    );
  }

  const conditions = chips.filter((c) => c.kind === "condition");
  const views = chips.filter((c) => c.kind === "browse");
  const shown = limit != null ? conditions.slice(0, limit) : conditions;
  const hidden = conditions.length - shown.length;

  return (
    <div className={`flex flex-wrap items-center gap-x-4 gap-y-1.5 ${className}`} data-testid="conditions-summary">
      {conditions.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5" data-testid="summary-group-conditions">
          <span
            className={`fa-overline ${SUMMARY_MUTED_TEXT}`}
            title="Saved to your account — these apply everywhere. Change them under Conditions."
          >
            Conditions
          </span>
          {shown.map((chip) => (
            <span
              key={chip.key}
              // `first-letter:uppercase` rather than `capitalize`, because the labels are sentences
              // ("no documentary", "for you ≥ 8.0") and title-casing every word turns one into a
              // headline. `inline-block`, not `inline-flex`: ::first-letter only applies to block
              // container boxes, so a flex chip silently renders lowercase.
              className={`inline-block rounded-full border px-2 py-0.5 fa-caption align-middle first-letter:uppercase ${TONE[chip.tone]}`}
            >
              {chip.label}
            </span>
          ))}
          {hidden > 0 && <span className={`fa-caption ${SUMMARY_MUTED_TEXT}`}>+{hidden} more</span>}
        </div>
      )}

      {views.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5" data-testid="summary-group-browse">
          <span
            className={`fa-overline ${SUMMARY_MUTED_TEXT}`}
            title="This browser only — set by the Movies/TV and availability toggles, and not saved to your account."
          >
            Viewing on this device
          </span>
          {views.map((chip) => (
            <span
              key={chip.key}
              // Dashed border: the shape difference says "different kind of filter" to a reader who
              // cannot rely on the group label having stayed on the same wrapped line.
              className={`inline-flex items-center gap-1 rounded-full border border-dashed px-2 py-0.5 fa-caption align-middle ${TONE[chip.tone]}`}
              data-testid={chip.key}
            >
              {chip.label}
              {onBrowseChange && browse && chip.clear && (
                <Button
                  variant="ghost"
                  size="icon-sm"
                  // The segmented control that set this is often scrolled out of view by the time
                  // the chip is read, so the chip has to be able to clear itself.
                  onClick={() => onBrowseChange({ ...browse, ...chip.clear })}
                  aria-label={`Clear “${chip.label}” — show everything again`}
                  title={`Clear “${chip.label}”`}
                  data-testid={`clear-${chip.key}`}
                  className={`-mr-0.5 h-4 w-4 rounded-full text-current opacity-70 hover:bg-transparent hover:opacity-100 focus-visible:opacity-100 ${COARSE_TAP}`}
                >
                  <X className="h-3 w-3" />
                </Button>
              )}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}
