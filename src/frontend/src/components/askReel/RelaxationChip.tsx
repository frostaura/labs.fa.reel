import { useMemo, useState } from "react";
import { SlidersHorizontal } from "lucide-react";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "../ui/dialog";
import ConditionsEditor from "../settings/ConditionsEditor";
import PillButton from "./PillButton";
import { CONDITIONS_SCOPE_COPY } from "../../lib/conditionsCopy";
import { relaxationLabel } from "../../lib/askReel/relaxation";
import type { RuleFocus } from "../../lib/pickConditions";
import type { AskKnockout } from "../../lib/askReelStream";

/**
 * One relaxation a none-found answer offers — "Without “English only”: +4" — as a control that opens
 * the Conditions editor ON that rule (WS-2 task 3; FD-2c's default: no "just this once" override,
 * the chip takes you to the rule). It used to be an inert `<li>` naming a number nobody could act on
 * from where they read it.
 *
 * The same editor the feed's Conditions button mounts — one implementation of these rules — opened
 * with `focusRule`, so the editor itself scrolls to and focuses the control the rule lives in (the
 * genre's own ✕, the language allow-list) once its filters and chips have rendered, and lands on
 * its own top for a rule with no control. R1-33: this chip used to find the control itself by
 * polling the editor's test ids for up to three seconds — a second, guessing implementation of
 * something only the editor can know.
 *
 * The rule is memoised on its two strings: the editor applies a `focusRule` once per object, and a
 * fresh literal on every render would pull focus back to the rule while the reader is editing.
 */
export default function RelaxationChip({ knockout }: { knockout: AskKnockout }) {
  const [open, setOpen] = useState(false);
  const { kind, value } = knockout;
  const focusRule = useMemo<RuleFocus>(() => ({ kind, value }), [kind, value]);

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <PillButton tone="accent" data-testid="askreel-relax" className="whitespace-normal py-2 text-left">
          <SlidersHorizontal className="h-3.5 w-3.5 shrink-0" aria-hidden />
          {relaxationLabel(knockout)}
        </PillButton>
      </DialogTrigger>
      <DialogContent className="max-w-2xl max-h-[85vh] overflow-y-auto p-0">
        <DialogHeader>
          <DialogTitle>Conditions for tonight&apos;s picks</DialogTitle>
          <DialogDescription data-testid="askreel-relax-scope">
            {`Turning off “${knockout.label}” would unlock ${knockout.wouldUnlock}. ${CONDITIONS_SCOPE_COPY}`}
          </DialogDescription>
        </DialogHeader>
        <div className="px-6 pb-6">
          <ConditionsEditor
            bare
            focusRule={focusRule}
            saveLabel="Save conditions"
            testIds={{ editor: "conditions-editor", locked: "conditions-locked", loading: "conditions-loading" }}
            onSaved={() => setOpen(false)}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
