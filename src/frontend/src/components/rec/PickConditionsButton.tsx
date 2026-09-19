import { useState } from "react";
import { SlidersHorizontal } from "lucide-react";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "../ui/dialog";
import ConditionsEditor from "../settings/ConditionsEditor";
import ConditionSummary from "../settings/ConditionSummary";
import { useGetFiltersQuery } from "../../store/api";
import { summaryChips } from "../../lib/pickConditions";
import type { BrowseFilter } from "../../lib/browseFilter";

/**
 * The conditions editor, reachable from the feed itself.
 *
 * These controls existed only in Settings, two screens from the picks they govern — so the founder's
 * standing `excludeGenres: ["documentary"]` was invisible at exactly the moment it mattered, when
 * looking at a feed and wondering why it looked like that. The count on the button is the smallest
 * honest version of that answer; opening it shows the chips in full.
 *
 * Composes the shared `ui/dialog` primitives rather than modifying them, and mounts the same
 * `ConditionsEditor` that Settings renders — there is one implementation of these rules.
 */
export default function PickConditionsButton({ onSaved }: { onSaved?: () => void }) {
  const [open, setOpen] = useState(false);
  const { data: filters } = useGetFiltersQuery();
  const activeCount = summaryChips(filters).length;

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <button
          type="button"
          title="Set the conditions for tonight's picks — genres, ratings, years, languages"
          data-testid="open-conditions"
          className="fa-overline inline-flex items-center gap-1.5 rounded-lg border border-fa-edge bg-fa-glass px-2.5 py-2 text-fa-frost transition hover:border-fa-frost/40 hover:bg-fa-glass-strong hover:text-fa-frost-bright"
        >
          <SlidersHorizontal className="h-3.5 w-3.5" />
          Conditions
          {/* Counts SAVED conditions only — the browse toggles are a per-device view filter this
              dialog cannot edit, so counting them here would send the user in to look for a control
              that is not there. The chip row therefore legitimately carries more chips than this
              badge counts; the label spells out which ones it means. */}
          {activeCount > 0 && (
            <span
              className="inline-flex h-4 min-w-4 items-center justify-center rounded-full bg-fa-frost/20 px-1 text-fa-frost-bright tabular-nums"
              data-testid="conditions-count"
              aria-label={`${activeCount} saved condition${activeCount === 1 ? "" : "s"}`}
              title={`${activeCount} saved condition${activeCount === 1 ? "" : "s"} — browse toggles are counted separately`}
            >
              {activeCount}
            </span>
          )}
        </button>
      </DialogTrigger>
      {/* Taller and wider than the default dialog: this is a form, not a confirmation, and the
          genre grid alone is 24 chips. The body scrolls so the phone case still reaches Save. */}
      <DialogContent className="max-w-2xl max-h-[85vh] overflow-y-auto p-0">
        <DialogHeader>
          <DialogTitle>Conditions for tonight&apos;s picks</DialogTitle>
          <DialogDescription>
            Airtight — these apply to the hero, every row and all search. Saving re-cuts the feed
            straight away.
          </DialogDescription>
        </DialogHeader>
        <div className="px-6 pb-6">
          <ConditionsEditor
            bare
            saveLabel="Save & re-cut picks"
            testIds={{ editor: "conditions-editor", locked: "conditions-locked", loading: "conditions-loading" }}
            onSaved={() => {
              onSaved?.();
              setOpen(false);
            }}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}

/**
 * The one-line "this is why your feed looks like this", for the feed header. Separate from the
 * button so the summary can sit under the page title at full width instead of being squeezed into
 * a control strip.
 *
 * Takes the browse filter as well as the saved conditions, because the header claims to summarise
 * everything shaping the feed and a "Movies only" toggle shapes it every bit as visibly as a genre
 * exclusion. Passing the setter through makes those chips dismissible from here.
 */
export function PickConditionsSummary({
  className,
  browse,
  onBrowseChange,
}: {
  className?: string;
  browse?: BrowseFilter | null;
  onBrowseChange?: (next: BrowseFilter) => void;
}) {
  const { data: filters } = useGetFiltersQuery();
  // Waits for the real payload rather than rendering against the defaults: `normalizeFilters`
  // fills an absent payload out to `languages: ["en"]`, so an in-flight query would briefly assert
  // an "English only" condition the account may not actually have.
  if (!filters || summaryChips(filters, browse).length === 0) return null;
  return (
    <ConditionSummary
      filters={filters}
      browse={browse}
      onBrowseChange={onBrowseChange}
      limit={6}
      className={className}
    />
  );
}
