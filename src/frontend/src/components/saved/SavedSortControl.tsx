import { SAVED_SORTS, SAVED_SORT_META, type SavedSort } from "../../lib/savedQueue";

/**
 * The queue's order, as four explicit choices rather than a dropdown. Each carries the promise it
 * makes as a tooltip, because "Tonight" is a compound rule (streaming now → best → shortest) that
 * a user would otherwise have to reverse-engineer from the cards.
 */
export default function SavedSortControl({
  value,
  onChange,
}: {
  value: SavedSort;
  onChange: (next: SavedSort) => void;
}) {
  return (
    <div
      role="group"
      aria-label="Sort the queue"
      className="inline-flex flex-wrap items-center gap-1 rounded-lg border border-fa-edge bg-fa-glass p-1"
      data-testid="saved-sort"
    >
      {SAVED_SORTS.map((sort) => {
        const active = sort === value;
        return (
          <button
            key={sort}
            type="button"
            onClick={() => onChange(sort)}
            aria-pressed={active}
            title={SAVED_SORT_META[sort].hint}
            data-testid={`saved-sort-${sort}`}
            className={`fa-overline rounded-md px-2.5 py-1.5 transition ${
              active
                ? "bg-fa-frost/15 text-fa-frost-bright"
                : "text-fa-frost-dim hover:bg-fa-glass-strong hover:text-fa-frost"
            }`}
          >
            {SAVED_SORT_META[sort].label}
          </button>
        );
      })}
    </div>
  );
}
