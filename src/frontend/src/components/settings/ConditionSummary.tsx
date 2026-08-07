import { summaryChips, type ChipTone, type FiltersLike } from "../../lib/pickConditions";

const TONE: Record<ChipTone, string> = {
  include: "border-fa-success/50 bg-fa-success/15 text-fa-success",
  exclude: "border-fa-danger/50 bg-fa-danger/15 text-fa-danger",
  neutral: "border-fa-edge bg-fa-glass text-fa-frost-dim",
};

/**
 * Everything currently narrowing the feed, as chips — the answer to "why does my feed look like
 * this", which a filter set buried on another screen can never give.
 *
 * The include/exclude split is carried by the WORDS ("has crime" / "no documentary") as well as the
 * colour, because this row also renders on the home feed with no legend beside it, and because
 * green-vs-red alone is not a distinction every reader can make.
 */
export default function ConditionSummary({
  filters,
  limit,
  className = "",
}: {
  filters: FiltersLike;
  /** Caps the row on tight surfaces; the overflow is counted rather than silently dropped. */
  limit?: number;
  className?: string;
}) {
  const chips = summaryChips(filters);

  if (chips.length === 0) {
    return (
      <p className={`fa-caption text-fa-frost-dim ${className}`} data-testid="conditions-summary-empty">
        Nothing set — everything is fair game.
      </p>
    );
  }

  const shown = limit != null ? chips.slice(0, limit) : chips;
  const hidden = chips.length - shown.length;

  return (
    <div className={`flex flex-wrap items-center gap-1.5 ${className}`} data-testid="conditions-summary">
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
      {hidden > 0 && <span className="fa-caption text-fa-frost-dim">+{hidden} more</span>}
    </div>
  );
}
