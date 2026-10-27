import { useState } from "react";
import { SlidersHorizontal } from "lucide-react";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "../ui/dialog";
import ConditionsEditor from "../settings/ConditionsEditor";
import { useGetFiltersQuery } from "../../store/api";
import { summaryChips } from "../../lib/pickConditions";
import { CONDITIONS_SCOPE_COPY } from "../../lib/conditionsCopy";
import { Button } from "../ui/button";

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
 *
 * Optionally controlled (`open` / `onOpenChange`), so Home's condition chips open the same dialog,
 * and optionally trigger-less (`trigger={false}`) for a phone header where the chips themselves are
 * the control. Uncontrolled with its own button, as Ask Reel mounts it, is the default.
 */
export default function PickConditionsButton({
  onSaved,
  open: controlledOpen,
  onOpenChange,
  trigger = true,
}: {
  onSaved?: () => void;
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  trigger?: boolean;
}) {
  const [ownOpen, setOwnOpen] = useState(false);
  const open = controlledOpen ?? ownOpen;
  const setOpen = onOpenChange ?? setOwnOpen;
  const { data: filters } = useGetFiltersQuery();
  // Nothing counted until the real payload arrives: `summaryChips` of an absent payload normalises
  // to the "English only" default, so the button used to say "1" for a moment on an account with
  // none, then drop the badge — and its height with it (HREV1-D04).
  const activeCount = filters ? summaryChips(filters).length : 0;

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      {trigger && (
        <DialogTrigger asChild>
          <Button
            title="Set the conditions for tonight's picks — genres, ratings, years, languages"
            data-testid="open-conditions"
            className="h-auto min-h-11 gap-1.5 rounded-lg bg-fa-glass px-2.5 py-2 text-fa-frost [@media(pointer:fine)]:min-h-0 hover:border-fa-frost/40 hover:bg-fa-glass-strong hover:text-fa-frost-bright"
          >
            <SlidersHorizontal className="h-3.5 w-3.5" aria-hidden />
            {/* As tall as the count badge, so the badge arriving with the conditions changes the
                button's width only, never the height of the line it sits in. */}
            <span className="fa-overline inline-flex h-4 items-center">Conditions</span>
            {/* Counts SAVED conditions only — the browse toggles are a per-device view filter this
                dialog cannot edit, so counting them here would send the user in to look for a control
                that is not there. The chip row therefore legitimately carries more chips than this
                badge counts; the label spells out which ones it means. */}
            {activeCount > 0 && (
              <span
                className="fa-overline inline-flex h-4 min-w-4 items-center justify-center rounded-full bg-fa-frost/20 px-1 text-fa-frost-bright tabular-nums"
                data-testid="conditions-count"
                aria-label={`${activeCount} saved condition${activeCount === 1 ? "" : "s"}`}
                title={`${activeCount} saved condition${activeCount === 1 ? "" : "s"} — browse toggles are counted separately`}
              >
                {activeCount}
              </span>
            )}
          </Button>
        </DialogTrigger>
      )}
      {/* Taller and wider than the default dialog: this is a form, not a confirmation, and the
          genre grid alone is 24 chips. The body scrolls so the phone case still reaches Save. */}
      <DialogContent className="max-w-2xl max-h-[85vh] overflow-y-auto p-0">
        <DialogHeader>
          <DialogTitle>Conditions for tonight&apos;s picks</DialogTitle>
          <DialogDescription data-testid="conditions-scope">
            {CONDITIONS_SCOPE_COPY} Saving re-cuts the feed straight away.
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
