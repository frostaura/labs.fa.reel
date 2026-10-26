import { useEffect, useRef, useState } from "react";
import { SlidersHorizontal } from "lucide-react";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "../ui/dialog";
import ConditionsEditor from "../settings/ConditionsEditor";
import { CONDITIONS_SCOPE_COPY } from "../../lib/conditionsCopy";
import { EDITOR, relaxationLabel, ruleTarget } from "../../lib/askReel/relaxation";
import type { AskKnockout } from "../../lib/askReelStream";

/** How long the dialog keeps looking for the rule's control while the editor loads its filters. */
const FIND_BUDGET_MS = 3_000;
const FIND_STEP_MS = 100;

/**
 * One relaxation a none-found answer offers — "Without “English only”: +4" — as a control that opens
 * the Conditions editor ON that rule (WS-2 task 3; FD-2c's default: no "just this once" override,
 * the chip takes you to the rule). It used to be an inert `<li>` naming a number nobody could act on
 * from where they read it.
 *
 * The same editor the feed's Conditions button mounts — one implementation of these rules — with
 * one addition: once it has rendered, the control the rule lives in is scrolled into view and
 * focused, so the reader lands on the genre chip or the language allow-list rather than on the top
 * of a long form. The editor loads its own filters, so the control is looked for until it exists
 * (bounded); a rule with no control of its own lands on the editor.
 */
export default function RelaxationChip({ knockout }: { knockout: AskKnockout }) {
  const [open, setOpen] = useState(false);
  const contentRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const target = ruleTarget(knockout);
    const started = Date.now();
    let timer: ReturnType<typeof setTimeout> | null = null;
    const look = () => {
      const root = contentRef.current;
      const el = root?.querySelector<HTMLElement>(target) ?? (Date.now() - started >= FIND_BUDGET_MS ? root?.querySelector<HTMLElement>(EDITOR) : null);
      if (el) {
        el.scrollIntoView({ block: "center" });
        if (el.matches(EDITOR)) el.setAttribute("tabindex", "-1");
        el.focus({ preventScroll: true });
        el.setAttribute("data-askreel-rule", "focused");
        return;
      }
      if (Date.now() - started < FIND_BUDGET_MS + FIND_STEP_MS) timer = setTimeout(look, FIND_STEP_MS);
    };
    timer = setTimeout(look, 0);
    return () => {
      if (timer !== null) clearTimeout(timer);
    };
  }, [open, knockout]);

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <button
          type="button"
          data-testid="askreel-relax"
          className="fa-caption inline-flex min-h-11 items-center gap-1.5 rounded-full border border-fa-frost/25 px-4 text-fa-frost hover:text-fa-frost-bright hover:border-fa-frost/50 hover:bg-fa-frost/10 transition-colors"
        >
          <SlidersHorizontal className="h-3.5 w-3.5" aria-hidden />
          {relaxationLabel(knockout)}
        </button>
      </DialogTrigger>
      <DialogContent ref={contentRef} className="max-w-2xl max-h-[85vh] overflow-y-auto p-0">
        <DialogHeader>
          <DialogTitle>Conditions for tonight&apos;s picks</DialogTitle>
          <DialogDescription data-testid="askreel-relax-scope">
            {`Turning off “${knockout.label}” would unlock ${knockout.wouldUnlock}. ${CONDITIONS_SCOPE_COPY}`}
          </DialogDescription>
        </DialogHeader>
        <div className="px-6 pb-6">
          <ConditionsEditor
            bare
            saveLabel="Save conditions"
            testIds={{ editor: "conditions-editor", locked: "conditions-locked", loading: "conditions-loading" }}
            onSaved={() => setOpen(false)}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
