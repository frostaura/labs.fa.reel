import { useState } from "react";
import { Minus, Plus, X } from "lucide-react";
import { useGetFiltersQuery } from "../../store/api";
import { summaryChips } from "../../lib/pickConditions";
import { conditionChipFace } from "../../lib/conditionChipFace";
import type { BrowseFilter } from "../../lib/browseFilter";

/**
 * Home's conditions, as chips you can act on (H-20, H-9, O-11): everything shaping tonight's picks,
 * stated under the page title, and each chip a way into the editor that changes it.
 *
 * - **Neutral, with ± icons.** The chips used to be tinted green and red, which made a standing
 *   preference read as an error state at the top of the page and still left the include/exclude
 *   distinction to hue. They are one neutral style now; a genre or keyword rule carries a `+` or `−`
 *   glyph, and its accessible name spells the rule out ("No documentary").
 * - **Tap opens the editor.** A chip is a button that opens the Conditions dialog — the answer to
 *   "why is my feed like this" is one tap from the thing that changes it.
 * - **"+N more" expands** in place instead of being a dead count, and collapses again.
 * - **`strip` variant** for a phone: the chips ARE the conditions control, inside the header's one
 *   scrolling line, at a 44 px target each — the separate "Conditions" button and a whole chip line
 *   above it were two rows of chrome above the first pick for one job.
 *
 * Browse toggles (per-device Movies/TV and availability) render as dashed chips with their own ✕,
 * because they are cleared here, not in the dialog; the strip omits them, since the segmented
 * control that sets them sits in the same line.
 */
export default function HomeConditionChips({
  browse,
  onBrowseChange,
  onOpenEditor,
  limit,
  variant = "row",
  className = "",
}: {
  browse?: BrowseFilter | null;
  onBrowseChange?: (next: BrowseFilter) => void;
  onOpenEditor: () => void;
  /** Condition chips shown before "+N more". */
  limit: number;
  variant?: "row" | "strip";
  className?: string;
}) {
  const { data: filters } = useGetFiltersQuery();
  const [expanded, setExpanded] = useState(false);

  // Waits for the real payload rather than rendering against the defaults: `normalizeFilters` fills
  // an absent payload out to `languages: ["en"]`, so an in-flight query would briefly assert an
  // "English only" condition the account may not actually have.
  if (!filters) return null;

  const strip = variant === "strip";
  const chips = summaryChips(filters, strip ? null : browse);
  const conditions = chips.filter((c) => c.kind === "condition");
  const views = chips.filter((c) => c.kind === "browse");
  if (conditions.length === 0 && views.length === 0) return null;

  const shown = expanded ? conditions : conditions.slice(0, limit);
  const hidden = conditions.length - shown.length;

  // A 44 px target on any coarse pointer, around a pill that stays caption-sized: the button is the
  // hit area and the span inside it is what you see (the card quick actions' pattern).
  const target = strip
    ? "inline-flex min-h-11 shrink-0 items-center"
    : "inline-flex min-h-11 items-center [@media(pointer:fine)]:min-h-0";
  const pill =
    "inline-flex items-center gap-1 whitespace-nowrap rounded-full border border-fa-edge bg-fa-glass px-2 py-0.5 fa-caption text-fa-frost transition group-hover:border-fa-frost/40 group-hover:text-fa-frost-bright";

  return (
    <div
      className={
        strip
          ? `flex items-center gap-1.5 ${className}`
          : `flex flex-wrap items-center gap-x-4 gap-y-0 ${className}`
      }
      data-testid="conditions-summary"
    >
      {conditions.length > 0 && (
        <div
          className={strip ? "flex items-center gap-1.5" : "flex flex-wrap items-center gap-x-1.5"}
          role="group"
          aria-label="Conditions — saved to your account; tap one to change them"
          data-testid="summary-group-conditions"
        >
          {!strip && (
            <span
              className="fa-overline text-fa-frost-dim"
              title="Saved to your account — these apply everywhere. Tap a chip to change them."
            >
              Conditions
            </span>
          )}
          {shown.map((chip) => {
            const face = conditionChipFace(chip);
            return (
              <button
                key={chip.key}
                type="button"
                onClick={onOpenEditor}
                aria-label={`${face.spoken} — change conditions`}
                title={`${face.spoken} — tap to change your conditions`}
                className={`group ${target}`}
                data-testid="condition-chip"
                data-sign={face.sign ?? "none"}
              >
                <span className={pill}>
                  {face.sign === "include" && <Plus className="h-3 w-3 shrink-0" aria-hidden />}
                  {face.sign === "exclude" && <Minus className="h-3 w-3 shrink-0" aria-hidden />}
                  <span className="first-letter:uppercase inline-block">{face.text}</span>
                </span>
              </button>
            );
          })}
          {conditions.length > limit && (
            <button
              type="button"
              onClick={() => setExpanded((v) => !v)}
              aria-expanded={expanded}
              className={`${target} px-1 fa-caption text-fa-frost-dim underline-offset-2 hover:text-fa-frost hover:underline`}
              data-testid="conditions-more"
            >
              {expanded ? "Show fewer" : `+${hidden} more`}
            </button>
          )}
        </div>
      )}

      {views.length > 0 && (
        <div className="flex flex-wrap items-center gap-x-1.5" data-testid="summary-group-browse">
          <span
            className="fa-overline text-fa-frost-dim"
            title="This browser only — set by the Movies/TV and availability toggles, and not saved to your account."
          >
            Viewing on this device
          </span>
          {views.map((chip) => (
            <span
              key={chip.key}
              // Dashed: a different kind of filter, said by shape as well as by the group label.
              className="inline-flex items-center gap-0.5 rounded-full border border-dashed border-fa-edge bg-fa-glass pl-2 fa-caption text-fa-frost"
              data-testid={chip.key}
            >
              {chip.label}
              {onBrowseChange && browse && chip.clear && (
                <button
                  type="button"
                  onClick={() => onBrowseChange({ ...browse, ...chip.clear })}
                  aria-label={`Clear “${chip.label}” — show everything again`}
                  title={`Clear “${chip.label}”`}
                  data-testid={`clear-${chip.key}`}
                  className="inline-flex min-h-11 min-w-11 items-center justify-center rounded-full opacity-70 transition hover:opacity-100 focus-visible:opacity-100 [@media(pointer:fine)]:min-h-0 [@media(pointer:fine)]:min-w-0 [@media(pointer:fine)]:p-1"
                >
                  <X className="h-3 w-3" />
                </button>
              )}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}
